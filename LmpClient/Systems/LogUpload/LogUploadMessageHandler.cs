using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System.Collections.Concurrent;

namespace LmpClient.Systems.LogUpload
{
    public class LogUploadMessageHandler : SubSystem<LogUploadSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is LogUploadBaseMsgData msgData)) return;

            switch (msgData.LogUploadMessageType)
            {
                case LogUploadMessageType.Accepted:
                    System.OnAccepted(msgData.UploadId);
                    break;
                case LogUploadMessageType.Done:
                    var done = (LogUploadDoneMsgData)msgData;
                    System.OnDone(done.UploadId, done.StoredBytes, done.StoredName);
                    break;
                case LogUploadMessageType.Refused:
                    var refused = (LogUploadRefusedMsgData)msgData;
                    System.OnRefused(refused.UploadId, refused.Reason, refused.Detail);
                    break;
                case LogUploadMessageType.Request:
                    var request = (LogUploadRequestMsgData)msgData;
                    System.OnLogsRequested(request.RequestId, request.Note);
                    break;
            }
        }
    }
}
