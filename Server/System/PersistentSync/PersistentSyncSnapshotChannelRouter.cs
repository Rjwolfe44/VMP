using LmpCommon.Message.Data.PersistentSync;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.PersistentSync;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Server.System.PersistentSync
{
    /// <summary>
    /// Chooses the Lidgren channel that carries PersistentSync snapshots to each client, and remembers which snapshot
    /// revision of each domain was last handed to each client.
    /// <para>
    /// All snapshots normally ride ReliableOrdered channel <see cref="DefaultChannel"/>. On 2026-10-23 that channel
    /// stopped delivering to one client for 50+ minutes after a burst of large ScienceSubjects snapshots, while every
    /// other channel kept working and nothing was logged on either side. A reliable-ordered channel that stalls never
    /// recovers: everything queued behind the missing message is withheld forever. A client that detects the stall sends
    /// a request with <see cref="PersistentSyncRequestMsgData.ResetSnapshotChannel"/> set; the server then moves that
    /// client to the next spare channel before answering.
    /// </para>
    /// </summary>
    public static class PersistentSyncSnapshotChannelRouter
    {
        public const int DefaultChannel = 23;

        /// <summary>Server-to-client ReliableOrdered channels that no other server message uses.</summary>
        internal static readonly int[] RecoveryChannels = { 24, 25, 26, 27, 28, 29, 30, 31, 22, 11 };

        /// <summary>A client still reporting an older revision this long after we sent a newer one is not receiving snapshots.</summary>
        internal static readonly TimeSpan DriftWarningAge = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan DriftLogInterval = TimeSpan.FromMinutes(1);

        /// <summary>Spare channels are finite per connection; repeated reset requests closer together than this reuse the current one.</summary>
        internal static readonly TimeSpan MinTimeBetweenResets = TimeSpan.FromSeconds(10);

        private sealed class SentSnapshot
        {
            public long Revision;
            public DateTime SentUtc;
            public int Channel;
        }

        private sealed class ClientChannelState
        {
            public readonly object Lock = new object();
            public int RecoveryIndex = -1;
            public DateTime LastResetUtc = DateTime.MinValue;
            public DateTime LastDriftLogUtc = DateTime.MinValue;
            public readonly ConcurrentDictionary<PersistentSyncDomainId, SentSnapshot> Sent = new ConcurrentDictionary<PersistentSyncDomainId, SentSnapshot>();

            /// <summary>Snapshot encodings the client reported it can read; 0 (raw only) until its first request arrives.</summary>
            public byte SupportedEncodings;

            /// <summary>The raw payload last handed to the client for each domain, which is what it holds as its base. Guarded by <see cref="Lock"/>.</summary>
            public readonly Dictionary<PersistentSyncDomainId, SnapshotBase> DeltaBases = new Dictionary<PersistentSyncDomainId, SnapshotBase>();

            /// <summary>Courtesy replies already queued for the client, so a burst of intents does not queue one reply each.</summary>
            public readonly ConcurrentDictionary<PersistentSyncDomainId, SentSnapshot> RepliesQueued = new ConcurrentDictionary<PersistentSyncDomainId, SentSnapshot>();
        }

        /// <summary>A courtesy resync reply (see <see cref="PersistentSyncDomainApplyResult.ReplyOnlyBecauseRevisionDrift"/>) is skipped when this revision went to the client this recently.</summary>
        internal static readonly TimeSpan DriftReplySuppressionWindow = TimeSpan.FromSeconds(10);

        /// <summary>Records which snapshot encodings <paramref name="client"/> can read. Called for every snapshot request.</summary>
        public static void SetSupportedEncodings(ClientStructure client, byte supportedEncodings)
        {
            if (client == null)
            {
                return;
            }

            GetState(client).SupportedEncodings = supportedEncodings;
        }

        public static byte GetSupportedEncodings(ClientStructure client)
        {
            return client == null ? (byte)0 : GetState(client).SupportedEncodings;
        }

        /// <summary>The raw snapshot the client was last sent for <paramref name="domainId"/>, or null when it must receive a full payload.</summary>
        internal static SnapshotBase GetDeltaBase(ClientStructure client, PersistentSyncDomainId domainId)
        {
            if (client == null)
            {
                return null;
            }

            var state = GetState(client);
            lock (state.Lock)
            {
                state.DeltaBases.TryGetValue(domainId, out var snapshotBase);
                return snapshotBase;
            }
        }

        /// <summary>
        /// Remembers that the client will hold this raw payload once it has read the message being sent. Payloads too small
        /// to be worth encoding clear the entry, the same rule the client applies when it keeps its copy.
        /// </summary>
        internal static void RememberSentPayload(ClientStructure client, PersistentSyncDomainId domainId, long revision, byte[] payload, int numBytes)
        {
            if (client == null)
            {
                return;
            }

            var snapshotBase = numBytes >= SnapshotCodec.MinCompressBytes ? SnapshotBase.Create(revision, payload, numBytes) : null;
            var state = GetState(client);
            lock (state.Lock)
            {
                if (snapshotBase == null)
                {
                    state.DeltaBases.Remove(domainId);
                }
                else
                {
                    state.DeltaBases[domainId] = snapshotBase;
                }
            }
        }

        /// <summary>Forgets every base: the next snapshot of each domain goes out in full.</summary>
        internal static void ClearDeltaBases(ClientStructure client)
        {
            if (client == null)
            {
                return;
            }

            var state = GetState(client);
            lock (state.Lock)
            {
                state.DeltaBases.Clear();
            }
        }

        internal static void ClearDeltaBases(ClientStructure client, IEnumerable<PersistentSyncDomainId> domainIds)
        {
            if (client == null || domainIds == null)
            {
                return;
            }

            var state = GetState(client);
            lock (state.Lock)
            {
                foreach (var domainId in domainIds)
                {
                    state.DeltaBases.Remove(domainId);
                }
            }
        }

        /// <summary>True when <paramref name="revision"/> of the domain was sent, or a reply carrying it queued, for the client within <see cref="DriftReplySuppressionWindow"/>.</summary>
        internal static bool WasRevisionSentRecently(ClientStructure client, PersistentSyncDomainId domainId, long revision, DateTime nowUtc)
        {
            if (client == null)
            {
                return false;
            }

            var state = GetState(client);
            return IsRecent(state.Sent, domainId, revision, nowUtc) || IsRecent(state.RepliesQueued, domainId, revision, nowUtc);
        }

        internal static void NoteReplyQueued(ClientStructure client, PersistentSyncDomainId domainId, long revision, DateTime nowUtc)
        {
            if (client == null)
            {
                return;
            }

            GetState(client).RepliesQueued[domainId] = new SentSnapshot { Revision = revision, SentUtc = nowUtc };
        }

        private static bool IsRecent(ConcurrentDictionary<PersistentSyncDomainId, SentSnapshot> table, PersistentSyncDomainId domainId, long revision, DateTime nowUtc)
        {
            return table.TryGetValue(domainId, out var entry) && entry.Revision == revision && nowUtc - entry.SentUtc < DriftReplySuppressionWindow;
        }

        // Keyed by object identity and released with the ClientStructure, so disconnects need no cleanup.
        private static readonly ConditionalWeakTable<ClientStructure, ClientChannelState> States = new ConditionalWeakTable<ClientStructure, ClientChannelState>();

        private static ClientChannelState GetState(ClientStructure client)
        {
            return States.GetValue(client, _ => new ClientChannelState());
        }

        public static int GetSnapshotChannel(ClientStructure client)
        {
            if (client == null)
            {
                return DefaultChannel;
            }

            var state = GetState(client);
            lock (state.Lock)
            {
                return ChannelForIndex(state.RecoveryIndex);
            }
        }

        /// <summary>Channel to use when sending <paramref name="message"/> to <paramref name="client"/>.</summary>
        public static int ResolveChannel(ClientStructure client, IServerMessageBase message)
        {
            if (message is PersistentSyncSrvMsg && client != null)
            {
                return GetSnapshotChannel(client);
            }

            return message.Channel;
        }

        /// <summary>
        /// Moves <paramref name="client"/>'s snapshot stream to the next spare channel and returns it. When no spare
        /// channel is left the client stays on the last one; only a reconnect gives it fresh channels. A second
        /// request within <see cref="MinTimeBetweenResets"/> keeps the channel it just got.
        /// </summary>
        public static int AdvanceSnapshotChannel(ClientStructure client, string reason)
        {
            return AdvanceSnapshotChannel(client, reason, DateTime.UtcNow);
        }

        internal static int AdvanceSnapshotChannel(ClientStructure client, string reason, DateTime nowUtc)
        {
            if (client == null)
            {
                return DefaultChannel;
            }

            var state = GetState(client);
            int oldChannel;
            int newChannel;
            bool exhausted;
            lock (state.Lock)
            {
                oldChannel = ChannelForIndex(state.RecoveryIndex);
                if (state.RecoveryIndex >= 0 && nowUtc - state.LastResetUtc < MinTimeBetweenResets)
                {
                    LunaLog.Debug($"[PersistentSync] snapshot channel reset client={client.PlayerName} reason={reason} ignored: channel {oldChannel} was assigned {(nowUtc - state.LastResetUtc).TotalSeconds:F1}s ago");
                    return oldChannel;
                }

                exhausted = state.RecoveryIndex >= RecoveryChannels.Length - 1;
                if (!exhausted)
                {
                    state.RecoveryIndex++;
                }

                // Whatever was queued on the stalled channel may never arrive, so nothing sent so far can serve as a base.
                state.DeltaBases.Clear();
                state.LastResetUtc = nowUtc;
                newChannel = ChannelForIndex(state.RecoveryIndex);
            }

            if (exhausted)
            {
                LunaLog.Error($"[PersistentSync] snapshot channel reset requested client={client.PlayerName} reason={reason} but no spare channel is left (staying on channel {newChannel}); the client must reconnect");
            }
            else
            {
                LunaLog.Warning($"[PersistentSync] snapshot channel reset client={client.PlayerName} channel {oldChannel} -> {newChannel} reason={reason}");
            }

            return newChannel;
        }

        /// <summary>Records that a snapshot was handed to Lidgren for <paramref name="client"/>.</summary>
        public static void NoteSnapshotSent(ClientStructure client, IServerMessageBase message, int channel, DateTime nowUtc)
        {
            if (client == null || !(message?.Data is PersistentSyncSnapshotMsgData data))
            {
                return;
            }

            var sent = new SentSnapshot { Revision = data.Revision, SentUtc = nowUtc, Channel = channel };
            GetState(client).Sent.AddOrUpdate(data.DomainId, sent, (_, previous) => data.Revision >= previous.Revision ? sent : previous);
        }

        /// <summary>
        /// Logs (at most once a minute per client) when an intent shows the client still has not applied a revision
        /// the server handed to it more than <see cref="DriftWarningAge"/> ago, which is what a stalled snapshot
        /// stream looks like from the server. Returns true when drift was detected.
        /// </summary>
        public static bool CheckIntentRevisionDrift(ClientStructure client, PersistentSyncDomainId domainId, long clientKnownRevision, DateTime nowUtc)
        {
            if (client == null)
            {
                return false;
            }

            var state = GetState(client);
            if (!state.Sent.TryGetValue(domainId, out var sent) || clientKnownRevision >= sent.Revision)
            {
                return false;
            }

            var age = nowUtc - sent.SentUtc;
            if (age < DriftWarningAge)
            {
                return false;
            }

            lock (state.Lock)
            {
                if (nowUtc - state.LastDriftLogUtc < DriftLogInterval)
                {
                    return true;
                }

                state.LastDriftLogUtc = nowUtc;
            }

            LunaLog.Warning(
                $"[PersistentSync] snapshot drift client={client.PlayerName} domain={domainId} clientKnownRevision={clientKnownRevision} " +
                $"but revision {sent.Revision} was sent to it {age.TotalSeconds:F0}s ago on channel {sent.Channel}: the client is not applying " +
                "PersistentSync snapshots (stalled snapshot channel, or a deferred apply)");
            return true;
        }

        private static int ChannelForIndex(int recoveryIndex)
        {
            return recoveryIndex < 0 ? DefaultChannel : RecoveryChannels[Math.Min(recoveryIndex, RecoveryChannels.Length - 1)];
        }
    }
}
