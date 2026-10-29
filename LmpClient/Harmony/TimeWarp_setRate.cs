using HarmonyLib;
using LmpClient.Systems.Warp;
using LmpCommon.Enums;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to deny the warping if needed. Adds another layer of security
    /// </summary>
    [HarmonyPatch(typeof(TimeWarp))]
    [HarmonyPatch("SetRate")]
    [HarmonyPatch(new[] { typeof(int), typeof(bool), typeof(bool) })]
    public class TimeWarp_SetRate
    {
        [HarmonyPrefix]
        private static bool PrefixSetRate(ref int rate_index, ref bool instant, ref bool postScreenMessage)
        {
            if (MainSystem.NetworkState < ClientState.Connected) return true;

            if (!WarpSystem.Singleton.WarpValidation()) return false;

            rate_index = RailsWarpStepGuard.LimitWhileActiveVesselUnpacked(rate_index, instant);
            return true;
        }
    }

    /// <summary>
    /// KSP packs the active vessel for rails warp in OrbitPhysicsManager.LateUpdate, after the new rate is already set.
    /// A physics tick that runs before that uses TimeWarp.fixedDeltaTime (0.02 s x rate) in the stock orbit drift
    /// correction (VesselPrecalculate.CalculateGravity), so jumping from 1x straight to 10000x sets the vessel's velocity
    /// to where its orbit will be 200 s later. The orbit then dips underground and KSP destroys the vessel ("crashed
    /// through terrain") as soon as it is packed. ApexGuidance autowarp did exactly that whenever the frame ran a second
    /// physics tick. Stock warp and upstream MechJeb raise one step at a time; we cap the request at the first rails rate
    /// while the active vessel is unpacked and re-apply the requested rate once it is packed.
    /// </summary>
    internal static class RailsWarpStepGuard
    {
        private static int _pendingRateIndex = -1;
        private static bool _pendingInstant;

        public static int LimitWhileActiveVesselUnpacked(int rateIndex, bool instant)
        {
            _pendingRateIndex = -1;
            if (rateIndex <= 0 || !HighLogic.LoadedSceneIsFlight || !TimeWarp.fetch || TimeWarp.WarpMode != TimeWarp.Modes.HIGH)
                return rateIndex;

            var vessel = FlightGlobals.ActiveVessel;
            if (!vessel || !vessel.loaded || vessel.packed)
                return rateIndex;

            var firstRailsRateIndex = TimeWarp.fetch.maxPhysicsRate_index + 1;
            if (rateIndex <= firstRailsRateIndex)
                return rateIndex;

            _pendingRateIndex = rateIndex;
            _pendingInstant = instant;
            LunaLog.Log($"[VMP]: Rails warp to rate index {rateIndex} requested while {vessel.vesselName} is not packed yet; " +
                        $"warping at rate index {firstRailsRateIndex} until it is (a direct jump can destroy the vessel).");
            return firstRailsRateIndex;
        }

        /// <summary>
        /// Call every Update: once the active vessel is on rails, apply the rate that was capped.
        /// </summary>
        public static void ApplyPending()
        {
            if (_pendingRateIndex < 0) return;

            if (!HighLogic.LoadedSceneIsFlight || !TimeWarp.fetch || TimeWarp.WarpMode != TimeWarp.Modes.HIGH || TimeWarp.CurrentRateIndex == 0)
            {
                // Warp was stopped or switched to physics warp in the meantime
                _pendingRateIndex = -1;
                return;
            }

            var vessel = FlightGlobals.ActiveVessel;
            if (!vessel || !vessel.packed) return;

            var rateIndex = _pendingRateIndex;
            _pendingRateIndex = -1;
            TimeWarp.SetRate(rateIndex, _pendingInstant);
        }

        public static void Reset() => _pendingRateIndex = -1;
    }
}
