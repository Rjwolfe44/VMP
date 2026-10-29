using LmpCommon.Enums;
using LmpCommon.PersistentSync;
using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Log;
using Server.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server.System.PersistentSync
{
    /// <summary>
    /// Sole writer of <c>ProgressTracking/Progress/*</c>. The canonical state is one entry per achievement (the node's
    /// Id and its contents, see <see cref="AchievementNodeData"/>), written as <c>Progress { Id { contents } }</c>.
    ///
    /// History: this store used to write each achievement's contents under <c>Progress</c> without its name, and every
    /// write appended another unnamed wrapper node to the file (699 of them, 900 KB on the train server) that the next
    /// load could not read. After every restart the server's achievements were empty, and the clients (which had no
    /// name to load them under either) re-earned and re-collected every milestone and record reward each session.
    /// Names that were lost cannot be recovered, so a legacy file loads as empty and is rewritten clean.
    /// </summary>
    public sealed class AchievementsPersistentSyncDomainStore : ScenarioSyncDomainStore<AchievementsPersistentSyncDomainStore.Canonical>
    {
        private const string ProgressNodeName = "Progress";

        public override PersistentSyncDomainId DomainId => PersistentSyncDomainId.Achievements;
        public override PersistentAuthorityPolicy AuthorityPolicy => PersistentAuthorityPolicy.AnyClientIntent;
        protected override string ScenarioName => "ProgressTracking";

        public override bool AuthorizeIntent(ClientStructure client, byte[] payload, int numBytes) => AuthorizeByPolicy(client);

        protected override Canonical CreateEmpty()
        {
            return new Canonical(new SortedDictionary<string, AchievementSnapshotInfo>(StringComparer.Ordinal));
        }

        protected override Canonical LoadCanonical(ConfigNode scenario, bool createdFromScratch)
        {
            var entries = new SortedDictionary<string, AchievementNodeData.Entry>(StringComparer.Ordinal);
            var droppedLegacyNodes = 0;

            if (scenario != null)
            {
                droppedLegacyNodes += scenario.GetAllNodes().Count(node => string.IsNullOrEmpty(node.Name));

                foreach (var progress in scenario.GetNodes(ProgressNodeName).Select(node => node.Value).Where(node => node != null))
                {
                    foreach (var child in progress.GetAllNodes())
                    {
                        if (!AchievementNodeData.IsValidId(child.Name))
                        {
                            droppedLegacyNodes++;
                            continue;
                        }

                        var entry = AchievementNodeData.FromConfigNode(child, 0);
                        if (entry == null || entry.IsEmpty) continue;

                        entries[child.Name] = entries.TryGetValue(child.Name, out var existing)
                            ? AchievementNodeData.Merge(existing, entry)
                            : entry;
                    }
                }
            }

            var map = new SortedDictionary<string, AchievementSnapshotInfo>(StringComparer.Ordinal);
            foreach (var kvp in entries)
            {
                map[kvp.Key] = CreateInfo(kvp.Key, AchievementNodeData.ToText(kvp.Value));
            }

            LunaLog.Normal($"[PersistentSync] Achievements LoadFromPersistence: achievements={map.Count} unreadableLegacyNodes={droppedLegacyNodes}");
            return new Canonical(map);
        }

        /// <summary>Rewrites a save that still holds the old unnamed wrapper nodes (or several Progress nodes).</summary>
        protected override bool ShouldWriteBackAfterLoad(Canonical loaded, ConfigNode scenario)
        {
            if (scenario.GetAllNodes().Any(node => string.IsNullOrEmpty(node.Name))) return true;

            var progressNodes = scenario.GetNodes(ProgressNodeName).Select(node => node.Value).Where(node => node != null).ToList();
            if (progressNodes.Count != 1) return true;

            return progressNodes[0].GetAllNodes().Any(child => !AchievementNodeData.IsValidId(child.Name));
        }

        protected override ReduceResult<Canonical> ReduceIntent(ClientStructure client, Canonical current, byte[] payload, int numBytes, string reason, bool isServerMutation)
        {
            var records = AchievementSnapshotPayloadSerializer.Deserialize(payload) ?? new AchievementSnapshotInfo[0];
            var next = new SortedDictionary<string, AchievementSnapshotInfo>(current.Achievements, StringComparer.Ordinal);

            foreach (var record in records)
            {
                if (record == null || !AchievementNodeData.IsValidId(record.Id)) continue;

                var incoming = AchievementNodeData.ParsePayload(record.Id, record.Data, record.NumBytes);
                if (incoming == null) continue;

                if (next.TryGetValue(record.Id, out var stored))
                {
                    var merged = AchievementNodeData.Merge(AchievementNodeData.ParseText(Encoding.UTF8.GetString(stored.Data, 0, stored.NumBytes)), incoming);
                    next[record.Id] = CreateInfo(record.Id, AchievementNodeData.ToText(merged));
                }
                else if (next.Count < AchievementNodeData.MaxAchievements)
                {
                    next[record.Id] = CreateInfo(record.Id, AchievementNodeData.ToText(incoming));
                }
            }

            return ReduceResult<Canonical>.Accept(new Canonical(next));
        }

        protected override ConfigNode WriteCanonical(ConfigNode scenario, Canonical canonical)
        {
            // RemoveNode(ConfigNode) never removes from the saved text (see ConfigNodeRemoval), so removal is by name. The
            // empty name takes out the unnamed wrappers older builds left behind.
            ConfigNodeRemoval.RemoveAllNodes(scenario, ProgressNodeName);
            ConfigNodeRemoval.RemoveAllNodes(scenario, string.Empty);

            scenario.AddNode(new ConfigNode(BuildProgressChildrenText(canonical.Achievements.Values)) { Name = ProgressNodeName });
            return scenario;
        }

        protected override byte[] SerializeSnapshot(Canonical canonical)
        {
            return AchievementSnapshotPayloadSerializer.Serialize(canonical.Achievements.Values.Select(CloneInfo).ToArray());
        }

        protected override bool AreEquivalent(Canonical a, Canonical b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Achievements.Count != b.Achievements.Count) return false;

            foreach (var kvp in a.Achievements)
            {
                if (!b.Achievements.TryGetValue(kvp.Key, out var other) || !RecordsAreEqual(kvp.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        private static AchievementSnapshotInfo CreateInfo(string id, string contentsText)
        {
            var bytes = Encoding.UTF8.GetBytes(contentsText);
            return new AchievementSnapshotInfo { Id = id, NumBytes = bytes.Length, Data = bytes };
        }

        private static bool RecordsAreEqual(AchievementSnapshotInfo left, AchievementSnapshotInfo right)
        {
            if (left == null || right == null) return left == right;
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
                   string.Equals(Encoding.UTF8.GetString(left.Data, 0, left.NumBytes), Encoding.UTF8.GetString(right.Data, 0, right.NumBytes), StringComparison.Ordinal);
        }

        private static AchievementSnapshotInfo CloneInfo(AchievementSnapshotInfo source)
        {
            var data = new byte[source.NumBytes];
            Buffer.BlockCopy(source.Data, 0, data, 0, source.NumBytes);
            return new AchievementSnapshotInfo
            {
                Id = source.Id,
                NumBytes = source.NumBytes,
                Data = data
            };
        }

        private static string BuildProgressChildrenText(IEnumerable<AchievementSnapshotInfo> achievements)
        {
            var builder = new StringBuilder();
            foreach (var achievement in achievements)
            {
                AchievementNodeData.AppendAsChild(builder, achievement.Id, Encoding.UTF8.GetString(achievement.Data, 0, achievement.NumBytes));
            }

            return builder.ToString();
        }

        /// <summary>Typed canonical state: achievements keyed by Id (ordinal, sorted for deterministic iteration).</summary>
        public sealed class Canonical
        {
            public Canonical(SortedDictionary<string, AchievementSnapshotInfo> achievements)
            {
                Achievements = achievements ?? new SortedDictionary<string, AchievementSnapshotInfo>(StringComparer.Ordinal);
            }

            public SortedDictionary<string, AchievementSnapshotInfo> Achievements { get; }
        }
    }
}
