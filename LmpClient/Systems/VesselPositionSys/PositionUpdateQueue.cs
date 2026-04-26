using LmpClient;
using LmpClient.Base;
using LmpCommon.Message.Data.Vessel;
using System;

namespace LmpClient.Systems.VesselPositionSys
{
    public class PositionUpdateQueue : CachedConcurrentQueue<VesselPositionUpdate, VesselPositionMsgData>
    {
        /// <summary>
        /// Maximum number of position updates held per vessel before old ones are evicted.
        /// Keeping at most this many items prevents interpolating through a large backlog of
        /// stale positions when a client falls temporarily behind the network.
        /// 3 = enough for smooth interpolation (current + next + one in reserve).
        /// </summary>
        private const int MaxQueueDepth = 3;

        /// <summary>
        /// Enqueue a new position update, evicting the oldest queued item first if the queue
        /// is already at capacity.  Evicted items are returned to the shared cache to avoid
        /// allocation churn.
        /// </summary>
        public override void Enqueue(VesselPositionMsgData msgData)
        {
            while (Queue.Count >= MaxQueueDepth && Queue.TryDequeue(out var stale))
            {
                Cache.Add(stale);
                VmpNetStats.RecordDrop();
            }

            base.Enqueue(msgData);
        }

        protected override void AssignFromMessage(VesselPositionUpdate value, VesselPositionMsgData msgData)
        {
            value.VesselId = msgData.VesselId;
            value.SubspaceId = msgData.SubspaceId;
            value.BodyIndex = msgData.BodyIndex;
            value.HeightFromTerrain = msgData.HeightFromTerrain;
            value.PingSec = msgData.PingSec;
            value.Landed = msgData.Landed;
            value.Splashed = msgData.Splashed;
            value.GameTimeStamp = msgData.GameTime;
            value.HackingGravity = msgData.HackingGravity;

            Array.Copy(msgData.SrfRelRotation, value.SrfRelRotation, 4);
            Array.Copy(msgData.LatLonAlt, value.LatLonAlt, 3);
            Array.Copy(msgData.VelocityVector, value.VelocityVector, 3);
            Array.Copy(msgData.NormalVector, value.NormalVector, 3);
            Array.Copy(msgData.Orbit, value.Orbit, 8);
        }
    }
}
