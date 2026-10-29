using LmpCommon.Time;
using System;
using System.Threading;

namespace LmpClient
{
    /// <summary>
    /// Lightweight, thread-safe network statistics collector for VMP.
    /// Tracks position-update bytes/messages sent and received plus queue-drop events.
    /// Logs a summary every <see cref="LogIntervalMs"/> milliseconds via <see cref="LunaLog"/>.
    /// Call <see cref="MaybeLog"/> from any per-frame update path to trigger periodic output.
    /// </summary>
    public static class VmpNetStats
    {
        private const double LogIntervalMs = 30_000;

        private static long _bytesSent;
        private static long _bytesReceived;
        private static long _messagesSent;
        private static long _messagesReceived;
        private static long _queueDrops;
        private static long _peakQueueDepth;

        private static long _totalBytesSent;
        private static long _totalBytesReceived;
        private static long _totalMessagesSent;
        private static long _totalMessagesReceived;
        private static long _totalQueueDrops;

        private static long _lastWindowMessagesSent;
        private static long _lastWindowMessagesReceived;
        private static long _lastWindowBytesSent;
        private static long _lastWindowBytesReceived;
        private static long _lastWindowQueueDrops;
        private static long _lastWindowPeakQueueDepth;

        private static DateTime _lastLog = DateTime.MinValue;

        public struct Snapshot
        {
            public long TotalMessagesSent;
            public long TotalMessagesReceived;
            public long TotalBytesSent;
            public long TotalBytesReceived;
            public long TotalQueueDrops;
            public long RecentMessagesSent;
            public long RecentMessagesReceived;
            public long RecentBytesSent;
            public long RecentBytesReceived;
            public long RecentQueueDrops;
            public long RecentPeakQueueDepth;
            public long LiveMessagesSent;
            public long LiveMessagesReceived;
            public long LivePeakQueueDepth;
            public double RecentWindowSeconds;
        }

        /// <summary>Immutable point-in-time counters for the sync dashboard.</summary>
        public static Snapshot GetSnapshot()
        {
            return new Snapshot
            {
                TotalMessagesSent = Interlocked.Read(ref _totalMessagesSent),
                TotalMessagesReceived = Interlocked.Read(ref _totalMessagesReceived),
                TotalBytesSent = Interlocked.Read(ref _totalBytesSent),
                TotalBytesReceived = Interlocked.Read(ref _totalBytesReceived),
                TotalQueueDrops = Interlocked.Read(ref _totalQueueDrops),
                RecentMessagesSent = Interlocked.Read(ref _lastWindowMessagesSent),
                RecentMessagesReceived = Interlocked.Read(ref _lastWindowMessagesReceived),
                RecentBytesSent = Interlocked.Read(ref _lastWindowBytesSent),
                RecentBytesReceived = Interlocked.Read(ref _lastWindowBytesReceived),
                RecentQueueDrops = Interlocked.Read(ref _lastWindowQueueDrops),
                RecentPeakQueueDepth = Interlocked.Read(ref _lastWindowPeakQueueDepth),
                LiveMessagesSent = Interlocked.Read(ref _messagesSent),
                LiveMessagesReceived = Interlocked.Read(ref _messagesReceived),
                LivePeakQueueDepth = Interlocked.Read(ref _peakQueueDepth),
                RecentWindowSeconds = LogIntervalMs / 1000.0
            };
        }

        /// <summary>Record that a position message was queued for sending.</summary>
        public static void RecordSent(int bytes)
        {
            Interlocked.Add(ref _bytesSent, bytes);
            Interlocked.Increment(ref _messagesSent);
            Interlocked.Add(ref _totalBytesSent, bytes);
            Interlocked.Increment(ref _totalMessagesSent);
        }

        /// <summary>Record that a position message was received and processed.</summary>
        public static void RecordReceived(int bytes)
        {
            Interlocked.Add(ref _bytesReceived, bytes);
            Interlocked.Increment(ref _messagesReceived);
            Interlocked.Add(ref _totalBytesReceived, bytes);
            Interlocked.Increment(ref _totalMessagesReceived);
        }

        /// <summary>Record that a stale queued position update was dropped to maintain queue depth.</summary>
        public static void RecordDrop()
        {
            Interlocked.Increment(ref _queueDrops);
            Interlocked.Increment(ref _totalQueueDrops);
        }

        /// <summary>
        /// Record the current depth of a per-vessel position-update queue after enqueue.
        /// We track only the high-water mark across all vessels so an unexpected backlog
        /// becomes visible in the periodic log line, even if no drops occurred.
        /// </summary>
        public static void RecordQueueDepth(int depth)
        {
            long observed;
            do
            {
                observed = Interlocked.Read(ref _peakQueueDepth);
                if (depth <= observed) return;
            } while (Interlocked.CompareExchange(ref _peakQueueDepth, depth, observed) != observed);
        }

        /// <summary>
        /// Emit a log line if the log interval has elapsed since the last one.
        /// Resets window counters after logging so each window shows the rate for that period.
        /// </summary>
        public static void MaybeLog()
        {
            var now = LunaComputerTime.UtcNow;
            if ((now - _lastLog).TotalMilliseconds < LogIntervalMs) return;
            _lastLog = now;

            var sent = Interlocked.Exchange(ref _messagesSent, 0);
            var recv = Interlocked.Exchange(ref _messagesReceived, 0);
            var bSent = Interlocked.Exchange(ref _bytesSent, 0);
            var bRecv = Interlocked.Exchange(ref _bytesReceived, 0);
            var drops = Interlocked.Exchange(ref _queueDrops, 0);
            var peak = Interlocked.Exchange(ref _peakQueueDepth, 0);

            Interlocked.Exchange(ref _lastWindowMessagesSent, sent);
            Interlocked.Exchange(ref _lastWindowMessagesReceived, recv);
            Interlocked.Exchange(ref _lastWindowBytesSent, bSent);
            Interlocked.Exchange(ref _lastWindowBytesReceived, bRecv);
            Interlocked.Exchange(ref _lastWindowQueueDrops, drops);
            Interlocked.Exchange(ref _lastWindowPeakQueueDepth, peak);

            LunaLog.Log($"[VMP NetStats] pos-upd sent={sent} ({bSent / 1024}KB) " +
                        $"recv={recv} ({bRecv / 1024}KB) " +
                        $"queue-drops={drops} peak-depth={peak} " +
                        $"(last {LogIntervalMs / 1000:0}s)");
        }
    }
}
