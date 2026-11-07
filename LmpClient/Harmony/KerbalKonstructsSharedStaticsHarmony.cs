using System;
using HarmonyLib;
using LmpClient.Systems.KerbalKonstructsShare;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Kerbal Konstructs hooks for sharing in-game static edits (docs/kk-live-sharing.md). KK is optional: without it,
    /// or with a KK version that lacks a member we use, nothing is patched and one line says why.
    /// Every patch body catches everything so neither KK nor the scenario load ever sees an exception from VMP.
    /// </summary>
    public static class KerbalKonstructsSharedStaticsHarmony
    {
        private static bool _tried;

        public static void TryRegister(HarmonyLib.Harmony harmony)
        {
            if (_tried)
                return;
            _tried = true;

            if (AccessTools.TypeByName("KerbalKonstructs.Core.StaticInstance") == null)
                return; //KK not installed: stay silent.

            try
            {
                var kk = KkReflection.TryCreate(out var problem);
                if (kk == null)
                {
                    LunaLog.LogWarning($"[KKShare] Sharing Kerbal Konstructs edits is off: this Kerbal Konstructs version does not match ({problem}).");
                    return;
                }

                var self = typeof(KerbalKonstructsSharedStaticsHarmony);
                harmony.Patch(kk.SettingsOnLoad, prefix: new HarmonyMethod(self, nameof(PrefixOnLoad)));
                harmony.Patch(kk.CareerStateLoad, prefix: new HarmonyMethod(self, nameof(PrefixCareerStateLoad)));
                harmony.Patch(kk.SettingsOnSave, postfix: new HarmonyMethod(self, nameof(PostfixOnSave)));
                harmony.Patch(kk.SiSaveConfig, postfix: new HarmonyMethod(self, nameof(PostfixSaveConfig)));
                harmony.Patch(kk.SaveObjects, prefix: new HarmonyMethod(self, nameof(PrefixSaveObjects)), postfix: new HarmonyMethod(self, nameof(PostfixSaveObjects)),
                    finalizer: new HarmonyMethod(self, nameof(FinalizerSaveObjects)));
                harmony.Patch(kk.SelectInstance, postfix: new HarmonyMethod(self, nameof(PostfixSelectInstance)));
                KkSharedStaticsSync.Initialize(kk);
                LunaLog.Log("[KKShare] Sharing Kerbal Konstructs edits with the server is on.");
            }
            catch (Exception ex)
            {
                LunaLog.LogError($"[KKShare] Could not hook Kerbal Konstructs, sharing edits is off: {ex.Message}");
            }
        }

        /// <summary>Before KK's OnLoad body: apply shared statics so KK's own state load finds them (docs/kk-live-sharing.md).</summary>
        private static void PrefixOnLoad(ConfigNode node)
        {
            try { KkSharedStaticsSync.OnScenarioLoading(node); }
            catch (Exception) { /* never break KK's load */ }
        }

        /// <summary>Before KK loads saved facility and launch site state (every static exists by then): drop states KK would crash on.</summary>
        private static void PrefixCareerStateLoad(ConfigNode kkcfgNode)
        {
            try { KkSharedStaticsSync.OnCareerStateLoading(kkcfgNode); }
            catch (Exception) { /* never break KK's load */ }
        }

        private static void PostfixOnSave(ConfigNode node)
        {
            try { KkSharedStaticsSync.OnScenarioSaved(node); }
            catch (Exception) { /* logged inside */ }
        }

        private static void PostfixSaveConfig(object __instance)
        {
            try { KkSharedStaticsSync.OnInstanceSaved(__instance); }
            catch (Exception) { /* logged inside */ }
        }

        private static void PrefixSaveObjects()
        {
            try { KkSharedStaticsSync.OnSaveAllStarting(); }
            catch (Exception) { /* never break KK's save */ }
        }

        /// <summary>Runs even when SaveObjects throws, so the "inside Save all" flag can never stay set.</summary>
        private static void FinalizerSaveObjects()
        {
            try { KkSharedStaticsSync.OnSaveAllEnded(); }
            catch (Exception) { /* never break KK's save */ }
        }

        private static void PostfixSaveObjects()
        {
            try { KkSharedStaticsSync.OnAllSaved(); }
            catch (Exception) { /* logged inside */ }
        }

        private static void PostfixSelectInstance(object[] __args)
        {
            try
            {
                //Any selection: the statics editor's list selects with isEditing = false and its editors work on that static.
                if (__args != null && __args.Length >= 1)
                    KkSharedStaticsSync.OnSelected(__args[0]);
            }
            catch (Exception) { /* never break KK's editor */ }
        }
    }
}
