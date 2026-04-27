using LmpClient;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Extensions;
using LmpClient.Network;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.Warp;
using LmpClient.Utilities;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace LmpClient.Systems.VesselPositionSys
{
    public class VesselPositionMessageSender : SubSystem<VesselPositionSystem>, IMessageSender
    {
        /// <summary>
        /// Orbit elements are considered "unchanged" and omitted from the delta when every element
        /// differs from the last sent value by less than this threshold (absolute for angles in degrees,
        /// relative fraction for semi-major axis).  A burn will push elements past this threshold
        /// within one or two frames; coasting in a stable orbit will not.
        /// </summary>
        private const double OrbitDeltaThreshold = 1e-4;

        /// <summary>
        /// How many full (All-fields) messages to force-send between delta messages, per vessel.
        /// Every Nth message is forced full so a late-joining or packet-dropping receiver can
        /// resync without waiting for the next SOI/subspace change.
        /// </summary>
        private const int ForceFullMessageEveryN = 30;

        // Per-vessel: last values we transmitted (used to compute deltas).
        private static readonly ConcurrentDictionary<Guid, VesselPosSentSnapshot> LastSent =
            new ConcurrentDictionary<Guid, VesselPosSentSnapshot>();

        public void SendMessage(IMessageData msg)
        {
            NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<VesselCliMsg>(msg));
            VmpNetStats.RecordSent(((VesselPositionMsgData)msg).InternalGetMessageSize());
        }

        /// <summary>
        /// Sends a vessel position update
        /// </summary>
        /// <param name="vessel">Vessel to send the position</param>
        /// <param name="doOrbitDriverReadyCheck">Set it to true if you want to check if the driver is ready.
        /// Avoid checking it unless is really needed as it uses reflection that's slow</param>
        public void SendVesselPositionUpdate(Vessel vessel, bool doOrbitDriverReadyCheck = false)
        {
            if (vessel == null) return;

            if (doOrbitDriverReadyCheck && !vessel.orbitDriver.Ready())
            {
                //Orbit driver is not ready so wait max 10 frames until it's ready
                CoroutineUtil.StartConditionRoutine("SendVesselPositionUpdate",
                    () => SendVesselPositionUpdate(vessel),
                    () => vessel.orbitDriver.Ready(), 10);

            }
            else
            {
                var msg = CreateMessageFromVessel(vessel);
                if (msg == null) return;

                SendMessage(msg);
            }
        }

        public static VesselPositionMsgData CreateMessageFromVessel(Vessel vessel)
        {
            if (!OrbitParametersAreOk(vessel)) return null;

            var msgData = MessageFactory.CreateNewMessageData<VesselPositionMsgData>();
            msgData.PingSec = NetworkStatistics.PingSec;
            msgData.SubspaceId = WarpSystem.Singleton.CurrentSubspace;
            msgData.GameTime = TimeSyncSystem.UniversalTime;
            try
            {
                msgData.VesselId = vessel.id;
                msgData.BodyIndex = vessel.mainBody.flightGlobalsIndex;
                msgData.BodyName = vessel.orbit.referenceBody.bodyName;
                msgData.Landed = vessel.Landed;
                msgData.Splashed = vessel.Splashed;

                SetSrfRelRotation(vessel, msgData);
                SetLatLonAlt(vessel, msgData);
                SetVelocityVector(vessel, msgData);
                SetAngularVelocityVector(vessel, msgData);
                SetNormalVector(vessel, msgData);
                SetOrbit(vessel, msgData);

                msgData.HeightFromTerrain = vessel.heightFromTerrain;

                if (MainSystem.BodiesGees.TryGetValue(vessel.mainBody, out var bodyGee))
                    msgData.HackingGravity = Math.Abs(bodyGee - vessel.mainBody.GeeASL) > 0.0001;
                else
                    msgData.HackingGravity = false;

                ComputeDeltaFields(vessel.id, msgData);

                return msgData;
            }
            catch (Exception e)
            {
                LunaLog.Log($"[VMP]: Failed to get vessel position update, exception: {e}");
            }

            return null;
        }

        /// <summary>
        /// Computes which field groups actually changed since the last transmission for this vessel
        /// and sets <see cref="VesselPositionMsgData.DeltaFields"/> accordingly.
        /// Every <see cref="ForceFullMessageEveryN"/> messages a full update is sent so receivers
        /// can always resync.
        /// </summary>
        private static void ComputeDeltaFields(Guid vesselId, VesselPositionMsgData msg)
        {
            var snap = LastSent.GetOrAdd(vesselId, _ => new VesselPosSentSnapshot());

            msg.SequenceNumber = unchecked(++snap.SequenceNumber);
            snap.SendCount++;
            if (snap.SendCount % ForceFullMessageEveryN == 1)
            {
                // Force full message — receiver always gets a clean baseline periodically.
                msg.DeltaFields = PositionDeltaFields.All;
                snap.CopyFrom(msg);
                return;
            }

            var flags = PositionDeltaFields.None;

            // LatLonAlt / VelocityVector / NormalVector / SrfRelRotation / HeightFromTerrain:
            // These change every frame and are cheap to send, so always include them.
            flags |= PositionDeltaFields.LatLonAlt | PositionDeltaFields.VelocityVector |
                     PositionDeltaFields.NormalVector | PositionDeltaFields.SrfRelRotation |
                     PositionDeltaFields.AngularVelocity |
                     PositionDeltaFields.HeightFromTerrain;

            // Body — only when the vessel changes SOI.
            if (msg.BodyIndex != snap.BodyIndex || msg.BodyName != snap.BodyName)
                flags |= PositionDeltaFields.Body;

            // SubspaceId — only when the subspace changes.
            if (msg.SubspaceId != snap.SubspaceId)
                flags |= PositionDeltaFields.SubspaceId;

            // SurfaceFlags — only when any flag changes.
            if (msg.Landed != snap.Landed || msg.Splashed != snap.Splashed || msg.HackingGravity != snap.HackingGravity)
                flags |= PositionDeltaFields.SurfaceFlags;

            // Orbit — only when elements have shifted enough to indicate a burn or SOI change.
            if (OrbitChanged(msg, snap))
                flags |= PositionDeltaFields.Orbit;

            msg.DeltaFields = flags;
            snap.CopyFrom(msg);
        }

        private static bool OrbitChanged(VesselPositionMsgData msg, VesselPosSentSnapshot snap)
        {
            for (var i = 0; i < 8; i++)
            {
                var delta = Math.Abs(msg.Orbit[i] - snap.Orbit[i]);
                if (delta > OrbitDeltaThreshold)
                    return true;
            }
            return false;
        }

        #region Set message values

        private static void SetOrbit(Vessel vessel, VesselPositionMsgData msgData)
        {
            msgData.Orbit[0] = vessel.orbit.inclination;
            msgData.Orbit[1] = vessel.orbit.eccentricity;
            msgData.Orbit[2] = vessel.orbit.semiMajorAxis;
            msgData.Orbit[3] = vessel.orbit.LAN;
            msgData.Orbit[4] = vessel.orbit.argumentOfPeriapsis;
            msgData.Orbit[5] = vessel.orbit.meanAnomalyAtEpoch;
            msgData.Orbit[6] = vessel.orbit.epoch;
            msgData.Orbit[7] = vessel.orbit.referenceBody.flightGlobalsIndex;
        }

        private static void SetVelocityVector(Vessel vessel, VesselPositionMsgData msgData)
        {
            var velVector = Quaternion.Inverse(vessel.mainBody.bodyTransform.rotation) * vessel.srf_velocity;
            msgData.VelocityVector[0] = velVector.x;
            msgData.VelocityVector[1] = velVector.y;
            msgData.VelocityVector[2] = velVector.z;
        }

        private static void SetAngularVelocityVector(Vessel vessel, VesselPositionMsgData msgData)
        {
            msgData.AngVelocityVector[0] = vessel.angularVelocity.x;
            msgData.AngVelocityVector[1] = vessel.angularVelocity.y;
            msgData.AngVelocityVector[2] = vessel.angularVelocity.z;
        }

        private static void SetNormalVector(Vessel vessel, VesselPositionMsgData msgData)
        {
            msgData.NormalVector[0] = vessel.terrainNormal.x;
            msgData.NormalVector[1] = vessel.terrainNormal.y;
            msgData.NormalVector[2] = vessel.terrainNormal.z;
        }

        private static void SetLatLonAlt(Vessel vessel, VesselPositionMsgData msgData)
        {
            msgData.LatLonAlt[0] = vessel.latitude;
            msgData.LatLonAlt[1] = vessel.longitude;
            msgData.LatLonAlt[2] = vessel.altitude;
        }

        private static void SetSrfRelRotation(Vessel vessel, VesselPositionMsgData msgData)
        {
            msgData.SrfRelRotation[0] = vessel.srfRelRotation.x;
            msgData.SrfRelRotation[1] = vessel.srfRelRotation.y;
            msgData.SrfRelRotation[2] = vessel.srfRelRotation.z;
            msgData.SrfRelRotation[3] = vessel.srfRelRotation.w;
        }

        #endregion

        /// <summary>
        /// Checks if the vessel contains NaN in any orbit parameter
        /// </summary>
        private static bool OrbitParametersAreOk(Vessel vessel)
        {
            var orbitParamsAreNan = double.IsNaN(vessel.orbit.inclination) ||
                                    double.IsNaN(vessel.orbit.eccentricity) ||
                                    double.IsNaN(vessel.orbit.semiMajorAxis) ||
                                    double.IsNaN(vessel.orbit.LAN) ||
                                    double.IsNaN(vessel.orbit.argumentOfPeriapsis) ||
                                    double.IsNaN(vessel.orbit.meanAnomalyAtEpoch) ||
                                    double.IsNaN(vessel.orbit.epoch) ||
                                    double.IsNaN(vessel.orbit.referenceBody.flightGlobalsIndex);

            return !orbitParamsAreNan;
        }

        /// <summary>
        /// Per-vessel snapshot of the last transmitted position values, used to compute delta flags.
        /// </summary>
        private class VesselPosSentSnapshot
        {
            public int SendCount;
            public ushort SequenceNumber;
            public int BodyIndex;
            public string BodyName;
            public int SubspaceId;
            public bool Landed, Splashed, HackingGravity;
            public readonly double[] Orbit = new double[8];
            public readonly float[] AngVelocityVector = new float[3];

            public void CopyFrom(VesselPositionMsgData m)
            {
                BodyIndex = m.BodyIndex;
                BodyName = m.BodyName;
                SubspaceId = m.SubspaceId;
                Landed = m.Landed;
                Splashed = m.Splashed;
                HackingGravity = m.HackingGravity;
                Array.Copy(m.AngVelocityVector, AngVelocityVector, 3);
                Array.Copy(m.Orbit, Orbit, 8);
            }
        }
    }
}
