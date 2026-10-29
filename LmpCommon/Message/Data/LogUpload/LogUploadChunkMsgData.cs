using Lidgren.Network;
using LmpCommon.LogUpload;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>
    /// One piece of the zip. <see cref="ChunkCount"/> and <see cref="TotalBytes"/> repeat the Begin values so the
    /// server can check every chunk against the upload it belongs to. <see cref="Data"/> may be longer than
    /// <see cref="NumBytes"/> (pooled buffer); only the first <see cref="NumBytes"/> bytes are payload.
    /// </summary>
    public class LogUploadChunkMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadChunkMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Chunk;

        public int ChunkIndex;
        public int ChunkCount;
        public int TotalBytes;
        public int NumBytes;
        public byte[] Data = new byte[0];

        public override string ClassName { get; } = nameof(LogUploadChunkMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            if (NumBytes < 0 || NumBytes > LogUploadLimits.MaxChunkBytes || Data == null || NumBytes > Data.Length)
                throw new InvalidOperationException($"Log upload chunk has an invalid length {NumBytes}");

            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write(ChunkIndex);
            lidgrenMsg.Write(ChunkCount);
            lidgrenMsg.Write(TotalBytes);

            // Zip data is already compressed; write it as-is.
            lidgrenMsg.Write(NumBytes);
            lidgrenMsg.Write(Data, 0, NumBytes);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            ChunkIndex = lidgrenMsg.ReadInt32();
            ChunkCount = lidgrenMsg.ReadInt32();
            TotalBytes = lidgrenMsg.ReadInt32();

            var numBytes = lidgrenMsg.ReadInt32();
            // Validate before allocating: the length comes from the peer. Lidgren does not bounds-check reads in release
            // builds, so also make sure the bytes are really in this message.
            var remainingBytes = (lidgrenMsg.LengthBits - lidgrenMsg.Position) / 8;
            if (numBytes < 0 || numBytes > LogUploadLimits.MaxChunkBytes || numBytes > remainingBytes)
                throw new InvalidOperationException($"Log upload chunk declares {numBytes} bytes ({remainingBytes} present, limit {LogUploadLimits.MaxChunkBytes})");

            NumBytes = numBytes;
            if (Data == null || Data.Length < NumBytes)
                Data = new byte[NumBytes];

            lidgrenMsg.ReadBytes(Data, 0, NumBytes);
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(int) * 4 + NumBytes;
        }
    }
}
