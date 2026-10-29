using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.LogUpload
{
    /// <summary>
    /// Base of every log upload message. Every message carries the client-chosen <see cref="UploadId"/> so replies and
    /// chunks can be matched to one upload.
    /// </summary>
    public abstract class LogUploadBaseMsgData : MessageData
    {
        /// <inheritdoc />
        internal LogUploadBaseMsgData() { }
        public override ushort SubType => (ushort)(int)LogUploadMessageType;
        public virtual LogUploadMessageType LogUploadMessageType => throw new NotImplementedException();

        public int UploadId;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(UploadId);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            UploadId = lidgrenMsg.ReadInt32();
        }

        internal override int InternalGetMessageSize()
        {
            return sizeof(int);
        }

        /// <summary>
        /// Reads a Lidgren string and throws when it is longer than <paramref name="maxLength"/>, so a peer cannot make
        /// us keep or display an arbitrarily long text.
        /// </summary>
        internal static string ReadBoundedString(NetIncomingMessage lidgrenMsg, int maxLength)
        {
            var value = lidgrenMsg.ReadString() ?? string.Empty;
            if (value.Length > maxLength)
                throw new InvalidOperationException($"Log upload text field is {value.Length} chars; the limit is {maxLength}");
            return value;
        }

        internal static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
