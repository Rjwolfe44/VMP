using LunaConfigNode.CfgNode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server.System.Scenario
{
    /// <summary>
    /// Three-way merge of scenario ConfigNodes, so two players saving the same scenario do not wipe each
    /// other's changes. For each upload we compare what the client sent (<c>theirs</c>) with what the server
    /// handed that client when it last synced (<c>baseNode</c>): anything the client did not change is left as
    /// the server (<c>ours</c>) has it, and only the client's own changes are applied on top.
    /// </summary>
    /// <remarks>
    /// Only used for scenario modules listed in <see cref="IsMergedModule"/>. Other modules keep the
    /// whole-blob overwrite of <see cref="ScenarioDataUpdater.RawConfigNodeInsertOrUpdate"/>.
    /// </remarks>
    public static class ScenarioNodeMerge
    {
        /// <summary>
        /// Scenario modules where concurrent players hold independent copies of shared state.
        /// Kerbal Konstructs keeps launch site / facility open state, mission logs and placed buildings here.
        /// </summary>
        private static readonly HashSet<string> MergedModules = new HashSet<string>(StringComparer.Ordinal)
        {
            "KerbalKonstructsSettings"
        };

        /// <summary>
        /// Containers that only ever grow through the merge: neither the container nor any child is removed because
        /// a client no longer has it. Facilities and LaunchSites come from the mod's own files, and a client that
        /// lacks a mod file simply does not save that entry. KKBuildings, KKGroups and KKMapDecals hold what players
        /// placed; a client missing a model or body silently drops those on load, so its next save would delete them
        /// for everybody. The price is that deleting a placed object in-game does not stick (it returns on rejoin).
        /// </summary>
        private static readonly HashSet<string> AdditiveContainers = new HashSet<string>(StringComparer.Ordinal)
        {
            "Facilities", "LaunchSites", "KKBuildings", "KKGroups", "KKMapDecals"
        };

        /// <summary>Additive containers whose children are separate objects that may share a node name.</summary>
        private static readonly HashSet<string> RepeatedChildContainers = new HashSet<string>(StringComparer.Ordinal)
        {
            "KKBuildings", "KKGroups", "KKMapDecals"
        };

        /// <summary>
        /// Children that repeat under one name (every placed building is an "Instance") and so need an identifying
        /// value to be matched between the three copies. Children without that value are matched by their full content.
        /// </summary>
        private static readonly Dictionary<string, string[]> IdentityKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            //KK gives every saved static a GUID.
            ["Instance"] = new[] { "UUID" },
            //KK's own key for a group is body name + group name. Map decals have no unique value (the default name
            //repeats between players), so they are matched by content: editing one adds a copy instead of changing it.
            ["Group"] = new[] { "CelestialBody", "Group" }
        };

        /// <summary>Counters merged as server + (client's change), so two players each adding one end up at two.</summary>
        private static readonly HashSet<string> AdditiveCounters = new HashSet<string>(StringComparer.Ordinal)
        {
            "MissionCount"
        };

        public static bool IsMergedModule(string moduleName) => moduleName != null && MergedModules.Contains(moduleName);

        /// <summary>
        /// Applies the client's changes (<paramref name="theirs"/> relative to <paramref name="baseNode"/>) to
        /// <paramref name="ours"/> in place. <paramref name="theirs"/> is consumed: subtrees added by the client
        /// are moved into <paramref name="ours"/>, so never reuse it afterwards. <paramref name="baseNode"/> is
        /// only read. Returns the number of values/nodes that were added, changed or removed.
        /// </summary>
        public static int Merge(ConfigNode baseNode, ConfigNode theirs, ConfigNode ours)
        {
            return MergeInto(baseNode, theirs, ours, null, false);
        }

        /// <summary>
        /// For a client with no baseline (it never received this scenario from the server, so we cannot tell what it
        /// changed): only adds values and nodes the server lacks and never overwrites what the server has.
        /// <paramref name="theirs"/> is consumed like in <see cref="Merge"/>.
        /// </summary>
        public static int AddMissing(ConfigNode theirs, ConfigNode ours)
        {
            return MergeInto(null, theirs, ours, null, true);
        }

        private static int MergeInto(ConfigNode baseNode, ConfigNode theirs, ConfigNode ours, string nodeName, bool addOnly)
        {
            var changes = 0;

            //Plain values: a value the client did not touch keeps whatever the server has now.
            foreach (var key in KeysOf(baseNode, theirs))
            {
                var b = ValuesOf(baseNode, key);
                var t = ValuesOf(theirs, key);

                if (addOnly)
                {
                    if (ValuesOf(ours, key).Count == 0 && t.Count > 0)
                    {
                        SetValues(ours, key, t);
                        changes++;
                    }
                    continue;
                }

                if (b.SequenceEqual(t, StringComparer.Ordinal))
                    continue;

                if (AdditiveCounters.Contains(key) && TryMergeCounter(b, t, ValuesOf(ours, key), out var merged))
                    t = new List<string> { merged };

                SetValues(ours, key, t);
                changes++;
            }

            //Child nodes, grouped by name.
            var inAdditive = nodeName != null && AdditiveContainers.Contains(nodeName);
            var inRepeatedChildren = nodeName != null && RepeatedChildContainers.Contains(nodeName);
            foreach (var name in NodeNamesOf(baseNode, theirs))
            {
                var bl = NodesOf(baseNode, name);
                var tl = NodesOf(theirs, name);
                var ol = NodesOf(ours, name);

                if (inRepeatedChildren || IdentityKeys.ContainsKey(name))
                {
                    //Siblings that are separate objects even when they share a name: a single node is no proof of identity.
                    changes += MergeKeyed(bl, tl, ol, name, ours, addOnly);
                }
                else if (bl.Count <= 1 && tl.Count <= 1 && ol.Count <= 1)
                {
                    changes += MergeSingleNode(bl.FirstOrDefault(), tl.FirstOrDefault(), ol.FirstOrDefault(), name, ours, inAdditive, addOnly);
                }
                else
                {
                    //Repeated sibling names with no identity to match on: treated as one unit.
                    if (SameGroup(bl, tl) || addOnly)
                    {
                        if (addOnly && ol.Count == 0 && tl.Count > 0)
                        {
                            foreach (var t in tl)
                                ours.AddNode(t);
                            changes++;
                        }
                        continue;
                    }

                    //RemoveNode(string) drops every child with that name; the overload taking a node removes nothing.
                    ours.RemoveNode(name);
                    foreach (var t in tl)
                        ours.AddNode(t);
                    changes++;
                }
            }

            return changes;
        }

        private static int MergeSingleNode(ConfigNode b, ConfigNode t, ConfigNode o, string name, ConfigNode oursParent, bool parentIsAdditive, bool addOnly)
        {
            if (b == null && t == null)
                return 0;

            if (t == null)
            {
                //The client no longer has the node: a deliberate delete, unless it is mod-defined or placed content.
                if (parentIsAdditive || AdditiveContainers.Contains(name) || o == null)
                    return 0;

                //Someone else changed it after this client last synced: their change wins over the delete.
                if (!Same(o, b))
                    return 0;

                oursParent.RemoveNode(name);
                return 1;
            }

            if (o == null)
            {
                //Already gone on the server. If the client did not touch it the removal stands; if it did, its edit wins.
                if (b != null && Same(b, t))
                    return 0;

                oursParent.AddNode(t);
                return 1;
            }

            if (addOnly)
                return MergeInto(null, t, o, name, true);

            if (b != null && Same(b, t))
                return 0;

            return MergeInto(b, t, o, name, false);
        }

        /// <summary>
        /// Same-named siblings matched by identity. Never removes: a missing sibling in the client's copy only means the
        /// client could not load it (or never had it), see <see cref="AdditiveContainers"/>.
        /// </summary>
        private static int MergeKeyed(List<ConfigNode> bl, List<ConfigNode> tl, List<ConfigNode> ol, string name, ConfigNode oursParent, bool addOnly)
        {
            IdentityKeys.TryGetValue(name, out var identityKeys);
            var b = Index(bl, identityKeys);
            var t = Index(tl, identityKeys);
            var o = Index(ol, identityKeys);
            var changes = 0;

            foreach (var key in t.Keys.ToList())
            {
                b.TryGetValue(key, out var bn);
                var tn = t[key];

                if (!o.TryGetValue(key, out var on))
                {
                    //Not on the server: new from this client (or removed there, which cannot happen since we never remove).
                    if (!addOnly && bn != null && Same(bn, tn))
                        continue;

                    oursParent.AddNode(tn);
                    changes++;
                    continue;
                }

                if (addOnly)
                    changes += MergeInto(null, tn, on, name, true);
                else if (bn == null || !Same(bn, tn))
                    changes += MergeInto(bn, tn, on, name, false);
            }

            return changes;
        }

        /// <summary>
        /// Maps identity to node. The identity is the node's values for <paramref name="identityKeys"/> when it has them
        /// all, else the node's full content. Two siblings with the same identity are kept apart (#2, #3, ...), so they
        /// never collapse into one.
        /// </summary>
        private static Dictionary<string, ConfigNode> Index(List<ConfigNode> nodes, string[] identityKeys)
        {
            var result = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                var parts = identityKeys?.Select(k => node.GetValue(k)?.Value).ToList();
                var key = parts == null || parts.Any(string.IsNullOrEmpty)
                    ? "content:" + Canonical(node)
                    : "id:" + string.Join("|", parts);
                var unique = key;
                for (var n = 2; result.ContainsKey(unique); n++)
                    unique = key + "#" + n;
                result[unique] = node;
            }
            return result;
        }

        /// <summary>Text form of a node that does not depend on the order of different keys or names.</summary>
        private static string Canonical(ConfigNode node)
        {
            var sb = new StringBuilder();
            AppendCanonical(node, sb);
            return sb.ToString();
        }

        private static void AppendCanonical(ConfigNode node, StringBuilder sb)
        {
            sb.Append('{');
            foreach (var key in KeysOf(node, null).OrderBy(k => k, StringComparer.Ordinal))
            {
                foreach (var value in ValuesOf(node, key))
                    sb.Append(key).Append('=').Append(value).Append(';');
            }
            foreach (var name in NodeNamesOf(node, null).OrderBy(n => n, StringComparer.Ordinal))
            {
                foreach (var child in NodesOf(node, name))
                {
                    sb.Append(name);
                    AppendCanonical(child, sb);
                }
            }
            sb.Append('}');
        }

        private static bool TryMergeCounter(List<string> baseValues, List<string> theirValues, List<string> ourValues, out string merged)
        {
            merged = null;
            if (theirValues.Count != 1 || ourValues.Count != 1 || baseValues.Count > 1)
                return false;

            var b = 0;
            if (baseValues.Count == 1 && !int.TryParse(baseValues[0], out b))
                return false;
            if (!int.TryParse(theirValues[0], out var t) || !int.TryParse(ourValues[0], out var o))
                return false;

            merged = Math.Max(0, o + (t - b)).ToString();
            return true;
        }

        /// <summary>
        /// True when two nodes hold the same values and children. Order of different keys/names is ignored, order
        /// of repeated keys/names is not.
        /// </summary>
        public static bool Same(ConfigNode a, ConfigNode b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;

            var keys = KeysOf(a, b);
            if (keys.Any(k => !ValuesOf(a, k).SequenceEqual(ValuesOf(b, k), StringComparer.Ordinal)))
                return false;

            foreach (var name in NodeNamesOf(a, b))
            {
                if (!SameGroup(NodesOf(a, name), NodesOf(b, name)))
                    return false;
            }

            return true;
        }

        private static bool SameGroup(List<ConfigNode> a, List<ConfigNode> b)
        {
            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!Same(a[i], b[i]))
                    return false;
            }

            return true;
        }

        private static void SetValues(ConfigNode node, string key, List<string> values)
        {
            var existing = node.GetValues(key);
            if (existing.Count == 1 && values.Count == 1)
            {
                node.UpdateValue(key, values[0]);
                return;
            }

            //Whether RemoveValue drops one or all entries of a repeated key, this ends with none left.
            for (var guard = existing.Count; guard > 0 && node.GetValues(key).Count > 0; guard--)
                node.RemoveValue(key);

            foreach (var value in values)
                node.CreateValue(new CfgNodeValue<string, string>(key, value));
        }

        private static List<string> KeysOf(ConfigNode a, ConfigNode b)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in new[] { a, b })
            {
                if (node == null)
                    continue;
                foreach (var value in node.GetAllValues())
                {
                    if (seen.Add(value.Key))
                        keys.Add(value.Key);
                }
            }
            return keys;
        }

        private static List<string> NodeNamesOf(ConfigNode a, ConfigNode b)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in new[] { a, b })
            {
                if (node == null)
                    continue;
                foreach (var child in node.GetAllNodes())
                {
                    if (seen.Add(child.Name))
                        names.Add(child.Name);
                }
            }
            return names;
        }

        private static List<string> ValuesOf(ConfigNode node, string key)
        {
            return node == null
                ? new List<string>()
                : node.GetValues(key).Select(v => v.Value).ToList();
        }

        private static List<ConfigNode> NodesOf(ConfigNode node, string name)
        {
            return node == null
                ? new List<ConfigNode>()
                : node.GetAllNodes().Where(n => n.Name == name).ToList();
        }
    }
}
