using LmpCommon.PersistentSync;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace LmpCommon.Stats
{
    /// <summary>
    /// Bytes and message counts per message type, in each direction, for the client and the server. Until this existed the
    /// only measurement was the position-update counter, so "what uses the bandwidth" could not be answered from a log.
    /// Each entry keeps a running total and a window that <see cref="TakeWindowSummary"/> reads and resets, so a periodic log
    /// line shows the rate for the last interval. Also tracks how much the snapshot encodings saved, per domain.
    /// Counting is a dictionary lookup plus two interlocked adds; it is safe to call from any thread.
    /// </summary>
    public static class VmpTrafficStats
    {
        private sealed class Counter
        {
            public long Messages;
            public long Bytes;
            public long WindowMessages;
            public long WindowBytes;

            public void Add(int bytes)
            {
                Interlocked.Increment(ref Messages);
                Interlocked.Add(ref Bytes, bytes);
                Interlocked.Increment(ref WindowMessages);
                Interlocked.Add(ref WindowBytes, bytes);
            }
        }

        private sealed class SnapshotCounter
        {
            public long Snapshots;
            public long RawBytes;
            public long WireBytes;
            public long WindowSnapshots;
            public long WindowRawBytes;
            public long WindowWireBytes;
            public readonly long[] Encodings = new long[4];
            public readonly long[] WindowEncodings = new long[4];
        }

        /// <summary>One line of the breakdown.</summary>
        public sealed class Row
        {
            public string Name;
            public long Messages;
            public long Bytes;
        }

        /// <summary>One domain's snapshot compression figures.</summary>
        public sealed class SnapshotRow
        {
            public PersistentSyncDomainId Domain;
            public long Snapshots;
            public long RawBytes;
            public long WireBytes;
            public long Raw;
            public long QuickLz;
            public long Zstd;
            public long ZstdDictionary;
        }

        private static readonly ConcurrentDictionary<string, Counter> Sent = new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Counter> Received = new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<PersistentSyncDomainId, SnapshotCounter> Snapshots = new ConcurrentDictionary<PersistentSyncDomainId, SnapshotCounter>();
        private static DateTime _windowStartUtc = DateTime.UtcNow;

        public static void RecordSent(string messageType, int bytes)
        {
            if (string.IsNullOrEmpty(messageType)) return;
            Sent.GetOrAdd(messageType, _ => new Counter()).Add(bytes);
        }

        public static void RecordReceived(string messageType, int bytes)
        {
            if (string.IsNullOrEmpty(messageType)) return;
            Received.GetOrAdd(messageType, _ => new Counter()).Add(bytes);
        }

        /// <summary>A snapshot payload went out (server) or came in (client): its decoded size and what it cost on the wire.</summary>
        public static void RecordSnapshot(PersistentSyncDomainId domain, byte encoding, int rawBytes, int wireBytes)
        {
            var counter = Snapshots.GetOrAdd(domain, _ => new SnapshotCounter());
            Interlocked.Increment(ref counter.Snapshots);
            Interlocked.Add(ref counter.RawBytes, rawBytes);
            Interlocked.Add(ref counter.WireBytes, wireBytes);
            Interlocked.Increment(ref counter.WindowSnapshots);
            Interlocked.Add(ref counter.WindowRawBytes, rawBytes);
            Interlocked.Add(ref counter.WindowWireBytes, wireBytes);
            if (encoding < counter.Encodings.Length)
            {
                Interlocked.Increment(ref counter.Encodings[encoding]);
                Interlocked.Increment(ref counter.WindowEncodings[encoding]);
            }
        }

        /// <summary>Totals since start, biggest first.</summary>
        public static List<Row> GetSentTotals() => Rows(Sent, window: false);

        public static List<Row> GetReceivedTotals() => Rows(Received, window: false);

        public static List<SnapshotRow> GetSnapshotTotals()
        {
            return Snapshots.Select(kvp => new SnapshotRow
            {
                Domain = kvp.Key,
                Snapshots = Interlocked.Read(ref kvp.Value.Snapshots),
                RawBytes = Interlocked.Read(ref kvp.Value.RawBytes),
                WireBytes = Interlocked.Read(ref kvp.Value.WireBytes),
                Raw = Interlocked.Read(ref kvp.Value.Encodings[SnapshotEncodings.Raw]),
                QuickLz = Interlocked.Read(ref kvp.Value.Encodings[SnapshotEncodings.QuickLz]),
                Zstd = Interlocked.Read(ref kvp.Value.Encodings[SnapshotEncodings.Zstd]),
                ZstdDictionary = Interlocked.Read(ref kvp.Value.Encodings[SnapshotEncodings.ZstdDictionary]),
            }).OrderByDescending(r => r.RawBytes).ToList();
        }

        /// <summary>
        /// One log line for everything counted since the previous call (null when nothing moved), then resets the window.
        /// Example: <c>[VMP Traffic 60s] out 1.4 MB: PersistentSyncSnapshotMsgData 900 KB/12, ... | in 80 KB: ... | snapshots: Contracts 12x 1.5 MB raw -> 84 KB (18x; 11 zstd+dict, 1 zstd)</c>
        /// </summary>
        public static string TakeWindowSummary(int topTypes = 6)
        {
            var now = DateTime.UtcNow;
            var seconds = Math.Max(1, (int)Math.Round((now - _windowStartUtc).TotalSeconds));
            _windowStartUtc = now;

            var sent = Rows(Sent, window: true, reset: true);
            var received = Rows(Received, window: true, reset: true);
            var snapshots = TakeSnapshotWindow();
            if (sent.Count == 0 && received.Count == 0 && snapshots.Count == 0) return null;

            var builder = new StringBuilder();
            builder.Append("[VMP Traffic ").Append(seconds).Append("s] ");
            builder.Append("out ").Append(FormatBytes(sent.Sum(r => r.Bytes))).Append(": ").Append(Describe(sent, topTypes));
            builder.Append(" | in ").Append(FormatBytes(received.Sum(r => r.Bytes))).Append(": ").Append(Describe(received, topTypes));
            if (snapshots.Count > 0)
            {
                builder.Append(" | snapshots: ");
                builder.Append(string.Join(", ", snapshots.Take(4).Select(DescribeSnapshots)));
            }

            return builder.ToString();
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
        }

        private static string Describe(List<Row> rows, int top)
        {
            if (rows.Count == 0) return "-";
            return string.Join(", ", rows.Take(top).Select(r => Short(r.Name) + " " + FormatBytes(r.Bytes) + "/" + r.Messages));
        }

        private static string Short(string name)
        {
            const string suffix = "MsgData";
            return name.EndsWith(suffix, StringComparison.Ordinal) ? name.Substring(0, name.Length - suffix.Length) : name;
        }

        private static string DescribeSnapshots(SnapshotRow row)
        {
            var ratio = row.WireBytes > 0 ? (double)row.RawBytes / row.WireBytes : 0;
            var parts = new List<string>();
            if (row.ZstdDictionary > 0) parts.Add(row.ZstdDictionary + " zstd+dict");
            if (row.Zstd > 0) parts.Add(row.Zstd + " zstd");
            if (row.QuickLz > 0) parts.Add(row.QuickLz + " quicklz");
            if (row.Raw > 0) parts.Add(row.Raw + " raw");
            return row.Domain + " " + row.Snapshots + "x " + FormatBytes(row.RawBytes) + " raw -> " + FormatBytes(row.WireBytes) + " (" + ratio.ToString("0.#") + "x; " + string.Join(", ", parts) + ")";
        }

        private static List<Row> Rows(ConcurrentDictionary<string, Counter> table, bool window, bool reset = false)
        {
            var rows = new List<Row>();
            foreach (var kvp in table)
            {
                long messages;
                long bytes;
                if (window)
                {
                    messages = reset ? Interlocked.Exchange(ref kvp.Value.WindowMessages, 0) : Interlocked.Read(ref kvp.Value.WindowMessages);
                    bytes = reset ? Interlocked.Exchange(ref kvp.Value.WindowBytes, 0) : Interlocked.Read(ref kvp.Value.WindowBytes);
                }
                else
                {
                    messages = Interlocked.Read(ref kvp.Value.Messages);
                    bytes = Interlocked.Read(ref kvp.Value.Bytes);
                }

                if (messages > 0 || bytes > 0)
                {
                    rows.Add(new Row { Name = kvp.Key, Messages = messages, Bytes = bytes });
                }
            }

            rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            return rows;
        }

        private static List<SnapshotRow> TakeSnapshotWindow()
        {
            var rows = new List<SnapshotRow>();
            foreach (var kvp in Snapshots)
            {
                var counter = kvp.Value;
                var snapshots = Interlocked.Exchange(ref counter.WindowSnapshots, 0);
                var raw = Interlocked.Exchange(ref counter.WindowRawBytes, 0);
                var wire = Interlocked.Exchange(ref counter.WindowWireBytes, 0);
                var row = new SnapshotRow
                {
                    Domain = kvp.Key,
                    Snapshots = snapshots,
                    RawBytes = raw,
                    WireBytes = wire,
                    Raw = Interlocked.Exchange(ref counter.WindowEncodings[SnapshotEncodings.Raw], 0),
                    QuickLz = Interlocked.Exchange(ref counter.WindowEncodings[SnapshotEncodings.QuickLz], 0),
                    Zstd = Interlocked.Exchange(ref counter.WindowEncodings[SnapshotEncodings.Zstd], 0),
                    ZstdDictionary = Interlocked.Exchange(ref counter.WindowEncodings[SnapshotEncodings.ZstdDictionary], 0),
                };
                if (snapshots > 0)
                {
                    rows.Add(row);
                }
            }

            rows.Sort((a, b) => b.RawBytes.CompareTo(a.RawBytes));
            return rows;
        }
    }
}
