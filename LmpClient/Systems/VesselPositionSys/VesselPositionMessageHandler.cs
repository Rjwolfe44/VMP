using LmpClient;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.VesselUtilities;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.VesselPositionSys
{
    public class VesselPositionMessageHandler : SubSystem<VesselPositionSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        /// <summary>
        /// Last fully-reconstructed position state per vessel.
        /// Used to fill in omitted fields when a delta message is received.
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, VesselPosRecvSnapshot> LastReceived =
            new ConcurrentDictionary<Guid, VesselPosRecvSnapshot>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is VesselPositionMsgData msgData)) return;

            var vesselId = msgData.VesselId;
            if (!VesselCommon.DoVesselChecks(vesselId))
                return;

            // Reconstruct omitted delta fields from last received snapshot.
            ReconstructDelta(vesselId, msgData);

            VmpNetStats.RecordReceived(msgData.InternalGetMessageSize());

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

        /// <summary>
        /// Fills in any field groups absent from <paramref name="msgData"/> (as indicated by its
        /// <see cref="VesselPositionMsgData.DeltaFields"/> bitmask) using the stored snapshot for
        /// this vessel, then updates the snapshot with the resulting full state.
        /// </summary>
        private static void ReconstructDelta(Guid vesselId, VesselPositionMsgData msg)
        {
            if (msg.DeltaFields == PositionDeltaFields.All)
            {
                // Full message — just update the snapshot and return.
                LastReceived.AddOrUpdate(vesselId, _ => VesselPosRecvSnapshot.From(msg), (_, s) => { s.CopyFrom(msg); return s; });
                return;
            }

            var snap = LastReceived.GetOrAdd(vesselId, _ => new VesselPosRecvSnapshot());

            if ((msg.DeltaFields & PositionDeltaFields.Body) == 0)
            {
                msg.BodyIndex = snap.BodyIndex;
                msg.BodyName = snap.BodyName;
            }

            if ((msg.DeltaFields & PositionDeltaFields.SubspaceId) == 0)
                msg.SubspaceId = snap.SubspaceId;

            if ((msg.DeltaFields & PositionDeltaFields.HeightFromTerrain) == 0)
                msg.HeightFromTerrain = snap.HeightFromTerrain;

            if ((msg.DeltaFields & PositionDeltaFields.SurfaceFlags) == 0)
            {
                msg.Landed = snap.Landed;
                msg.Splashed = snap.Splashed;
                msg.HackingGravity = snap.HackingGravity;
            }

            if ((msg.DeltaFields & PositionDeltaFields.LatLonAlt) == 0)
                Array.Copy(snap.LatLonAlt, msg.LatLonAlt, 3);

            if ((msg.DeltaFields & PositionDeltaFields.VelocityVector) == 0)
                Array.Copy(snap.VelocityVector, msg.VelocityVector, 3);

            if ((msg.DeltaFields & PositionDeltaFields.NormalVector) == 0)
                Array.Copy(snap.NormalVector, msg.NormalVector, 3);

            if ((msg.DeltaFields & PositionDeltaFields.SrfRelRotation) == 0)
                Array.Copy(snap.SrfRelRotation, msg.SrfRelRotation, 4);

            if ((msg.DeltaFields & PositionDeltaFields.Orbit) == 0)
                Array.Copy(snap.Orbit, msg.Orbit, 8);

            // Treat the reconstructed message as a full update and store it.
            snap.CopyFrom(msg);
        }

        /// <summary>Per-vessel snapshot of the last fully-reconstructed received state.</summary>
        private class VesselPosRecvSnapshot
        {
            public int BodyIndex;
            public string BodyName;
            public int SubspaceId;
            public float HeightFromTerrain;
            public bool Landed, Splashed, HackingGravity;
            public readonly double[] LatLonAlt = new double[3];
            public readonly double[] VelocityVector = new double[3];
            public readonly double[] NormalVector = new double[3];
            public readonly float[] SrfRelRotation = new float[4];
            public readonly double[] Orbit = new double[8];

            public static VesselPosRecvSnapshot From(VesselPositionMsgData m)
            {
                var s = new VesselPosRecvSnapshot();
                s.CopyFrom(m);
                return s;
            }

            public void CopyFrom(VesselPositionMsgData m)
            {
                BodyIndex = m.BodyIndex;
                BodyName = m.BodyName;
                SubspaceId = m.SubspaceId;
                HeightFromTerrain = m.HeightFromTerrain;
                Landed = m.Landed;
                Splashed = m.Splashed;
                HackingGravity = m.HackingGravity;
                Array.Copy(m.LatLonAlt, LatLonAlt, 3);
                Array.Copy(m.VelocityVector, VelocityVector, 3);
                Array.Copy(m.NormalVector, NormalVector, 3);
                Array.Copy(m.SrfRelRotation, SrfRelRotation, 4);
                Array.Copy(m.Orbit, Orbit, 8);
            }
        }
    }
}

