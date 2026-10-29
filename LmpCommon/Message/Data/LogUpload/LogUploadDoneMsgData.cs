using Lidgren.Network;
using LmpCommon.LogUpload;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>Server stored the whole upload as <see cref="StoredName"/> (a name the server generated).</summary>
    public class LogUploadDoneMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadDoneMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Done;

        public long StoredBytes;
        public string StoredName = string.Empty;

        public override string ClassName { get; } = nameof(LogUploadDoneMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write(StoredBytes);
            lidgrenMsg.Write(Truncate(StoredName, LogUploadLimits.MaxReplyTextLength));
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            StoredBytes = lidgrenMsg.ReadInt64();
            StoredName = ReadBoundedString(lidgrenMsg, LogUploadLimits.MaxReplyTextLength);
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(long) + Truncate(StoredName, LogUploadLimits.MaxReplyTextLength).GetByteCount();
        }
    }
}
