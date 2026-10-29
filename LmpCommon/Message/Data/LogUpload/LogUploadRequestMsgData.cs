using Lidgren.Network;
using LmpCommon.LogUpload;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System.Text;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>
    /// The server host asks this player's game to send a diagnostics bundle (server to client). The client answers, when its
    /// player allows automatic diagnostics, with a normal upload labelled <see cref="LogUploadReason.Requested"/>; the server
    /// only accepts such an upload while a request for that player is pending. Wire layout: int32 request id, then a string
    /// note of at most <see cref="LogUploadLimits.MaxRequestNoteLength"/> characters. The id travels in the base class
    /// <see cref="LogUploadBaseMsgData.UploadId"/> field and is exposed as <see cref="RequestId"/>.
    /// </summary>
    public class LogUploadRequestMsgData : LogUploadBaseMsgData
    {
        /// <inheritdoc />
        internal LogUploadRequestMsgData() { }
        public override LogUploadMessageType LogUploadMessageType => LogUploadMessageType.Request;

        /// <summary>Server-chosen id of this request; only used to match log lines on both sides.</summary>
        public int RequestId
        {
            get { return UploadId; }
            set { UploadId = value; }
        }

        /// <summary>Optional short reason from the host, shown in the report's info file. Never trusted: always passed through <see cref="CleanNote"/>.</summary>
        public string Note = string.Empty;

        public override string ClassName { get; } = nameof(LogUploadRequestMsgData);

        /// <summary>Control characters removed, surrounding spaces trimmed and the text cut to <see cref="LogUploadLimits.MaxRequestNoteLength"/> characters.</summary>
        public static string CleanNote(string note)
        {
            if (string.IsNullOrEmpty(note)) return string.Empty;

            var builder = new StringBuilder(note.Length < LogUploadLimits.MaxRequestNoteLength ? note.Length : LogUploadLimits.MaxRequestNoteLength);
            foreach (var c in note)
            {
                if (char.IsControl(c)) continue;
                builder.Append(c);
            }

            var text = builder.ToString().Trim();
            return text.Length <= LogUploadLimits.MaxRequestNoteLength ? text : text.Substring(0, LogUploadLimits.MaxRequestNoteLength).TrimEnd();
        }

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            lidgrenMsg.Write(CleanNote(Note));
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            Note = CleanNote(ReadBoundedString(lidgrenMsg, LogUploadLimits.MaxRequestNoteLength));
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + CleanNote(Note).GetByteCount();
        }
    }
}
