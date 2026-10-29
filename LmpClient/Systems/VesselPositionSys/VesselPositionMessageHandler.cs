using LmpClient;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.VesselUtilities;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Sync;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.VesselPositionSys
{
    public class VesselPositionMessageHandler : SubSystem<VesselPositionSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        private static readonly ConcurrentDictionary<Guid, VesselPositionRecvState> LastReceived =
            new ConcurrentDictionary<Guid, VesselPositionRecvState>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is VesselPositionMsgData msgData)) return;

            var vesselId = msgData.VesselId;
            if (!VesselCommon.DoVesselChecks(vesselId))
                return;

            var snap = LastReceived.GetOrAdd(vesselId, _ => new VesselPositionRecvState());
            if (!VesselPositionSyncMath.TryAcceptSequence(msgData.SequenceNumber, ref snap))
                return;

            VesselPositionSyncMath.ReconstructIncomingDelta(msgData, ref snap);

            VmpNetStats.RecordReceived(msgData.InternalGetMessageSize());
            VmpSyncTrace.RecordReceived(msgData);

            if (!VesselPositionSystem.CurrentVesselUpdate.ContainsKey(vesselId))
            {
                VesselPositionSystem.CurrentVesselUpdate.TryAdd(vesselId, new VesselPositionUpdate(msgData));
                VesselPositionSystem.TargetVesselUpdateQueue.TryAdd(vesselId, new PositionUpdateQueue());
            }
            else
            {
                VesselPositionSystem.TargetVesselUpdateQueue.TryGetValue(vesselId, out var queue);
                queue?.Enqueue(msgData);
            }
        }
    }
}
