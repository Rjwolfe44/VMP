using System;

namespace LmpCommon.PersistentSync
{
    public enum PersistentSyncLivenessAction
    {
        None = 0,

        /// <summary>
        /// Send a small snapshot request. A healthy server always answers a request, so the reply proves the
        /// snapshot stream still reaches this client.
        /// </summary>
        SendProbe = 1,

        /// <summary>
        /// A request went unanswered. Ask the server to move this client's snapshot stream to a fresh channel and
        /// resend every domain (<c>PersistentSyncRequestMsgData.ResetSnapshotChannel</c>).
        /// </summary>
        SendRecovery = 2
    }

    /// <summary>
    /// Detects a PersistentSync snapshot stream that has silently stopped reaching this client.
    /// <para>
    /// Background (2026-10-23 train session): after a burst of ~30 Science/ScienceSubjects snapshots in 3 seconds the
    /// client never received another PersistentSync snapshot for 50+ minutes, while every other message type kept
    /// arriving and nothing was logged. All snapshots travel on one ReliableOrdered Lidgren channel; once that channel
    /// stops delivering, nothing sent on it arrives again, and the client had no way to notice.
    /// </para>
    /// <para>
    /// Rules: any received snapshot proves liveness. A request (initial sync, resync, probe, recovery) must be answered
    /// within <see cref="ReplyTimeout"/>. An intent usually, but not always, gets an echo, so an intent without any
    /// snapshot for <see cref="IntentReplyGrace"/> triggers a probe rather than a recovery. A quiet stream is probed
    /// every <see cref="QuietProbeInterval"/>. Recovery requests back off exponentially up to
    /// <see cref="MaxRecoveryBackoff"/>.
    /// </para>
    /// Pure logic with an injected clock so it can be unit tested without Unity. Thread-safe.
    /// </summary>
    public sealed class PersistentSyncLivenessWatchdog
    {
        private readonly object _lock = new object();

        private DateTime _lastSnapshotUtc;
        private DateTime? _awaitingReplySinceUtc;
        private string _awaitingReason;
        private DateTime? _oldestIntentWithoutReplyUtc;
        private DateTime? _lastRecoveryUtc;
        private DateTime? _stallSinceUtc;
        private int _recoveryAttempts;

        public TimeSpan QuietProbeInterval { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan IntentReplyGrace { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan InitialRecoveryBackoff { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan MaxRecoveryBackoff { get; set; } = TimeSpan.FromMinutes(5);

        public bool IsStallSuspected
        {
            get { lock (_lock) return _stallSinceUtc.HasValue; }
        }

        public DateTime? StallSinceUtc
        {
            get { lock (_lock) return _stallSinceUtc; }
        }

        public DateTime LastSnapshotUtc
        {
            get { lock (_lock) return _lastSnapshotUtc; }
        }

        public int RecoveryAttempts
        {
            get { lock (_lock) return _recoveryAttempts; }
        }

        /// <summary>What the outstanding request was (for logs), or null when nothing is outstanding.</summary>
        public string AwaitingReason
        {
            get { lock (_lock) return _awaitingReason; }
        }

        public DateTime? AwaitingReplySinceUtc
        {
            get { lock (_lock) return _awaitingReplySinceUtc; }
        }

        /// <summary>Starts a fresh session; the stream counts as alive at <paramref name="nowUtc"/>.</summary>
        public void Reset(DateTime nowUtc)
        {
            lock (_lock)
            {
                _lastSnapshotUtc = nowUtc;
                _awaitingReplySinceUtc = null;
                _awaitingReason = null;
                _oldestIntentWithoutReplyUtc = null;
                _lastRecoveryUtc = null;
                _stallSinceUtc = null;
                _recoveryAttempts = 0;
            }
        }

        /// <summary>
        /// Call for every snapshot that reaches the client, whatever the reconciler later does with it (applied,
        /// deferred, ignored as stale). Returns how long the stream had been stalled when this snapshot ends a
        /// suspected stall, otherwise null.
        /// </summary>
        public TimeSpan? OnSnapshotReceived(DateTime nowUtc)
        {
            lock (_lock)
            {
                _lastSnapshotUtc = nowUtc;
                _awaitingReplySinceUtc = null;
                _awaitingReason = null;
                _oldestIntentWithoutReplyUtc = null;

                if (!_stallSinceUtc.HasValue)
                {
                    return null;
                }

                var stalledFor = nowUtc - _stallSinceUtc.Value;
                _stallSinceUtc = null;
                _lastRecoveryUtc = null;
                _recoveryAttempts = 0;
                return stalledFor < TimeSpan.Zero ? TimeSpan.Zero : stalledFor;
            }
        }

        /// <summary>
        /// Call when a request that the server always answers was sent. The oldest unanswered request is what
        /// counts, so a stream of new requests cannot keep postponing the timeout.
        /// </summary>
        public void OnRequestSent(DateTime nowUtc, string reason)
        {
            lock (_lock)
            {
                if (_awaitingReplySinceUtc.HasValue)
                {
                    return;
                }

                _awaitingReplySinceUtc = nowUtc;
                _awaitingReason = reason;
            }
        }

        public void OnIntentSent(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (!_oldestIntentWithoutReplyUtc.HasValue)
                {
                    _oldestIntentWithoutReplyUtc = nowUtc;
                }
            }
        }

        /// <summary>
        /// Decides what to do now. When this returns <see cref="PersistentSyncLivenessAction.SendProbe"/> or
        /// <see cref="PersistentSyncLivenessAction.SendRecovery"/>, the watchdog already counts that request as
        /// outstanding; the caller only has to send it.
        /// </summary>
        public PersistentSyncLivenessAction Evaluate(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_awaitingReplySinceUtc.HasValue)
                {
                    if (nowUtc - _awaitingReplySinceUtc.Value < ReplyTimeout)
                    {
                        return PersistentSyncLivenessAction.None;
                    }

                    if (!_stallSinceUtc.HasValue)
                    {
                        _stallSinceUtc = _awaitingReplySinceUtc.Value;
                    }

                    if (_lastRecoveryUtc.HasValue && nowUtc - _lastRecoveryUtc.Value < CurrentRecoveryBackoffLocked())
                    {
                        return PersistentSyncLivenessAction.None;
                    }

                    _lastRecoveryUtc = nowUtc;
                    _recoveryAttempts++;
                    _awaitingReplySinceUtc = nowUtc;
                    _awaitingReason = "Recovery";
                    return PersistentSyncLivenessAction.SendRecovery;
                }

                if (_oldestIntentWithoutReplyUtc.HasValue && nowUtc - _oldestIntentWithoutReplyUtc.Value >= IntentReplyGrace)
                {
                    _oldestIntentWithoutReplyUtc = null;
                    _awaitingReplySinceUtc = nowUtc;
                    _awaitingReason = "ProbeAfterUnansweredIntent";
                    return PersistentSyncLivenessAction.SendProbe;
                }

                if (nowUtc - _lastSnapshotUtc >= QuietProbeInterval)
                {
                    _awaitingReplySinceUtc = nowUtc;
                    _awaitingReason = "ProbeQuietStream";
                    return PersistentSyncLivenessAction.SendProbe;
                }

                return PersistentSyncLivenessAction.None;
            }
        }

        private TimeSpan CurrentRecoveryBackoffLocked()
        {
            var doublings = Math.Min(Math.Max(0, _recoveryAttempts - 1), 20);
            var ticks = InitialRecoveryBackoff.Ticks * (double)(1L << doublings);
            return ticks >= MaxRecoveryBackoff.Ticks ? MaxRecoveryBackoff : TimeSpan.FromTicks((long)ticks);
        }
    }
}
