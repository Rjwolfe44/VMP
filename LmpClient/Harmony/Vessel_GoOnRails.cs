using HarmonyLib;
using LmpClient.Events;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to trigger an event when FINISHED packing a vessel
    /// </summary>
    [HarmonyPatch(typeof(Vessel))]
    [HarmonyPatch("GoOnRails")]
    public class Vessel_GoOnRails
    {
        [HarmonyPostfix]
        private static void PostfixGoOnRails(Vessel __instance)
        {
            // KSP's EventData.Fire can throw by itself (seen once: a null entry in its own listener list while a warp
            // subspace change put vessels on rails, which disconnected the player). Never let that escape into the caller.
            try
            {
                RailEvent.onVesselGoneOnRails.Fire(__instance);
            }
            catch (Exception ex)
            {
                LunaLog.LogError($"[VMP] onVesselGoneOnRails failed for vessel {(__instance != null ? __instance.id.ToString() : "null")}: {ex}");
            }
        }
    }
}
