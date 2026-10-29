using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpCommon.PersistentSync
{
    /// <summary>
    /// Remembers facility upgrades this client made itself (sent as PersistentSync intents) until a server snapshot
    /// shows them, so re-applying a cached server snapshot can never lower a facility the player just upgraded.
    /// <para>
    /// Background (2026-10-23): the client upgraded the LaunchPad to level 2, but the server's reply never arrived.
    /// Every later KSC entry re-staged the last server snapshot it had (LaunchPad=1) and reverted the upgrade.
    /// </para>
    /// The server only ever raises facility levels from intents (lower levels are no-ops), so a local upgrade is
    /// either confirmed by a later snapshot (level at or above ours) or, if a newer server revision still shows a
    /// lower level well after we sent it, superseded.
    /// Pure logic; not thread-safe (Unity main thread only).
    /// </summary>
    public sealed class UpgradeableFacilityLocalUpgradeTracker
    {
        private sealed class PendingUpgrade
        {
            public string FacilityId;
            public int Level;
            public long KnownRevisionAtSend;
            public DateTime SentUtc;
        }

        private readonly Dictionary<string, PendingUpgrade> _pending = new Dictionary<string, PendingUpgrade>(StringComparer.Ordinal);

        /// <summary>
        /// How long a newer server revision must keep contradicting a local upgrade before the upgrade is dropped.
        /// Covers snapshots of other players' changes that the server sent before it processed our intent.
        /// </summary>
        public TimeSpan SupersedeGrace { get; set; } = TimeSpan.FromSeconds(60);

        public int PendingCount => _pending.Count;

        public void Clear()
        {
            _pending.Clear();
        }

        public void NoteLocalUpgrade(string facilityId, int level, long knownRevisionAtSend, DateTime nowUtc)
        {
            var key = NormalizeFacilityKey(facilityId);
            if (key.Length == 0)
            {
                return;
            }

            if (_pending.TryGetValue(key, out var existing) && existing.Level >= level)
            {
                return;
            }

            _pending[key] = new PendingUpgrade
            {
                FacilityId = facilityId,
                Level = level,
                KnownRevisionAtSend = knownRevisionAtSend,
                SentUtc = nowUtc
            };
        }

        /// <summary>
        /// Call with every server facility snapshot as it arrives. Drops local upgrades the server now shows, and
        /// ones that a newer server revision has contradicted for at least <see cref="SupersedeGrace"/>.
        /// Returns a description of each dropped upgrade (for logs).
        /// </summary>
        public List<string> OnServerSnapshot(IDictionary<string, int> serverLevels, long serverRevision, DateTime nowUtc)
        {
            var resolved = new List<string>();
            foreach (var key in _pending.Keys.ToList())
            {
                var pending = _pending[key];
                var hasServerLevel = TryGetLevel(serverLevels, key, out _, out var serverLevel);
                if (hasServerLevel && serverLevel >= pending.Level)
                {
                    _pending.Remove(key);
                    resolved.Add($"{pending.FacilityId}={pending.Level} confirmed(serverRev={serverRevision} serverLevel={serverLevel})");
                    continue;
                }

                if (serverRevision > pending.KnownRevisionAtSend && nowUtc - pending.SentUtc >= SupersedeGrace)
                {
                    _pending.Remove(key);
                    resolved.Add($"{pending.FacilityId}={pending.Level} superseded(serverRev={serverRevision} serverLevel={(hasServerLevel ? serverLevel.ToString() : "missing")})");
                }
            }

            return resolved;
        }

        /// <summary>
        /// Returns a copy of <paramref name="serverLevels"/> with every still-pending local upgrade raised on top.
        /// Never lowers a level. <paramref name="keptLocal"/> receives one entry per facility that was raised.
        /// </summary>
        public Dictionary<string, int> MergeOver(IDictionary<string, int> serverLevels, ICollection<string> keptLocal)
        {
            var merged = serverLevels == null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(serverLevels, StringComparer.Ordinal);

            foreach (var pair in _pending)
            {
                var pending = pair.Value;
                var found = TryGetLevel(merged, pair.Key, out var mergedKey, out var serverLevel);
                if (found && serverLevel >= pending.Level)
                {
                    continue;
                }

                var targetKey = found ? mergedKey : pending.FacilityId;
                merged[targetKey] = pending.Level;
                keptLocal?.Add($"{targetKey}={pending.Level}(server={(found ? serverLevel.ToString() : "missing")})");
            }

            return merged;
        }

        /// <summary>
        /// Facility ids differ by slash direction, casing and the <c>SpaceCenter/</c> prefix between scene objects,
        /// intents and server scenario keys; compare them on this normalized form.
        /// </summary>
        public static string NormalizeFacilityKey(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId))
            {
                return string.Empty;
            }

            var normalized = facilityId.Trim().Replace('\\', '/');
            const string prefix = "SpaceCenter/";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(prefix.Length);
            }

            return normalized.ToLowerInvariant();
        }

        private static bool TryGetLevel(IDictionary<string, int> levels, string normalizedKey, out string matchedKey, out int level)
        {
            matchedKey = null;
            level = 0;
            if (levels == null)
            {
                return false;
            }

            foreach (var pair in levels)
            {
                if (string.Equals(NormalizeFacilityKey(pair.Key), normalizedKey, StringComparison.Ordinal))
                {
                    matchedKey = pair.Key;
                    level = pair.Value;
                    return true;
                }
            }

            return false;
        }
    }
}
