using System;
using HarmonyLib;
using LmpClient.Utilities;
using LmpCommon.Enums;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// Measures KSP's game state save (Game.Updated, run by every stock save) so a slow one is logged with the scenario
    /// module or mod handler that took the time. See <see cref="GameSaveProfiler"/>. Only observes: the original always
    /// runs and exceptions pass through untouched.
    /// </summary>
    [HarmonyPatch(typeof(Game))]
    [HarmonyPatch("Updated", typeof(GameScenes))]
    public class Game_Updated
    {
        [HarmonyPrefix]
        private static void PrefixUpdated(out bool __state)
        {
            __state = false;
            if (MainSystem.NetworkState < ClientState.Connected) return;

            GameSaveProfiler.Begin();
            __state = true;
        }

        /// <summary>
        /// A finalizer, not a postfix: a mod's OnSave that throws aborts Game.Updated, and Harmony skips postfixes then,
        /// which would leave the measurement open. Only closes a measurement this call opened, and hands back the original
        /// exception (or none) so the game sees exactly what it would have seen without us.
        /// </summary>
        [HarmonyFinalizer]
        private static Exception FinalizeUpdated(bool __state, Exception __exception)
        {
            if (__state)
            {
                GameSaveProfiler.End();
            }

            return __exception;
        }
    }
}
