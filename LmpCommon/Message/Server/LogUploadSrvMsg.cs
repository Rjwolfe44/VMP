using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Server.Base;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Server
{
    /// <summary>
    /// Replies to a log upload (accepted / done / refused). ReliableUnordered: a handful of tiny messages that must
    /// arrive but need no ordering, so they can never queue behind a stalled ReliableOrdered channel (every
    /// server-to-client ordered channel is already taken by other messages or PersistentSync recovery).
    /// </summary>
    public class LogUploadSrvMsg : SrvMsgBase<LogUploadBaseMsgData>
    {
        /// <inheritdoc />
        internal LogUploadSrvMsg() { }

        /// <inheritdoc />
        public override string ClassName { get; } = nameof(LogUploadSrvMsg);

        /// <inheritdoc />
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)LogUploadMessageType.Accepted] = typeof(LogUploadAcceptedMsgData),
            [(ushort)LogUploadMessageType.Done] = typeof(LogUploadDoneMsgData),
            [(ushort)LogUploadMessageType.Refused] = typeof(LogUploadRefusedMsgData),
            [(ushort)LogUploadMessageType.Request] = typeof(LogUploadRequestMsgData),
        };

        public override ServerMessageType MessageType => ServerMessageType.LogUpload;

        // Unordered delivery always uses channel 0 (see MessageBase.Channel).
        protected override int DefaultChannel => 0;
        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableUnordered;
    }
}
