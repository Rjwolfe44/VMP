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

        private static DateTime _lastLog = DateTime.MinValue;

        /// <summary>Record that a position message was queued for sending.</summary>
        public static void RecordSent(int bytes)
        {
            Interlocked.Add(ref _bytesSent, bytes);
            Interlocked.Increment(ref _messagesSent);
        }

        /// <summary>Record that a position message was received and processed.</summary>
        public static void RecordReceived(int bytes)
        {
            Interlocked.Add(ref _bytesReceived, bytes);
            Interlocked.Increment(ref _messagesReceived);
        }

        /// <summary>Record that a stale queued position update was dropped to maintain queue depth.</summary>
        public static void RecordDrop() => Interlocked.Increment(ref _queueDrops);

        /// <summary>
        /// Emit a log line if the log interval has elapsed since the last one.
        /// Resets counters after logging so each window shows the rate for that period.
        /// Cheap to call every frame — does nothing until the interval fires.
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

            LunaLog.Log($"[VMP NetStats] pos-upd sent={sent} ({bSent / 1024}KB) " +
                        $"recv={recv} ({bRecv / 1024}KB) " +
                        $"queue-drops={drops} " +
                        $"(last {LogIntervalMs / 1000:0}s)");
        }
    }
}
