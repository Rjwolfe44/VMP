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
        /// <para>
        /// Sized to absorb realistic network jitter for the default 50ms nearby-vessel send
        /// interval (~20 msg/s/vessel).  The interpolation/consumption side already handles
        /// backlog: <see cref="VesselPositionUpdate.AdjustExtraInterpolationTimes"/> sets
        /// <c>CurrentFrame = float.MaxValue</c> when the client is behind, which causes one
        /// queued packet to be drained per FixedUpdate (~50 Hz) — faster than arrival.
        /// So a deep queue self-drains without packet loss; the only role of this cap is to
        /// bound memory if the client is hard-stalled (loading screen, alt-tabbed, GC hitch).
        /// </para>
        /// <para>
        /// 16 slots = ~800ms of buffered motion at 50ms cadence.  Beyond that, the client is
        /// truly stalled and dropping the oldest is the right behavior.
        /// </para>
        /// </summary>
        private const int MaxQueueDepth = 16;

        /// <summary>
        /// Enqueue a new position update.  If the queue is at capacity (client is hard-stalled),
        /// evict the oldest queued items so we hold the freshest data.  Evicted items are
        /// returned to the shared cache to avoid allocation churn.
        /// </summary>
        public override void Enqueue(VesselPositionMsgData msgData)
        {
            while (Queue.Count >= MaxQueueDepth && Queue.TryDequeue(out var stale))
            {
                Cache.Add(stale);
                VmpNetStats.RecordDrop();
            }

            base.Enqueue(msgData);
            VmpNetStats.RecordQueueDepth(Queue.Count);
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
