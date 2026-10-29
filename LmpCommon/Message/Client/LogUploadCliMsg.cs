using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.LogUpload;
using LmpCommon.Message.Client.Base;
using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Client
{
    /// <summary>
    /// "Send logs to host" upload, client to server. Rides its own ReliableOrdered channel
    /// (<see cref="LogUploadLimits.ClientChannel"/>) so a large upload can never hold back another channel's messages;
    /// the client also paces chunks so the upload never floods the connection.
    /// </summary>
    public class LogUploadCliMsg : CliMsgBase<LogUploadBaseMsgData>
    {
        /// <inheritdoc />
        internal LogUploadCliMsg() { }

        /// <inheritdoc />
        public override string ClassName { get; } = nameof(LogUploadCliMsg);

        /// <inheritdoc />
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)LogUploadMessageType.Begin] = typeof(LogUploadBeginMsgData),
            [(ushort)LogUploadMessageType.Chunk] = typeof(LogUploadChunkMsgData),
            [(ushort)LogUploadMessageType.Cancel] = typeof(LogUploadCancelMsgData),
        };

        public override ClientMessageType MessageType => ClientMessageType.LogUpload;
        protected override int DefaultChannel => LogUploadLimits.ClientChannel;
        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
