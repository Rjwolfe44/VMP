using HarmonyLib;
using LmpClient.Utilities;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// Times each scenario module's Save while a Game.Updated is measured (<see cref="GameSaveProfiler"/>). Stock
    /// Game.Updated saves every module through this constructor (ScenarioRunner.UpdateModules). Outside a measured
    /// save the patch does nothing but read one flag. Only observes: exceptions pass through untouched.
    /// </summary>
    [HarmonyPatch(typeof(ProtoScenarioModule), MethodType.Constructor, typeof(ScenarioModule))]
    public class ProtoScenarioModule_Ctor
    {
        [HarmonyPrefix]
        private static void PrefixCtor(out long __state)
        {
            __state = GameSaveProfiler.IsMeasuring ? StallWatch.Start() : 0;
        }

        [HarmonyPostfix]
        private static void PostfixCtor(ScenarioModule __0, long __state)
        {
            if (__state != 0)
            {
                GameSaveProfiler.RecordScenarioModule(__0, __state);
            }
        }
    }
}
