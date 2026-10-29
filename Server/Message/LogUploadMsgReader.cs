using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Message.Base;
using Server.System;

namespace Server.Message
{
    public class LogUploadMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            // MessageReceiver already drops non-handshake messages from unauthenticated clients; the assembler checks again.
            try
            {
                var data = (LogUploadBaseMsgData)message.Data;
                switch (data.LogUploadMessageType)
                {
                    case LogUploadMessageType.Begin:
                        LogUploadSystem.HandleBegin(client, (LogUploadBeginMsgData)data);
                        break;
                    case LogUploadMessageType.Chunk:
                        LogUploadSystem.HandleChunk(client, (LogUploadChunkMsgData)data);
                        break;
                    case LogUploadMessageType.Cancel:
                        LogUploadSystem.HandleCancel(client, (LogUploadCancelMsgData)data);
                        break;
                }
            }
            finally
            {
                // Chunk bytes are written to disk synchronously above, so the pooled buffer can be reused.
                message.Recycle();
            }
        }
    }
}
