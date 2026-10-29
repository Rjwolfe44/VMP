using Lidgren.Network;
using LmpCommon.LogUpload;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>
    /// Client asks to upload one zip of <see cref="TotalBytes"/> bytes split into <see cref="ChunkCount"/> chunks of
    /// <see cref="ChunkSize"/> bytes (the last one may be shorter). The server answers Accepted or Refused. No file name
    /// or path is sent: the server names the stored file itself.
    /// </summary>
    public class LogUploadBeginMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadBeginMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Begin;

        public LogUploadFileKind FileKind = LogUploadFileKind.ClientLogsZip;
        public int TotalBytes;
        public int ChunkSize;
        public int ChunkCount;

        /// <summary>Why the client sends this bundle; anything but <see cref="LogUploadReason.Manual"/> is an automatic report.</summary>
        public LogUploadReason Reason = LogUploadReason.Manual;

        public override string ClassName { get; } = nameof(LogUploadBeginMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write((byte)FileKind);
            lidgrenMsg.Write(TotalBytes);
            lidgrenMsg.Write(ChunkSize);
            lidgrenMsg.Write(ChunkCount);
            lidgrenMsg.Write((byte)Reason);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            FileKind = (LogUploadFileKind)lidgrenMsg.ReadByte();
            TotalBytes = lidgrenMsg.ReadInt32();
            ChunkSize = lidgrenMsg.ReadInt32();
            ChunkCount = lidgrenMsg.ReadInt32();
            Reason = (LogUploadReason)lidgrenMsg.ReadByte();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(byte) + sizeof(int) * 3 + sizeof(byte);
        }
    }
}
