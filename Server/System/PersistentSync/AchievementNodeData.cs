using LunaConfigNode.CfgNode;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Server.System.PersistentSync
{
    /// <summary>
    /// One ProgressTracking achievement as the Achievements domain stores and ships it: the node's <b>contents</b>
    /// (values first, then child nodes, no header and no braces of its own), exactly what KSP's
    /// <c>ProgressNode.Save</c> + <c>ConfigNode.WriteNode</c> produce and what the clients send. The achievement's name
    /// travels separately as the snapshot Id and is written into the scenario as the child node's name
    /// (<c>Progress { Kerbin { ... } }</c>), which is the only shape KSP's <c>ProgressTree.Load</c> reads.
    ///
    /// Also owns the union merge. Progress only ever grows in KSP, so a client that reports an older picture of a node
    /// (it joined earlier, or has not applied the newest snapshot yet) must never take anything away: completion
    /// states only move up (reached, then completed manned/unmanned, then completed), numeric values such as
    /// <c>record</c> only move up, and child nodes the stored copy has but the report lacks are kept. A rolled-back
    /// record is what made the game pay the same speed/altitude record rewards again.
    /// </summary>
    internal static class AchievementNodeData
    {
        public const int MaxIdLength = 64;
        public const int MaxNodeBytes = 256 * 1024;
        public const int MaxAchievements = 512;
        private const int MaxDepth = 16;

        private const string ReachedKey = "reached";
        private const string CompletedKey = "completed";
        private const string CompletedMannedKey = "completedManned";
        private const string CompletedUnmannedKey = "completedUnmanned";

        /// <summary>Mutable, detached copy of a node's contents; nothing here points back into a LunaConfigNode tree.</summary>
        internal sealed class Entry
        {
            public List<KeyValuePair<string, string>> Values { get; } = new List<KeyValuePair<string, string>>();
            public List<KeyValuePair<string, Entry>> Nodes { get; } = new List<KeyValuePair<string, Entry>>();

            public bool IsEmpty => Values.Count == 0 && Nodes.Count == 0;
        }

        /// <summary>Ids end up as node names in the scenario file, so anything that could break the format is refused.</summary>
        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
            if (!string.Equals(id, id.Trim(), StringComparison.Ordinal)) return false;
            foreach (var c in id)
            {
                if (c < ' ' || c == '{' || c == '}' || c == '=' || c == '/' || c == '\\') return false;
            }

            return true;
        }

        /// <summary>
        /// Reads a client payload. Real clients send contents only; an older helper (and some tests) wrap the contents
        /// in <c>Id { ... }</c>, which is unwrapped here. Returns null for empty, oversized or unusable input.
        /// </summary>
        public static Entry ParsePayload(string id, byte[] data, int numBytes)
        {
            if (data == null || numBytes <= 0 || numBytes > data.Length || numBytes > MaxNodeBytes) return null;

            var root = new ConfigNode(Encoding.UTF8.GetString(data, 0, numBytes));
            if (root.GetAllValues().Count == 0)
            {
                var children = root.GetAllNodes();
                if (children.Count == 1 && string.Equals(children[0].Name, id, StringComparison.Ordinal))
                {
                    root = children[0];
                }
            }

            var entry = FromConfigNode(root, 0);
            return entry == null || entry.IsEmpty ? null : entry;
        }

        /// <summary>Reads stored canonical text (see <see cref="ToText"/>).</summary>
        public static Entry ParseText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var entry = FromConfigNode(new ConfigNode(text), 0);
            return entry == null || entry.IsEmpty ? null : entry;
        }

        public static Entry FromConfigNode(ConfigNode node, int depth)
        {
            if (node == null || depth > MaxDepth) return null;

            var entry = new Entry();
            foreach (var value in node.GetAllValues())
            {
                if (string.IsNullOrEmpty(value.Key)) continue;
                entry.Values.Add(new KeyValuePair<string, string>(value.Key, value.Value ?? string.Empty));
            }

            foreach (var child in node.GetAllNodes())
            {
                if (child == null || string.IsNullOrEmpty(child.Name)) continue;
                var childEntry = FromConfigNode(child, depth + 1);
                if (childEntry != null)
                {
                    entry.Nodes.Add(new KeyValuePair<string, Entry>(child.Name, childEntry));
                }
            }

            return entry;
        }

        /// <summary>Canonical contents text: values first, then child nodes, tab indented, "\n" line ends.</summary>
        public static string ToText(Entry entry)
        {
            var builder = new StringBuilder();
            Append(builder, entry, 0);
            return builder.ToString();
        }

        /// <summary>Contents text wrapped as a named child, ready to sit inside the scenario's <c>Progress</c> node.</summary>
        public static void AppendAsChild(StringBuilder builder, string id, string contentsText)
        {
            builder.Append(id).Append('\n').Append("{\n");
            foreach (var line in contentsText.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                builder.Append('\t').Append(line).Append('\n');
            }

            builder.Append("}\n");
        }

        private static void Append(StringBuilder builder, Entry entry, int depth)
        {
            var indent = new string('\t', depth);
            foreach (var value in entry.Values)
            {
                builder.Append(indent).Append(value.Key).Append(" = ").Append(value.Value).Append('\n');
            }

            foreach (var node in entry.Nodes)
            {
                builder.Append(indent).Append(node.Key).Append('\n');
                builder.Append(indent).Append("{\n");
                Append(builder, node.Value, depth + 1);
                builder.Append(indent).Append("}\n");
            }
        }

        /// <summary>Union of the stored copy and a report; see the type remarks for the rules. Neither argument is changed.</summary>
        public static Entry Merge(Entry existing, Entry incoming)
        {
            if (existing == null) return incoming;
            if (incoming == null) return existing;

            var merged = new Entry();
            merged.Values.AddRange(MergeValues(existing.Values, incoming.Values));
            merged.Nodes.AddRange(MergeNodes(existing.Nodes, incoming.Nodes));
            return merged;
        }

        private static List<KeyValuePair<string, Entry>> MergeNodes(List<KeyValuePair<string, Entry>> existing, List<KeyValuePair<string, Entry>> incoming)
        {
            var result = new List<KeyValuePair<string, Entry>>();
            var used = new bool[existing.Count];
            foreach (var node in incoming)
            {
                var match = -1;
                for (var i = 0; i < existing.Count; i++)
                {
                    if (!used[i] && string.Equals(existing[i].Key, node.Key, StringComparison.Ordinal))
                    {
                        match = i;
                        break;
                    }
                }

                if (match < 0)
                {
                    result.Add(node);
                    continue;
                }

                used[match] = true;
                result.Add(new KeyValuePair<string, Entry>(node.Key, Merge(existing[match].Value, node.Value)));
            }

            for (var i = 0; i < existing.Count; i++)
            {
                if (!used[i]) result.Add(existing[i]);
            }

            return result;
        }

        private static List<KeyValuePair<string, string>> MergeValues(List<KeyValuePair<string, string>> existing, List<KeyValuePair<string, string>> incoming)
        {
            var result = new List<KeyValuePair<string, string>>();

            var completion = MergeCompletion(existing, incoming);
            if (completion.HasValue) result.Add(completion.Value);

            var keys = new List<string>();
            foreach (var value in existing.Concat(incoming))
            {
                if (IsCompletionKey(value.Key) || keys.Contains(value.Key)) continue;
                keys.Add(value.Key);
            }

            foreach (var key in keys)
            {
                var oldValues = existing.Where(v => v.Key == key).Select(v => v.Value).ToList();
                var newValues = incoming.Where(v => v.Key == key).Select(v => v.Value).ToList();

                IEnumerable<string> chosen;
                if (newValues.Count == 0)
                {
                    chosen = oldValues;
                }
                else if (oldValues.Count == 0)
                {
                    chosen = newValues;
                }
                else if (oldValues.Count == 1 && newValues.Count == 1 && TryParse(oldValues[0], out var oldNumber) && TryParse(newValues[0], out var newNumber))
                {
                    chosen = new[] { newNumber >= oldNumber ? newValues[0] : oldValues[0] };
                }
                else if (oldValues.Count > 1 || newValues.Count > 1)
                {
                    chosen = oldValues.Concat(newValues).Distinct(StringComparer.Ordinal);
                }
                else
                {
                    chosen = newValues;
                }

                foreach (var value in chosen)
                {
                    result.Add(new KeyValuePair<string, string>(key, value));
                }
            }

            return result;
        }

        private static KeyValuePair<string, string>? MergeCompletion(List<KeyValuePair<string, string>> existing, List<KeyValuePair<string, string>> incoming)
        {
            var manned = false;
            var unmanned = false;
            var reached = false;
            var completedDates = new List<string>();
            var reachedDates = new List<string>();

            foreach (var value in existing.Concat(incoming))
            {
                switch (value.Key)
                {
                    case CompletedKey:
                        manned = true;
                        unmanned = true;
                        completedDates.Add(value.Value);
                        break;
                    case CompletedMannedKey:
                        manned = true;
                        completedDates.Add(value.Value);
                        break;
                    case CompletedUnmannedKey:
                        unmanned = true;
                        completedDates.Add(value.Value);
                        break;
                    case ReachedKey:
                        reached = true;
                        reachedDates.Add(value.Value);
                        break;
                }
            }

            // The stored date wins when the stored copy already has the resulting state, so a stale report neither
            // changes the node nor churns the revision; a new state takes the earliest date among its sources.
            if (manned && unmanned) return Completion(existing, CompletedKey, completedDates);
            if (manned) return Completion(existing, CompletedMannedKey, completedDates);
            if (unmanned) return Completion(existing, CompletedUnmannedKey, completedDates);
            if (reached) return Completion(existing, ReachedKey, reachedDates);
            return null;
        }

        private static KeyValuePair<string, string> Completion(List<KeyValuePair<string, string>> existing, string key, List<string> dates)
        {
            foreach (var value in existing)
            {
                if (value.Key == key) return value;
            }

            return new KeyValuePair<string, string>(key, Earliest(dates));
        }

        private static bool IsCompletionKey(string key)
        {
            return key == ReachedKey || key == CompletedKey || key == CompletedMannedKey || key == CompletedUnmannedKey;
        }

        private static string Earliest(List<string> dates)
        {
            string best = null;
            var bestNumber = double.MaxValue;
            foreach (var date in dates)
            {
                if (TryParse(date, out var number))
                {
                    if (best == null || number < bestNumber)
                    {
                        best = date;
                        bestNumber = number;
                    }
                }
                else if (best == null)
                {
                    best = date;
                }
            }

            return best ?? string.Empty;
        }

        private static bool TryParse(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !double.IsNaN(number) && !double.IsInfinity(number);
        }
    }
}
