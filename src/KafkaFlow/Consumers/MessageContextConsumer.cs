namespace KafkaFlow.Consumers
{
    using System;
    using System.Threading;
    using Confluent.Kafka;

    internal class MessageContextConsumer : IMessageContextConsumer
    {
        private readonly IKafkaConsumer consumer;
        private readonly IOffsetManager offsetManager;
        private readonly ConsumeResult<byte[], byte[]> kafkaResult;

        public MessageContextConsumer(
            IKafkaConsumer consumer,
            IOffsetManager offsetManager,
            ConsumeResult<byte[], byte[]> kafkaResult,
            CancellationToken workerStopped)
        {
            this.WorkerStopped = workerStopped;
            this.consumer = consumer;
            this.offsetManager = offsetManager;
            this.kafkaResult = kafkaResult;
        }

        public string Name => this.consumer.Configuration.ConsumerName;

        public CancellationToken WorkerStopped { get; }

        public bool ShouldStoreOffset { get; set; } = true;

        public DateTime MessageTimestamp => this.kafkaResult.Message.Timestamp.UtcDateTime;

        public void StoreOffset()
        {
            this.offsetManager.StoreOffset(this.kafkaResult.TopicPartitionOffset);
        }

        public IOffsetsWatermark GetOffsetsWatermark()
        {
            return new OffsetsWatermark(this.consumer.GetWatermarkOffsets(this.kafkaResult.TopicPartition));
        }

        public void Pause()
        {
            this.consumer.FlowManager.Pause(this.consumer.Assignment);
        }

        public void Resume()
        {
            this.consumer.FlowManager.Resume(this.consumer.Assignment);
        }
    }
}
