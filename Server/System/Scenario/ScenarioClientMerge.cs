using LunaConfigNode.CfgNode;
using Server.Log;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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

        /// <summary>Distinct refusals and errors logged per connection; after that one line says the rest is suppressed.</summary>
        private const int MaxLoggedPerConnection = 100;
        private const string SuppressedMarker = "<suppressed>";

        /// <summary>
        /// Stores a client's upload of a merged module. Runs on the caller's thread, so uploads from one client
        /// are applied in the order they arrive. The merge runs on a copy of the stored module: if it throws, the
        /// stored module and the client's baseline both stay as they were, so nothing is half applied and counters
        /// (MissionCount) are not counted twice when the client uploads again.
        /// </summary>
        /// <param name="logged">The connection's ScenarioMergeLogged, so each refusal is logged once per connection and
        /// one client cannot fill a shared budget and silence everybody else. Null logs every refusal (tests).</param>
        public static void ApplyUpload(ConcurrentDictionary<string, ConfigNode> baselines, string playerName, string module, string scenarioText,
            ConcurrentDictionary<string, byte> logged = null)
        {
            var notes = new List<string>();
            try
            {
                var theirs = new ConfigNode(scenarioText);
                ScenarioNodeMerge.SanitizeShared(theirs, notes);
                //The merge moves nodes out of "theirs" into the stored copy, so the next baseline is a separate parse.
                var nextBaseline = new ConfigNode(scenarioText);
                ScenarioNodeMerge.SanitizeShared(nextBaseline, null);

                lock (ScenarioStoreSystem.ConfigTreeAccessLock)
                {
                    var hasStored = ScenarioStoreSystem.CurrentScenarios.TryGetValue(module, out var stored);
                    if (hasStored && baselines.TryGetValue(module, out var baseNode))
                    {
                        var working = new ConfigNode(stored.ToString());
                        var changes = ScenarioNodeMerge.Merge(baseNode, theirs, working, notes);
                        ScenarioStoreSystem.CurrentScenarios[module] = working;
                        LunaLog.Debug($"Merged scenario {module} from {playerName}: {changes} change(s) applied.");
                    }
                    else if (hasStored)
                    {
                        //The server had nothing to hand this client at join (fresh universe), so its copy is just the mod's
                        //defaults and we cannot tell what it changed: keep what the server has and only add what is new.
                        var working = new ConfigNode(stored.ToString());
                        var added = ScenarioNodeMerge.AddMissing(theirs, working, notes);
                        ScenarioStoreSystem.CurrentScenarios[module] = working;
                        LunaLog.Debug($"Scenario {module} from {playerName} had no merge baseline: {added} missing entr(ies) added, server values kept.");
                    }
                    else
                    {
                        //First copy of this scenario the server has ever seen (already sanitized above).
                        LunaLog.Debug($"Scenario {module} from {playerName} stored as the first copy.");
                        ScenarioStoreSystem.CurrentScenarios[module] = theirs;
                    }

                    baselines[module] = nextBaseline;
                }
            }
            catch (Exception e)
            {
                notes.Clear();
                if (ShouldLog(logged, playerName, module, "error:" + e.Message))
                    LunaLog.Error($"Could not merge scenario {module} from {playerName}; the server copy is unchanged: {e.Message}");
            }

            //Kerbal Konstructs statics shared by players (docs/kk-live-sharing.md). Only written when one changes.
            foreach (var note in notes)
            {
                if (!note.StartsWith("refused", StringComparison.Ordinal))
                    LunaLog.Info($"[KKShare] {module} from {playerName}: {note}");
                else if (ShouldLog(logged, playerName, module, note))
                    LunaLog.Warning($"[KKShare] {module} from {playerName}: {note} (logged once)");
            }
        }

        private static bool ShouldLog(ConcurrentDictionary<string, byte> logged, string playerName, string module, string text)
        {
            if (logged == null)
                return true;
            if (logged.Count >= MaxLoggedPerConnection)
            {
                if (logged.TryAdd(SuppressedMarker, 0))
                    LunaLog.Warning($"[KKShare] {module} from {playerName}: more than {MaxLoggedPerConnection} distinct refusals or errors on this connection; further ones are not logged until the player reconnects.");
                return false;
            }
            return logged.TryAdd(module + "|" + text, 0);
        }
    }
}
