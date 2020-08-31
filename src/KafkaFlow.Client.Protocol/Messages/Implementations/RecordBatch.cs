namespace KafkaFlow.Client.Protocol.Messages.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using KafkaFlow.Client.Protocol.Streams;

    public class RecordBatch : IRequest, IResponse
    {
        private readonly LinkedList<Record> records = new LinkedList<Record>();

        public long BaseOffset { get; private set; } = 0;

        public int BatchLength { get; private set; }

        public int PartitionLeaderEpoch { get; private set; } = 0;

        public byte Magic { get; private set; } = 2;

        public int Crc { get; private set; }

        public short Attributes { get; private set; } = 0;

        public int LastOffsetDelta { get; private set; }

        public long FirstTimestamp { get; private set; }

        public long MaxTimestamp { get; private set; }

        public long ProducerId { get; private set; } = -1;

        public short ProducerEpoch { get; private set; } = -1;

        public int BaseSequence { get; private set; } = -1;

        public IReadOnlyCollection<Record> Records => this.records;

        public void AddRecord(Record record)
        {
            lock (this.records)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (this.records.Count == 0)
                {
                    this.FirstTimestamp = now;
                    record.OffsetDelta = 0;
                    record.TimestampDelta = 0;
                }
                else
                {
                    record.TimestampDelta = (int) (now - this.FirstTimestamp);
                    record.OffsetDelta = this.LastOffsetDelta = this.records.Count;
                }

                this.MaxTimestamp = now;
                this.records.AddLast(record);
            }
        }

        public void Write(Stream destination)
        {
            // destination.WriteInt32(crcSliceLength + 8 + 4 + 4 + 1 + 4);
            var lengthStartPosition = destination.Position;
            destination.WriteInt32(0); // Length will be filled in the end
            destination.WriteInt64(this.BaseOffset);
            destination.WriteInt32(0); // BatchLength will be filled in the end
            destination.WriteInt32(this.PartitionLeaderEpoch);
            destination.WriteByte(this.Magic);
            destination.WriteInt32(0); // CRC will be filled in the wnd

            var crcPosition = destination.Position;
            destination.WriteInt16(this.Attributes);
            destination.WriteInt32(this.LastOffsetDelta);
            destination.WriteInt64(this.FirstTimestamp);
            destination.WriteInt64(this.MaxTimestamp);
            destination.WriteInt64(this.ProducerId);
            destination.WriteInt16(this.ProducerEpoch);
            destination.WriteInt32(this.BaseSequence);
            destination.WriteArray(this.records);
            var endPosition = destination.Position;

            //Write total length
            var totalLength = (int) (endPosition - lengthStartPosition - 4);
            destination.Position = lengthStartPosition;
            destination.WriteInt32(totalLength);

            //Write batch length
            destination.Position = lengthStartPosition + 4 + 8;
            destination.WriteInt32(this.BatchLength = totalLength - 8 - 4);

            //Write CRC
            var crc = Crc32CHash.Compute((DynamicMemoryStream) destination, (int) crcPosition, (int) (endPosition - crcPosition));
            destination.Position = crcPosition - 4;
            destination.WriteInt32((int) crc);

            destination.Position = endPosition;
        }

        public void Read(Stream source)
        {
            var size = source.ReadInt32();

            if (size == 0)
                return;

            using var tracked = new TrackedStream(source, size);
            this.BaseOffset = tracked.ReadInt64();
            this.BatchLength = tracked.ReadInt32();
            this.PartitionLeaderEpoch = tracked.ReadInt32();
            this.Magic = (byte) tracked.ReadByte();
            this.Crc = tracked.ReadInt32();
            this.Attributes = tracked.ReadInt16();
            this.LastOffsetDelta = tracked.ReadInt32();
            this.FirstTimestamp = tracked.ReadInt64();
            this.MaxTimestamp = tracked.ReadInt64();
            this.ProducerId = tracked.ReadInt64();
            this.ProducerEpoch = tracked.ReadInt16();
            this.BaseSequence = tracked.ReadInt32();

            var totalRecords = tracked.ReadInt32();

            for (var i = 0; i < totalRecords; i++)
            {
                this.records.AddLast(tracked.ReadMessage<Record>());
            }

            tracked.DiscardRemainingData();
        }

        public class Record : IRequest, IResponse
        {
            public int Length { get; private set; }

            public byte Attributes { get; private set; } = 0;

            public int TimestampDelta { get; internal set; }

            public int OffsetDelta { get; internal set; }

            public byte[] Key { get; set; }

            public byte[] Value { get; set; }

            public Header[] Headers { get; set; } = Array.Empty<Header>();

            public void Write(Stream destination)
            {
                using var tmp = new DynamicMemoryStream(MemoryManager.Instance, 256);

                tmp.WriteByte(this.Attributes);
                tmp.WriteVarint(this.TimestampDelta);
                tmp.WriteVarint(this.OffsetDelta);

                if (this.Key is null)
                {
                    tmp.WriteVarint(-1);
                }
                else
                {
                    tmp.WriteVarint(this.Key.Length);
                    tmp.Write(this.Key);
                }

                if (this.Value is null)
                {
                    tmp.WriteVarint(-1);
                }
                else
                {
                    tmp.WriteVarint(this.Value.Length);
                    tmp.Write(this.Value);
                }

                tmp.WriteVarint(this.Headers.Length);
                foreach (var header in this.Headers)
                {
                    tmp.WriteMessage(header);
                }

                destination.WriteVarint(this.Length = Convert.ToInt32(tmp.Length));

                tmp.Position = 0;
                tmp.CopyTo(destination);
            }

            public void Read(Stream source)
            {
                this.Length = source.ReadVarint();
                this.Attributes = (byte) source.ReadByte();
                this.TimestampDelta = source.ReadVarint();
                this.OffsetDelta = source.ReadVarint();
                this.Key = source.ReadBytes(source.ReadVarint());
                this.Value = source.ReadBytes(source.ReadVarint());
                this.Headers = source.ReadArray<Header>(source.ReadVarint());
            }
        }

        public class Header : IRequest, IResponse
        {
            public string Key { get; set; }

            public byte[] Value { get; set; }

            public void Write(Stream destination)
            {
                var keyBytes = Encoding.UTF8.GetBytes(this.Key);

                destination.WriteVarint(keyBytes.Length);
                destination.Write(keyBytes);

                if (this.Value is null)
                {
                    destination.WriteVarint(-1);
                }
                else
                {
                    destination.WriteVarint(this.Value.Length);
                    destination.Write(this.Value);
                }
            }

            public void Read(Stream source)
            {
                this.Key = source.ReadString(source.ReadVarint());
                this.Value = source.ReadBytes(source.ReadVarint());
            }
        }
    }
}