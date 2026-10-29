using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System.Collections.Concurrent;

namespace LmpClient.Systems.PersistentSync
{
    public class PersistentSyncMessageHandler : SubSystem<PersistentSyncSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is PersistentSyncBaseMsgData msgData)) return;
            if (msgData.PersistentSyncMessageType != PersistentSyncMessageType.Snapshot) return;

            var snapshot = (PersistentSyncSnapshotMsgData)msg.Data;

            // The payload may be compressed, possibly relative to the previous snapshot of this domain. Decode before anything
            // else looks at it. A snapshot that cannot be restored exactly is never applied: the client asks for a full one.
            var decoded = System.SnapshotDecoder.TryDecodeInPlace(snapshot, out var decodeError);

            // Any snapshot message proves the stream is alive, decodable or not.
            System.NoteSnapshotHandled(snapshot);
            if (!decoded)
            {
                System.RequestFullSnapshot(snapshot.DomainId, snapshot.Revision, decodeError);
                return;
            }

            System.Reconciler.HandleSnapshot(snapshot);
        }
    }
}
