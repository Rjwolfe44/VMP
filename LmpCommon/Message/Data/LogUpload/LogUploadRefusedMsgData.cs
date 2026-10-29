using Lidgren.Network;
using LmpCommon.LogUpload;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>Server refused the upload, or aborted it part-way (bad chunk, timeout). <see cref="Detail"/> is a short server-written explanation.</summary>
    public class LogUploadRefusedMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadRefusedMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Refused;

        public LogUploadRefusalReason Reason;
        public string Detail = string.Empty;

        public override string ClassName { get; } = nameof(LogUploadRefusedMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write((byte)Reason);
            lidgrenMsg.Write(Truncate(Detail, LogUploadLimits.MaxReplyTextLength));
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            Reason = (LogUploadRefusalReason)lidgrenMsg.ReadByte();
            Detail = ReadBoundedString(lidgrenMsg, LogUploadLimits.MaxReplyTextLength);
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(byte) + Truncate(Detail, LogUploadLimits.MaxReplyTextLength).GetByteCount();
        }
    }
}
