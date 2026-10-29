using HarmonyLib;
using LmpClient.Events;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to trigger an event when FINISHED unpacking a vessel
    /// </summary>
    [HarmonyPatch(typeof(Vessel))]
    [HarmonyPatch("GoOffRails")]
    public class Vessel_GoOffRails
    {
        [HarmonyPostfix]
        private static void PostfixGoOffRails(Vessel __instance)
        {
            // See Vessel_GoOnRails: a failure inside KSP's EventData.Fire must not escape into the caller.
            try
            {
                RailEvent.onVesselGoneOffRails.Fire(__instance);
            }
            catch (Exception ex)
            {
                LunaLog.LogError($"[VMP] onVesselGoneOffRails failed for vessel {(__instance != null ? __instance.id.ToString() : "null")}: {ex}");
            }
        }
    }
}
