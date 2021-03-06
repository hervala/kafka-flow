﻿namespace KafkaFlow.Consumers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Confluent.Kafka;
    using KafkaFlow.Configuration;

    internal class KafkaConsumer : IKafkaConsumer
    {
        private readonly IConsumerManager consumerManager;
        private readonly ILogHandler logHandler;
        private readonly IConsumerWorkerPool consumerWorkerPool;
        private readonly CancellationToken busStopCancellationToken;

        private readonly ConsumerBuilder<byte[], byte[]> consumerBuilder;

        private IConsumer<byte[], byte[]> consumer;

        private CancellationTokenSource stopCancellationTokenSource;
        private Task backgroundTask;

        public KafkaConsumer(
            ConsumerConfiguration configuration,
            IConsumerManager consumerManager,
            ILogHandler logHandler,
            IConsumerWorkerPool consumerWorkerPool,
            CancellationToken busStopCancellationToken)
        {
            this.Configuration = configuration;
            this.consumerManager = consumerManager;
            this.logHandler = logHandler;
            this.consumerWorkerPool = consumerWorkerPool;
            this.busStopCancellationToken = busStopCancellationToken;

            var kafkaConfig = configuration.GetKafkaConfig();

            this.consumerBuilder = new ConsumerBuilder<byte[], byte[]>(kafkaConfig);

            this.consumerBuilder
                .SetPartitionsAssignedHandler((consumer, partitions) => this.OnPartitionAssigned(partitions))
                .SetPartitionsRevokedHandler((consumer, partitions) => this.OnPartitionRevoked(partitions))
                .SetErrorHandler(
                    (p, error) =>
                    {
                        if (error.IsFatal)
                        {
                            this.logHandler.Error("Kafka Consumer Fatal Error", null, new { Error = error });
                        }
                        else
                        {
                            this.logHandler.Warning("Kafka Consumer Error", new { Error = error });
                        }
                    })
                .SetStatisticsHandler(
                    (consumer, statistics) =>
                    {
                        foreach (var handler in configuration.StatisticsHandlers)
                        {
                            handler.Invoke(statistics);
                        }
                    });
        }

        public ConsumerConfiguration Configuration { get; }

        public IReadOnlyList<string> Subscription => this.consumer?.Subscription;

        public IReadOnlyList<TopicPartition> Assignment => this.consumer?.Assignment;

        public IKafkaConsumerFlowManager FlowManager { get; private set; }

        public string MemberId => this.consumer?.MemberId;

        public string ClientInstanceName => this.consumer?.Name;

        private void OnPartitionRevoked(IReadOnlyCollection<TopicPartitionOffset> topicPartitions)
        {
            this.logHandler.Warning(
                "Partitions revoked",
                this.GetConsumerLogInfo(topicPartitions.Select(x => x.TopicPartition)));

            this.consumerWorkerPool.StopAsync().GetAwaiter().GetResult();
        }

        private void OnPartitionAssigned(IReadOnlyCollection<TopicPartition> partitions)
        {
            this.logHandler.Info(
                "Partitions assigned",
                this.GetConsumerLogInfo(partitions));

            this.consumerWorkerPool
                .StartAsync(
                    this,
                    partitions,
                    this.stopCancellationTokenSource.Token)
                .GetAwaiter()
                .GetResult();
        }

        private object GetConsumerLogInfo(IEnumerable<TopicPartition> partitions) => new
        {
            this.Configuration.GroupId,
            this.Configuration.ConsumerName,
            Topics = partitions
                .GroupBy(x => x.Topic)
                .Select(
                    x => new
                    {
                        x.First().Topic,
                        PartitionsCount = x.Count(),
                        Partitions = x.Select(y => y.Partition.Value)
                    })
        };

        public Task StartAsync()
        {
            this.stopCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(this.busStopCancellationToken);

            this.CreateConsumerAndBackgroundTask();

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            await this.consumerWorkerPool.StopAsync().ConfigureAwait(false);

            if (this.stopCancellationTokenSource.Token.CanBeCanceled)
            {
                this.stopCancellationTokenSource.Cancel();
            }

            await this.backgroundTask.ConfigureAwait(false);
            this.backgroundTask.Dispose();
        }

        private void CreateConsumerAndBackgroundTask()
        {
            this.consumer = this.consumerBuilder.Build();
            this.FlowManager = new KafkaConsumerFlowManager(
                this.consumer,
                this.stopCancellationTokenSource.Token,
                this.logHandler);

            this.consumerManager.AddOrUpdate(
                new MessageConsumer(
                    this,
                    this.consumerWorkerPool,
                    this.logHandler));

            this.consumer.Subscribe(this.Configuration.Topics);

            this.backgroundTask = Task.Factory.StartNew(
                async () =>
                {
                    using (this.consumer)
                    {
                        while (!this.stopCancellationTokenSource.IsCancellationRequested)
                        {
                            try
                            {
                                var message = this.consumer.Consume(this.stopCancellationTokenSource.Token);

                                await this.consumerWorkerPool
                                    .EnqueueAsync(message, this.stopCancellationTokenSource.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Ignores the exception
                            }
                            catch (KafkaException ex) when (ex.Error.IsFatal)
                            {
                                this.logHandler.Error(
                                    "Kafka fatal error occurred. Trying to restart in 5 seconds",
                                    ex,
                                    null);

                                await this.consumerWorkerPool.StopAsync().ConfigureAwait(false);
                                _ = Task
                                    .Delay(5000, this.stopCancellationTokenSource.Token)
                                    .ContinueWith(t => this.CreateConsumerAndBackgroundTask());

                                break;
                            }
                            catch (Exception ex)
                            {
                                this.logHandler.Warning(
                                    "Error consuming message from Kafka",
                                    ex);
                            }
                        }

                        this.consumer.Close();
                    }

                    this.consumer = null;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public Offset GetPosition(TopicPartition topicPartition) =>
            this.consumer.Position(topicPartition);

        public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition) =>
            this.consumer.GetWatermarkOffsets(topicPartition);

        public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout) =>
            this.consumer.QueryWatermarkOffsets(topicPartition, timeout);

        public List<TopicPartitionOffset> OffsetsForTimes(
            IEnumerable<TopicPartitionTimestamp> topicPartitions,
            TimeSpan timeout) =>
            this.consumer.OffsetsForTimes(topicPartitions, timeout);

        public void Commit(IEnumerable<TopicPartitionOffset> offsetsValues)
        {
            this.consumer.Commit(offsetsValues);
        }
    }
}
