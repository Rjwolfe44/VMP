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

            /// <summary>Decoded bytes per byte on the wire (0 when nothing was sent).</summary>
            public double Ratio
            {
                get { return WireBytes > 0 ? (double)RawBytes / WireBytes : 0; }
            }
        }

        private static readonly ConcurrentDictionary<string, Counter> Sent = new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Counter> Received = new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<PersistentSyncDomainId, SnapshotCounter> Snapshots = new ConcurrentDictionary<PersistentSyncDomainId, SnapshotCounter>();
        private static DateTime _windowStartUtc = DateTime.UtcNow;

        private static readonly object RingLock = new object();
        private static readonly DateTime StartedAtUtc = DateTime.UtcNow;
        private static readonly TrafficWindowRing Ring = new TrafficWindowRing(TrafficWindowRing.DefaultCapacity);

        /// <summary>Length of one window of the ring behind <see cref="GetReport"/>.</summary>
        public static readonly TimeSpan RingWindowLength = TimeSpan.FromSeconds(60);

        static VmpTrafficStats()
        {
            // Baseline for the first ring window: every counter is still (almost) zero when the process starts.
            Ring.Add(CaptureSample(StartedAtUtc));
        }

        /// <summary>When this process first touched the counters (the start of the "since start" figures).</summary>
        public static DateTime StartedUtc
        {
            get { return StartedAtUtc; }
        }

        /// <summary>Reads every counter once. Allocates, so it is meant for once a minute, not for the hot path.</summary>
        public static TrafficSample CaptureSample(DateTime nowUtc)
        {
            var sample = new TrafficSample { TimeUtc = nowUtc };
            foreach (var kvp in Sent)
            {
                sample.Sent[kvp.Key] = new[] { Interlocked.Read(ref kvp.Value.Messages), Interlocked.Read(ref kvp.Value.Bytes) };
            }

            foreach (var kvp in Received)
            {
                sample.Received[kvp.Key] = new[] { Interlocked.Read(ref kvp.Value.Messages), Interlocked.Read(ref kvp.Value.Bytes) };
            }

            foreach (var kvp in Snapshots)
            {
                var counter = kvp.Value;
                sample.Snapshots[kvp.Key] = new[]
                {
                    Interlocked.Read(ref counter.Snapshots),
                    Interlocked.Read(ref counter.RawBytes),
                    Interlocked.Read(ref counter.WireBytes),
                    Interlocked.Read(ref counter.Encodings[SnapshotEncodings.Raw]),
                    Interlocked.Read(ref counter.Encodings[SnapshotEncodings.QuickLz]),
                    Interlocked.Read(ref counter.Encodings[SnapshotEncodings.Zstd]),
                    Interlocked.Read(ref counter.Encodings[SnapshotEncodings.ZstdDictionary]),
                };
            }

            return sample;
        }

        /// <summary>Closes the current one-minute window when at least <see cref="RingWindowLength"/> has passed since the last one. Cheap to call often.</summary>
        public static void RollWindows(DateTime nowUtc)
        {
            lock (RingLock)
            {
                if (nowUtc - Ring.LastSampleUtc < RingWindowLength) return;
                Ring.Add(CaptureSample(nowUtc));
            }
        }

        /// <summary>
        /// Per-type bytes and messages for the last closed window (about one minute), the last ten windows (about ten minutes) and since
        /// start, in and out, plus the snapshot compression table. Windows are closed by <see cref="RollWindows"/> (the server calls it every
        /// tick) so the newest figures can lag by up to a minute; each window carries its real length in seconds.
        /// </summary>
        public static TrafficReport GetReport(DateTime nowUtc)
        {
            lock (RingLock)
            {
                if (nowUtc - Ring.LastSampleUtc >= RingWindowLength) Ring.Add(CaptureSample(nowUtc));

                return new TrafficReport
                {
                    NowUtc = nowUtc,
                    StartedUtc = StartedAtUtc,
                    WindowCount = Ring.Count,
                    LastMinute = Ring.Latest(),
                    LastTenMinutes = Ring.Combine(TrafficWindowRing.DefaultCapacity),
                    SinceStart = TrafficWindowRing.Delta(new TrafficSample { TimeUtc = StartedAtUtc }, CaptureSample(nowUtc)),
                };
            }
        }

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

    /// <summary>All counters at one moment: message type to [messages, bytes]; snapshot domain to [snapshots, raw, wire, raw enc, quicklz, zstd, zstd+dict].</summary>
    public sealed class TrafficSample
    {
        public DateTime TimeUtc;
        public readonly Dictionary<string, long[]> Sent = new Dictionary<string, long[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, long[]> Received = new Dictionary<string, long[]>(StringComparer.Ordinal);
        public readonly Dictionary<PersistentSyncDomainId, long[]> Snapshots = new Dictionary<PersistentSyncDomainId, long[]>();
    }

    /// <summary>What moved between two samples.</summary>
    public sealed class TrafficWindow
    {
        public DateTime StartUtc;
        public DateTime EndUtc;
        public double Seconds;
        public List<VmpTrafficStats.Row> Sent = new List<VmpTrafficStats.Row>();
        public List<VmpTrafficStats.Row> Received = new List<VmpTrafficStats.Row>();
        public List<VmpTrafficStats.SnapshotRow> Snapshots = new List<VmpTrafficStats.SnapshotRow>();

        public long SentBytes { get { return Sum(Sent, false); } }
        public long SentMessages { get { return Sum(Sent, true); } }
        public long ReceivedBytes { get { return Sum(Received, false); } }
        public long ReceivedMessages { get { return Sum(Received, true); } }
        public double SentBytesPerSecond { get { return Seconds > 0 ? SentBytes / Seconds : 0; } }
        public double ReceivedBytesPerSecond { get { return Seconds > 0 ? ReceivedBytes / Seconds : 0; } }

        private static long Sum(List<VmpTrafficStats.Row> rows, bool messages)
        {
            long total = 0;
            foreach (var row in rows) total += messages ? row.Messages : row.Bytes;
            return total;
        }
    }

    public sealed class TrafficReport
    {
        public DateTime NowUtc;
        public DateTime StartedUtc;
        /// <summary>Closed windows currently in the ring (at most 10).</summary>
        public int WindowCount;
        /// <summary>Newest closed window; null until the first one closes.</summary>
        public TrafficWindow LastMinute;
        /// <summary>All closed windows in the ring added together; null until the first one closes.</summary>
        public TrafficWindow LastTenMinutes;
        public TrafficWindow SinceStart;
    }

    /// <summary>
    /// The last N one-minute windows. Each <see cref="Add"/> takes a cumulative sample, stores the difference to the previous sample as a
    /// window and drops the oldest window once full. Pure data and arithmetic, so it can be tested with made-up samples.
    /// </summary>
    public sealed class TrafficWindowRing
    {
        public const int DefaultCapacity = 10;

        private readonly object _lock = new object();
        private readonly TrafficWindow[] _windows;
        private int _count;
        private int _next;
        private TrafficSample _previous;

        public TrafficWindowRing(int capacity)
        {
            _windows = new TrafficWindow[Math.Max(1, capacity)];
        }

        public int Count
        {
            get { lock (_lock) return _count; }
        }

        public DateTime LastSampleUtc
        {
            get { lock (_lock) return _previous == null ? DateTime.MinValue : _previous.TimeUtc; }
        }

        /// <summary>The first sample only sets the baseline; every later one closes a window.</summary>
        public void Add(TrafficSample sample)
        {
            lock (_lock)
            {
                if (_previous != null)
                {
                    _windows[_next] = Delta(_previous, sample);
                    _next = (_next + 1) % _windows.Length;
                    if (_count < _windows.Length) _count++;
                }

                _previous = sample;
            }
        }

        public TrafficWindow Latest()
        {
            lock (_lock)
            {
                return _count == 0 ? null : _windows[(_next - 1 + _windows.Length) % _windows.Length];
            }
        }

        /// <summary>Oldest to newest.</summary>
        public List<TrafficWindow> Windows()
        {
            lock (_lock)
            {
                var list = new List<TrafficWindow>(_count);
                for (var i = _count - 1; i >= 0; i--) list.Add(_windows[(_next - 1 - i + _windows.Length) % _windows.Length]);
                return list;
            }
        }

        /// <summary>The newest <paramref name="count"/> windows added together (null when there is none).</summary>
        public TrafficWindow Combine(int count)
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                var take = Math.Min(Math.Max(1, count), _count);
                var sent = new Dictionary<string, VmpTrafficStats.Row>(StringComparer.Ordinal);
                var received = new Dictionary<string, VmpTrafficStats.Row>(StringComparer.Ordinal);
                var snapshots = new Dictionary<PersistentSyncDomainId, VmpTrafficStats.SnapshotRow>();
                var combined = new TrafficWindow();

                for (var i = 0; i < take; i++)
                {
                    var window = _windows[(_next - 1 - i + _windows.Length) % _windows.Length];
                    if (i == 0) combined.EndUtc = window.EndUtc;
                    combined.StartUtc = window.StartUtc;
                    combined.Seconds += window.Seconds;
                    foreach (var row in window.Sent) AddRow(sent, row);
                    foreach (var row in window.Received) AddRow(received, row);
                    foreach (var row in window.Snapshots) AddSnapshotRow(snapshots, row);
                }

                combined.Sent = SortedRows(sent);
                combined.Received = SortedRows(received);
                combined.Snapshots = new List<VmpTrafficStats.SnapshotRow>(snapshots.Values);
                combined.Snapshots.Sort((a, b) => b.RawBytes.CompareTo(a.RawBytes));
                return combined;
            }
        }

        /// <summary>Everything that was counted between the two samples (counters only grow, so a smaller value counts as zero).</summary>
        public static TrafficWindow Delta(TrafficSample previous, TrafficSample current)
        {
            var window = new TrafficWindow
            {
                StartUtc = previous.TimeUtc,
                EndUtc = current.TimeUtc,
                Seconds = Math.Max(0, (current.TimeUtc - previous.TimeUtc).TotalSeconds),
                Sent = DeltaRows(previous.Sent, current.Sent),
                Received = DeltaRows(previous.Received, current.Received),
            };

            foreach (var kvp in current.Snapshots)
            {
                long[] before;
                if (!previous.Snapshots.TryGetValue(kvp.Key, out before)) before = new long[7];
                var now = kvp.Value;
                var snapshots = Positive(now[0] - before[0]);
                if (snapshots == 0) continue;
                window.Snapshots.Add(new VmpTrafficStats.SnapshotRow
                {
                    Domain = kvp.Key,
                    Snapshots = snapshots,
                    RawBytes = Positive(now[1] - before[1]),
                    WireBytes = Positive(now[2] - before[2]),
                    Raw = Positive(now[3] - before[3]),
                    QuickLz = Positive(now[4] - before[4]),
                    Zstd = Positive(now[5] - before[5]),
                    ZstdDictionary = Positive(now[6] - before[6]),
                });
            }

            window.Snapshots.Sort((a, b) => b.RawBytes.CompareTo(a.RawBytes));
            return window;
        }

        private static List<VmpTrafficStats.Row> DeltaRows(Dictionary<string, long[]> previous, Dictionary<string, long[]> current)
        {
            var rows = new List<VmpTrafficStats.Row>();
            foreach (var kvp in current)
            {
                long[] before;
                if (!previous.TryGetValue(kvp.Key, out before)) before = new long[2];
                var messages = Positive(kvp.Value[0] - before[0]);
                var bytes = Positive(kvp.Value[1] - before[1]);
                if (messages > 0 || bytes > 0) rows.Add(new VmpTrafficStats.Row { Name = kvp.Key, Messages = messages, Bytes = bytes });
            }

            rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            return rows;
        }

        private static long Positive(long value)
        {
            return value > 0 ? value : 0;
        }

        private static void AddRow(Dictionary<string, VmpTrafficStats.Row> table, VmpTrafficStats.Row row)
        {
            VmpTrafficStats.Row total;
            if (!table.TryGetValue(row.Name, out total))
            {
                total = new VmpTrafficStats.Row { Name = row.Name };
                table[row.Name] = total;
            }

            total.Messages += row.Messages;
            total.Bytes += row.Bytes;
        }

        private static void AddSnapshotRow(Dictionary<PersistentSyncDomainId, VmpTrafficStats.SnapshotRow> table, VmpTrafficStats.SnapshotRow row)
        {
            VmpTrafficStats.SnapshotRow total;
            if (!table.TryGetValue(row.Domain, out total))
            {
                total = new VmpTrafficStats.SnapshotRow { Domain = row.Domain };
                table[row.Domain] = total;
            }

            total.Snapshots += row.Snapshots;
            total.RawBytes += row.RawBytes;
            total.WireBytes += row.WireBytes;
            total.Raw += row.Raw;
            total.QuickLz += row.QuickLz;
            total.Zstd += row.Zstd;
            total.ZstdDictionary += row.ZstdDictionary;
        }

        private static List<VmpTrafficStats.Row> SortedRows(Dictionary<string, VmpTrafficStats.Row> table)
        {
            var rows = new List<VmpTrafficStats.Row>(table.Values);
            rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            return rows;
        }
    }
}
