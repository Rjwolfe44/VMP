using System;

namespace LmpCommon.LogUpload
{
    /// <summary>
    /// How an upload is cut into chunks. Pure arithmetic shared by the client (which sends them) and the tests; the server
    /// repeats the same checks in Begin and for every chunk and never trusts the client's numbers.
    /// </summary>
    public static class LogUploadChunkPlan
    {
        public static int ChunkCount(int totalBytes, int chunkBytes)
        {
            if (totalBytes <= 0 || chunkBytes <= 0) return 0;
            return (int)(((long)totalBytes + chunkBytes - 1) / chunkBytes);
        }

        /// <summary>Bytes in chunk <paramref name="index"/>: all full except the last, which holds the rest. 0 when out of range.</summary>
        public static int ChunkLength(int totalBytes, int chunkBytes, int index)
        {
            var count = ChunkCount(totalBytes, chunkBytes);
            if (index < 0 || index >= count) return 0;
            return (int)Math.Min(chunkBytes, totalBytes - (long)index * chunkBytes);
        }

        /// <summary>True when a server that accepts <see cref="LogUploadLimits.MinChunkBytes"/> to <see cref="LogUploadLimits.MaxChunkBytes"/> would take this plan.</summary>
        public static bool IsAcceptable(int totalBytes, int chunkBytes, int chunkCount)
        {
            return totalBytes >= LogUploadLimits.MinTotalBytes && totalBytes <= LogUploadLimits.MaxTotalBytes &&
                   chunkBytes >= LogUploadLimits.MinChunkBytes && chunkBytes <= LogUploadLimits.MaxChunkBytes &&
                   chunkCount == ChunkCount(totalBytes, chunkBytes) && chunkCount <= LogUploadLimits.MaxChunkCount;
        }
    }

    /// <summary>
    /// Decides, from the clock and the state of the connection's upload channel, whether the sending thread may hand the next
    /// chunk to the network now. Two rules: a rate cap (<see cref="LogUploadLimits.ClientMaxBytesPerSecond"/>, token bucket
    /// that never holds more than a few chunks, so a thread that wakes late after a freeze catches up a little but never
    /// floods the link) and a window rule (the previous chunk's fragments must be mostly acknowledged, so a slow link slows
    /// the upload instead of queueing it). It reads no game state, so it works the same however slowly the game draws frames.
    /// </summary>
    public sealed class LogUploadPacer
    {
        private readonly double _chunkBytes;
        private readonly double _bytesPerMs;
        private readonly double _maxTokens;
        private double _tokens;
        private long _lastMs;
        private bool _started;

        /// <param name="chunkBytes">Size of a full chunk.</param>
        /// <param name="bytesPerSecond">Rate cap.</param>
        /// <param name="burstChunks">How many chunks of credit may pile up while waiting (at least 1).</param>
        public LogUploadPacer(int chunkBytes, int bytesPerSecond, int burstChunks = 2)
        {
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
            _chunkBytes = chunkBytes;
            _bytesPerMs = bytesPerSecond / 1000.0;
            _maxTokens = chunkBytes * (double)Math.Max(1, burstChunks);
            _tokens = chunkBytes;
        }

        /// <summary>
        /// True when one chunk may be sent right now (the credit is then spent). <paramref name="slotsInUse"/> and
        /// <paramref name="windowSize"/> describe the reliable channel; a negative <paramref name="slotsInUse"/> (no connection) never sends.
        /// </summary>
        public bool TrySend(long nowMs, int slotsInUse, int windowSize)
        {
            if (!_started)
            {
                _started = true;
                _lastMs = nowMs;
            }

            if (nowMs > _lastMs)
            {
                _tokens = Math.Min(_maxTokens, _tokens + (nowMs - _lastMs) * _bytesPerMs);
                _lastMs = nowMs;
            }

            if (slotsInUse < 0 || slotsInUse > windowSize / 4) return false;
            if (_tokens < _chunkBytes) return false;

            _tokens -= _chunkBytes;
            return true;
        }
    }

    /// <summary>
    /// When the client tries again after an upload failed for a reason that is not the player's or the file's fault: once after
    /// 2 minutes and once more after 10. The retry is the same upload (same reason), started by the client itself, so the
    /// client counts it once against its own automatic-report limits; the server refunds the failed attempt (see
    /// LogUploadAssembler) and takes the retry as a fresh upload.
    /// </summary>
    public static class LogUploadRetry
    {
        /// <summary>Wait before retry 1 and retry 2.</summary>
        public static readonly TimeSpan[] Delays = { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) };

        public static int MaxRetries => Delays.Length;

        /// <summary>A scheduled retry that could not start (no connection, ...) for this long after it was due is dropped.</summary>
        public static readonly TimeSpan GiveUpAfterDue = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Refusals worth another try: the upload timed out, the server was busy with others, or it was asked again too soon.
        /// Everything else (too big, too many today, not a zip, not allowed, cancelled, server could not write) would fail the same way again.
        /// </summary>
        public static bool IsTransient(LogUploadRefusalReason reason)
        {
            return reason == LogUploadRefusalReason.TimedOut || reason == LogUploadRefusalReason.Busy || reason == LogUploadRefusalReason.TooFrequent;
        }

        /// <summary>
        /// A disconnect while the upload waits for the server's confirmation (every chunk sent) may have delivered it: no retry then,
        /// it could store a second copy. A disconnect while still sending is retried.
        /// </summary>
        public static bool CanRetryAfterConnectionLoss(bool waitingForConfirmation) => !waitingForConfirmation;

        /// <summary>
        /// Why a due retry must be forgotten instead of started, or null when it may go ahead: it is an automatic report (or one the host
        /// asked for) and the player turned those off since, or the game is now on a different server than the one the upload was for.
        /// </summary>
        public static string DropReason(bool automaticReport, bool automaticReportsEnabled, string endpointWhenFailed, string endpointNow)
        {
            if (automaticReport && !automaticReportsEnabled) return "sending reports automatically was turned off in the options";
            if (!string.Equals(endpointWhenFailed ?? string.Empty, endpointNow ?? string.Empty, StringComparison.Ordinal))
                return "the game is connected to a different server now";
            return null;
        }

        /// <summary>"2 minutes", "1 minute", "45 seconds": plain words for the on-screen status.</summary>
        public static string FormatDelay(TimeSpan delay)
        {
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            if (delay.TotalMinutes >= 1)
            {
                var minutes = (int)Math.Ceiling(delay.TotalMinutes);
                return minutes == 1 ? "1 minute" : minutes + " minutes";
            }

            var seconds = (int)Math.Ceiling(delay.TotalSeconds);
            return seconds == 1 ? "1 second" : seconds + " seconds";
        }
    }

    /// <summary>The retry bookkeeping of one failed upload; one instance lives in the client's upload system. Not thread-safe (use under the caller's lock).</summary>
    public sealed class LogUploadRetryPlan
    {
        public int RetriesStarted { get; private set; }

        /// <summary>When the next retry is due, or null when none is scheduled.</summary>
        public DateTime? DueUtc { get; private set; }

        public bool Pending => DueUtc.HasValue;

        /// <summary>
        /// Called when an attempt failed. Schedules the next retry when <paramref name="transient"/> and retries are left; returns the
        /// delay, or null when the upload is given up (any earlier schedule is cleared then).
        /// </summary>
        public TimeSpan? OnAttemptFailed(DateTime nowUtc, bool transient)
        {
            if (!transient || RetriesStarted >= LogUploadRetry.MaxRetries)
            {
                DueUtc = null;
                return null;
            }

            var delay = LogUploadRetry.Delays[RetriesStarted];
            DueUtc = nowUtc + delay;
            return delay;
        }

        public bool IsDue(DateTime nowUtc) => DueUtc.HasValue && nowUtc >= DueUtc.Value;

        /// <summary>Due long ago and still not started: forget it.</summary>
        public bool HasExpired(DateTime nowUtc) => DueUtc.HasValue && nowUtc - DueUtc.Value > LogUploadRetry.GiveUpAfterDue;

        public TimeSpan TimeLeft(DateTime nowUtc) => DueUtc.HasValue && DueUtc.Value > nowUtc ? DueUtc.Value - nowUtc : TimeSpan.Zero;

        /// <summary>The scheduled retry begins now: it no longer waits, and one retry is used up.</summary>
        public void MarkStarted()
        {
            if (!DueUtc.HasValue) return;
            DueUtc = null;
            RetriesStarted++;
        }

        /// <summary>A new upload (the player's click, or a new automatic report) replaces whatever was waiting.</summary>
        public void Reset()
        {
            DueUtc = null;
            RetriesStarted = 0;
        }
    }
}
