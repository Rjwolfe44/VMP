using LunaConfigNode.CfgNode;
using Server.Log;
using System.Collections.Concurrent;

namespace Server.System.Scenario
{
    /// <summary>
    /// Keeps, per connected client, the copy of a merged scenario the server last handed it, and uses that copy
    /// as the common ancestor when the client uploads its own version (see <see cref="ScenarioNodeMerge"/>).
    /// </summary>
    public static class ScenarioClientMerge
    {
        /// <summary>
        /// Call with the exact text sent to the client when it requests its scenarios.
        /// <paramref name="baselines"/> is the client's own <c>ScenarioMergeBaselines</c>.
        /// </summary>
        public static void RecordBaseline(ConcurrentDictionary<string, ConfigNode> baselines, string module, string scenarioText)
        {
            if (scenarioText == null || !ScenarioNodeMerge.IsMergedModule(module))
                return;

            baselines[module] = new ConfigNode(scenarioText);
        }

        /// <summary>
        /// Stores a client's upload of a merged module. Runs on the caller's thread, so uploads from one client
        /// are applied in the order they arrive.
        /// </summary>
        public static void ApplyUpload(ConcurrentDictionary<string, ConfigNode> baselines, string playerName, string module, string scenarioText)
        {
            var theirs = new ConfigNode(scenarioText);
            //The merge moves nodes out of "theirs" into the stored copy, so the next baseline is a separate parse.
            var nextBaseline = new ConfigNode(scenarioText);

            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
            {
                var hasStored = ScenarioStoreSystem.CurrentScenarios.TryGetValue(module, out var ours);
                if (hasStored && baselines.TryGetValue(module, out var baseNode))
                {
                    var changes = ScenarioNodeMerge.Merge(baseNode, theirs, ours);
                    LunaLog.Debug($"Merged scenario {module} from {playerName}: {changes} change(s) applied.");
                }
                else if (hasStored)
                {
                    //The server had nothing to hand this client at join (fresh universe), so its copy is just the mod's
                    //defaults and we cannot tell what it changed: keep what the server has and only add what is new.
                    var added = ScenarioNodeMerge.AddMissing(theirs, ours);
                    LunaLog.Debug($"Scenario {module} from {playerName} had no merge baseline: {added} missing entr(ies) added, server values kept.");
                }
                else
                {
                    //First copy of this scenario the server has ever seen.
                    LunaLog.Debug($"Scenario {module} from {playerName} stored as the first copy.");
                    ScenarioStoreSystem.CurrentScenarios[module] = theirs;
                }

                baselines[module] = nextBaseline;
            }
        }
    }
}
