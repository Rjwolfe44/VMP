using Lidgren.Network;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpCommon.Enums;
using LmpCommon.LogUpload;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Interface;
using LmpCommon.Time;
using System;

namespace LmpClient.Systems.LogUpload
{
    public class LogUploadMessageSender : SubSystem<LogUploadSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<LogUploadCliMsg>(msg)));
        }

        public void SendBegin(int uploadId, int totalBytes, int chunkSize, int chunkCount, LogUploadReason reason = LogUploadReason.Manual)
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<LogUploadBeginMsgData>();
            msgData.UploadId = uploadId;
            msgData.FileKind = LogUploadFileKind.ClientLogsZip;
            msgData.TotalBytes = totalBytes;
            msgData.ChunkSize = chunkSize;
            msgData.ChunkCount = chunkCount;
            msgData.Reason = reason;
            SendMessage(msgData);
        }

        public void SendCancel(int uploadId)
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<LogUploadCancelMsgData>();
            msgData.UploadId = uploadId;
            SendMessage(msgData);
        }

        /// <summary>
        /// Hands one chunk straight to Lidgren from the upload thread (Lidgren's SendMessage is thread-safe) instead of
        /// going through <see cref="NetworkSender"/>'s queue. That way the pacing loop sees the chunk in the channel's
        /// send window the moment this returns and never queues a burst of chunks it cannot see.
        /// </summary>
        public bool SendChunkNow(int uploadId, int chunkIndex, int chunkCount, int totalBytes, byte[] buffer, int count)
        {
            var connection = NetworkMain.ClientConnection;
            if (connection == null || connection.Status != NetPeerStatus.Running || connection.ServerConnection == null ||
                MainSystem.NetworkState < ClientState.Handshaked)
                return false;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<LogUploadChunkMsgData>();
            msgData.UploadId = uploadId;
            msgData.ChunkIndex = chunkIndex;
            msgData.ChunkCount = chunkCount;
            msgData.TotalBytes = totalBytes;
            if (msgData.Data == null || msgData.Data.Length < count)
                msgData.Data = new byte[count];
            Buffer.BlockCopy(buffer, 0, msgData.Data, 0, count);
            msgData.NumBytes = count;

            var msg = MessageFactory.CreateNew<LogUploadCliMsg>(msgData);
            try
            {
                msg.Data.SentTime = LunaNetworkTime.UtcNow.Ticks;
                var outgoing = connection.CreateMessage(msg.GetMessageSize());
                msg.Serialize(outgoing);
                var result = connection.SendMessage(outgoing, msg.NetDeliveryMethod, msg.Channel);
                return result == NetSendResult.Sent || result == NetSendResult.Queued;
            }
            finally
            {
                msg.Recycle();
            }
        }

        /// <summary>
        /// Window slots of the upload channel still waiting for an ack (or not yet sent). Each fragment of a chunk takes
        /// one slot; -1 when there is no connection.
        /// </summary>
        public static int UploadChannelSlotsInUse(out int windowSize)
        {
            windowSize = 0;
            var serverConnection = NetworkMain.ClientConnection?.ServerConnection;
            if (serverConnection == null) return -1;

            serverConnection.GetSendQueueInfo(NetDeliveryMethod.ReliableOrdered, LogUploadLimits.ClientChannel, out windowSize, out var freeSlots);
            return windowSize - freeSlots;
        }
    }
}
