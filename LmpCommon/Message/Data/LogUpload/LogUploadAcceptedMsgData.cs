using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>Server accepted the Begin request; the client may now stream the chunks.</summary>
    public class LogUploadAcceptedMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadAcceptedMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Accepted;

        public override string ClassName { get; } = nameof(LogUploadAcceptedMsgData);
    }
}
