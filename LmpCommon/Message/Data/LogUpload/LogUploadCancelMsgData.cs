using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>Client gives up on <see cref="LogUploadBaseMsgData.UploadId"/>; the server deletes the partial file.</summary>
    public class LogUploadCancelMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadCancelMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Cancel;

        public override string ClassName { get; } = nameof(LogUploadCancelMsgData);
    }
}
