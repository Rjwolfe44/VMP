using LmpCommon.Message.Data.Vessel;
using System;

namespace LmpCommon.Sync
{
    /// <summary>
    /// Pure, testable helpers for vessel position delta encoding, sequence handling,
    /// and render-delay / interpolation timing. No Unity or KSP dependencies.
    /// </summary>
    public static class VesselPositionSyncMath
    {
        public const double DefaultOrbitDeltaThreshold = 1e-4;
        public const int DefaultForceFullMessageEveryN = 30;
        public const float MaxRenderOffsetSec = 2.5f;
        public const float MinRenderOffsetFloorSec = 0.100f;

        /// <summary>
        /// How far behind real time observers render a remote vessel.
        /// Uses only the sender's ping so every observer agrees on the same render timestamp.
        /// </summary>
        public static float PositionRenderOffsetSec(float senderPingSec, int vesselUpdatesMsInterval)
        {
            var sendIntervalSec = vesselUpdatesMsInterval / 1000f;
            var floor = Math.Max(MinRenderOffsetFloorSec, 2f * sendIntervalSec);
            return Clamp(senderPingSec, floor, MaxRenderOffsetSec);
        }

        /// <summary>Max seconds to stretch one position interpolation segment (2× send interval).</summary>
        public static double MaxInterpolationDurationSec(int effectiveSendMsInterval) =>
            effectiveSendMsInterval / 1000.0 * 2.0;

        public static double ComputeTimeDifference(double universalTime, double gameTimeStamp, float senderPingSec, int vesselUpdatesMsInterval) =>
            universalTime - gameTimeStamp - PositionRenderOffsetSec(senderPingSec, vesselUpdatesMsInterval);

        public static bool IsPositionUpdateTooOld(double gameTimeStamp, float senderPingSec, double universalTime, int vesselUpdatesMsInterval) =>
            gameTimeStamp < universalTime - PositionRenderOffsetSec(senderPingSec, vesselUpdatesMsInterval);

        /// <summary>
        /// Returns a signed adjustment (seconds) applied to interpolation duration to absorb clock error.
        /// <paramref name="timeDifference"/> is positive when the client is behind the target render time.
        /// </summary>
        public static double ComputeInterpolationFixSec(double timeDifference, double fixedDeltaTime)
        {
            var errorInSeconds = Math.Abs(timeDifference);
            var errorInFrames = errorInSeconds / fixedDeltaTime;

            if (errorInFrames < 1)
                return 0;

            if (errorInFrames <= 2)
                return fixedDeltaTime;

            if (errorInFrames <= 5)
                return fixedDeltaTime * 2;

            if (errorInSeconds <= 2.5)
                return fixedDeltaTime * errorInFrames / 2;

            return fixedDeltaTime * errorInFrames;
        }

        public static double ComputeSignedInterpolationFixSec(double timeDifference, double fixedDeltaTime) =>
            (timeDifference > 0 ? -1 : 1) * ComputeInterpolationFixSec(timeDifference, fixedDeltaTime);

        public static bool TryAcceptSequence(ushort sequenceNumber, ref VesselPositionRecvState state)
        {
            if (state.HasSequenceNumber)
            {
                var delta = unchecked((short)(sequenceNumber - state.LastSequenceNumber));
                if (delta <= 0)
                    return false;
            }

            state.LastSequenceNumber = sequenceNumber;
            state.HasSequenceNumber = true;
            return true;
        }

        public static bool OrbitElementsChanged(double[] orbit, double[] previous, double threshold = DefaultOrbitDeltaThreshold)
        {
            if (orbit == null || previous == null || orbit.Length < 8 || previous.Length < 8)
                return true;

            for (var i = 0; i < 8; i++)
            {
                if (Math.Abs(orbit[i] - previous[i]) > threshold)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Computes delta flags and sequence number for an outgoing position message.
        /// </summary>
        public static void ApplyOutgoingDelta(
            VesselPositionMsgData msg,
            ref VesselPositionSentState state,
            bool forceIncludeOrbit = false,
            int forceFullMessageEveryN = DefaultForceFullMessageEveryN,
            double orbitDeltaThreshold = DefaultOrbitDeltaThreshold)
        {
            msg.SequenceNumber = unchecked(++state.SequenceNumber);
            state.SendCount++;

            if (state.SendCount % forceFullMessageEveryN == 1)
            {
                msg.DeltaFields = PositionDeltaFields.All;
                state.CopyFrom(msg);
                return;
            }

            var flags = PositionDeltaFields.LatLonAlt | PositionDeltaFields.VelocityVector |
                        PositionDeltaFields.NormalVector | PositionDeltaFields.SrfRelRotation |
                        PositionDeltaFields.AngularVelocity | PositionDeltaFields.HeightFromTerrain;

            if (msg.BodyIndex != state.BodyIndex || msg.BodyName != state.BodyName)
                flags |= PositionDeltaFields.Body;

            if (msg.SubspaceId != state.SubspaceId)
                flags |= PositionDeltaFields.SubspaceId;

            if (msg.Landed != state.Landed || msg.Splashed != state.Splashed || msg.HackingGravity != state.HackingGravity)
                flags |= PositionDeltaFields.SurfaceFlags;

            if (forceIncludeOrbit || OrbitElementsChanged(msg.Orbit, state.Orbit, orbitDeltaThreshold))
                flags |= PositionDeltaFields.Orbit;

            msg.DeltaFields = flags;
            state.CopyFrom(msg);
        }

        /// <summary>
        /// Fills omitted delta fields from <paramref name="state"/> and updates the snapshot.
        /// </summary>
        public static void ReconstructIncomingDelta(VesselPositionMsgData msg, ref VesselPositionRecvState state)
        {
            if (msg.DeltaFields == PositionDeltaFields.All)
            {
                state.CopyFrom(msg);
                return;
            }

            if ((msg.DeltaFields & PositionDeltaFields.Body) == 0)
            {
                msg.BodyIndex = state.BodyIndex;
                msg.BodyName = state.BodyName;
            }

            if ((msg.DeltaFields & PositionDeltaFields.SubspaceId) == 0)
                msg.SubspaceId = state.SubspaceId;

            if ((msg.DeltaFields & PositionDeltaFields.HeightFromTerrain) == 0)
                msg.HeightFromTerrain = state.HeightFromTerrain;

            if ((msg.DeltaFields & PositionDeltaFields.SurfaceFlags) == 0)
            {
                msg.Landed = state.Landed;
                msg.Splashed = state.Splashed;
                msg.HackingGravity = state.HackingGravity;
            }

            if ((msg.DeltaFields & PositionDeltaFields.LatLonAlt) == 0)
                Array.Copy(state.LatLonAlt, msg.LatLonAlt, 3);

            if ((msg.DeltaFields & PositionDeltaFields.VelocityVector) == 0)
                Array.Copy(state.VelocityVector, msg.VelocityVector, 3);

            if ((msg.DeltaFields & PositionDeltaFields.NormalVector) == 0)
                Array.Copy(state.NormalVector, msg.NormalVector, 3);

            if ((msg.DeltaFields & PositionDeltaFields.SrfRelRotation) == 0)
                Array.Copy(state.SrfRelRotation, msg.SrfRelRotation, 4);

            if ((msg.DeltaFields & PositionDeltaFields.AngularVelocity) == 0)
                Array.Copy(state.AngVelocityVector, msg.AngVelocityVector, 3);

            if ((msg.DeltaFields & PositionDeltaFields.Orbit) == 0)
                Array.Copy(state.Orbit, msg.Orbit, 8);

            state.CopyFrom(msg);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
