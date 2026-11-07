using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LmpCommon.KerbalKonstructs
{
    /// <summary>
    /// Data format of the Kerbal Konstructs statics that players edit in game and that VMP shares with everybody.
    /// The entries travel inside the existing <c>KerbalKonstructsSettings</c> scenario as one child node:
    /// <code>
    /// VmpSharedStatics
    /// {
    ///     SharedStatic
    ///     {
    ///         VmpFormat = 1
    ///         VmpKey = uuid:...         (identity, see BuildKey)
    ///         ModelName = ...           (KK model name; KK's cfg keeps it as STATIC/pointername)
    ///         UUID = ... (and every other value KK wrote for the instance)
    ///         LaunchSite { ... }        (exactly as KK writes it to its cfg)
    ///         Facility { ... }
    ///     }
    /// }
    /// </code>
    /// Kept free of KSP and Kerbal Konstructs types so the rules can be unit tested; see docs/kk-live-sharing.md.
    /// </summary>
    public static class KkSharedStaticsFormat
    {
        public const string ModuleName = "KerbalKonstructsSettings";
        public const string ContainerName = "VmpSharedStatics";
        public const string EntryName = "SharedStatic";
        public const string KeyField = "VmpKey";
        public const string FormatField = "VmpFormat";
        public const string ModelField = "ModelName";
        /// <summary>Written by the first build of this feature to describe a group; ignored since (groups are never created from shared data).</summary>
        public const string GroupCenterNode = "VmpGroupCenter";

        /// <summary>Entries with a higher format are skipped by this build (a newer client wrote them).</summary>
        public const int FormatVersion = 1;

        /// <summary>Largest serialized entry accepted by clients and the server. A launch pad with facilities is ~2-4 KB.</summary>
        public const int MaxEntryBytes = 16 * 1024;

        /// <summary>Most entries the shared container may hold (server) or a client may upload.</summary>
        public const int MaxEntries = 256;

        /// <summary>
        /// Largest total of all entries (serialized) a client uploads or the server stores. The KK module is uploaded
        /// whenever it changes (its savetime changes on every save), so this bounds the cost of every upload.
        /// </summary>
        public const int MaxContainerBytes = 512 * 1024;

        public const string UuidPrefix = "uuid:";
        public const string LocatorPrefix = "loc:";

        /// <summary>
        /// Identity of a shared static. Kerbal Konstructs gives every instance a UUID; pack statics carry it in their cfg
        /// file, so every client with the same pack has the same UUID. When an instance has no UUID in its file KK makes a
        /// random one at every game start, so it differs per client: then the identity is the model, body, group and the
        /// position relative to the group (rounded to 0.1 m), which every client loads from the same file.
        /// </summary>
        /// <param name="uuidIsStable">True when the UUID came from the static's cfg file, or the static was created in this session (nobody else has it yet).</param>
        public static string BuildKey(string uuid, bool uuidIsStable, string model, string body, string group, float x, float y, float z)
        {
            if (uuidIsStable && !string.IsNullOrEmpty(uuid))
                return UuidPrefix + uuid.Trim();
            return LocatorKey(model, body, group, x, y, z);
        }

        public static string LocatorKey(string model, string body, string group, float x, float y, float z)
        {
            return LocatorPrefix + string.Join("|", new[]
            {
                model ?? string.Empty, body ?? string.Empty, group ?? string.Empty,
                Round(x), Round(y), Round(z)
            });
        }

        /// <summary>
        /// Identity used when the local player saves a static. In order: the identity of the shared entry this static was
        /// spawned from; the identity of a shared entry (own or received) that carries the same UUID, which keeps a
        /// <c>loc:</c> identity after KK wrote the UUID into the player's file and the game restarted (else that player's
        /// next save would become a second, <c>uuid:</c>, entry and everybody else would get a duplicate); the new key.
        /// A <c>uuid:</c> identity is only reused while it names the UUID the static has now: KK gives a static a new
        /// UUID when its UUID is already taken, and an entry whose key and UUID disagree is refused by every receiver.
        /// </summary>
        public static string ResolveCaptureKey(string appliedKey, string existingKeyWithSameUuid, string uuid, bool uuidIsStable, string locatorKey)
        {
            if (IsReusable(appliedKey, uuid))
                return appliedKey;
            if (IsReusable(existingKeyWithSameUuid, uuid))
                return existingKeyWithSameUuid;
            if (uuidIsStable && !string.IsNullOrEmpty(uuid))
                return UuidPrefix + uuid.Trim();
            return IsValidKey(locatorKey) ? locatorKey : null;
        }

        private static bool IsReusable(string key, string currentUuid) =>
            IsValidKey(key) && (!IsUuidKey(key) || string.Equals(UuidOf(key), currentUuid?.Trim(), StringComparison.Ordinal));

        /// <summary>
        /// Size of an entry as both sides count it: UTF-8 bytes of every non-empty line, trimmed, plus one per line,
        /// without the entry's own name and braces. KSP (client) and LunaConfigNode (server) indent differently and
        /// only the server's text includes the entry's name; this measure is the same for both.
        /// </summary>
        public static int MeasureContent(string text)
        {
            return MeasureLines(Lines(text));
        }

        /// <summary><see cref="MeasureContent"/> for a node written with its name and braces (the server's ToString of a child node).</summary>
        public static int MeasureWrapped(string text, string nodeName)
        {
            var lines = Lines(text);
            if (lines.Count >= 3 && lines[0] == nodeName && lines[1] == "{" && lines[lines.Count - 1] == "}")
                lines = lines.GetRange(2, lines.Count - 3);
            return MeasureLines(lines);
        }

        private static List<string> Lines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
                return result;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length > 0)
                    result.Add(line);
            }
            return result;
        }

        private static int MeasureLines(List<string> lines)
        {
            var total = 0;
            foreach (var line in lines)
                total += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            return total;
        }

        public static bool IsUuidKey(string key) => key != null && key.StartsWith(UuidPrefix, StringComparison.Ordinal) && key.Length > UuidPrefix.Length;

        public static bool IsLocatorKey(string key) => key != null && key.StartsWith(LocatorPrefix, StringComparison.Ordinal) && key.Length > LocatorPrefix.Length;

        public static bool IsValidKey(string key) => IsUuidKey(key) || IsLocatorKey(key);

        public static string UuidOf(string key) => IsUuidKey(key) ? key.Substring(UuidPrefix.Length) : null;

        /// <summary>Format value of an entry: missing means 1 (the first format), unparsable means unsupported.</summary>
        public static bool IsSupportedFormat(string formatValue)
        {
            if (string.IsNullOrEmpty(formatValue))
                return true;
            return int.TryParse(formatValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= FormatVersion;
        }

        /// <summary>Parses KSP's "x,y,z" vector text. Returns false for anything else.</summary>
        public static bool TryParseVector(string text, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (string.IsNullOrEmpty(text))
                return false;
            var parts = text.Split(',');
            return parts.Length == 3 &&
                   float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                   float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        private static string Round(float v)
        {
            var r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            //"-0.0" and "0.0" must give the same key.
            if (r == 0) r = 0;
            return r.ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What a client does with one shared entry it received from the server.</summary>
    public enum KkSharedApplyDecision
    {
        /// <summary>Written by a newer VMP build in a format this build does not know.</summary>
        SkipUnsupportedFormat,
        /// <summary>Missing identity, model or body name.</summary>
        RejectMalformed,
        RejectTooLarge,
        /// <summary>This exact entry was already applied in this game session.</summary>
        SkipAlreadyApplied,
        /// <summary>The local static already looks exactly like the shared one (for example the player who shared it).</summary>
        SkipIdentical,
        /// <summary>This client does not have the model (different mod set). Nothing local is touched.</summary>
        RejectMissingModel,
        RejectMissingBody,
        /// <summary>A <c>uuid:</c> identity whose entry carries a different UUID: refused, it would rewrite another static.</summary>
        RejectKeyMismatch,
        /// <summary>The local static with this identity is a different model: refused, it would replace an unrelated static.</summary>
        RejectModelMismatch,
        /// <summary>The identity points at a save-game static; those are synced by KK's own KKBuildings node.</summary>
        RejectSaveGameInstance,
        /// <summary>The static sits in a save-game group, which KK deletes and rebuilds (with its children) on every load.</summary>
        RejectSaveGameGroup,
        /// <summary>The group does not exist here. Groups are never created from shared data (see docs/kk-live-sharing.md).</summary>
        RejectMissingGroup,
        /// <summary>The local player has this static open in a KK editor right now; try again on the next load.</summary>
        DeferEditing,
        /// <summary>Remove the local static and spawn it again from the shared entry.</summary>
        Replace,
        /// <summary>No local static with this identity: spawn it from the shared entry.</summary>
        SpawnNew
    }

    /// <summary>Everything the apply decision depends on, gathered by the client from KK.</summary>
    public struct KkSharedApplyFacts
    {
        public string FormatValue;
        public string Key;
        public string ModelName;
        public string BodyName;
        /// <summary>The UUID value inside the entry (KK always writes one).</summary>
        public string EntryUuid;
        /// <summary>Model of the local static the identity matched, when one was found.</summary>
        public string LocalModelName;
        public int EntryBytes;
        public bool AlreadyAppliedSameEntry;
        public bool ModelAvailable;
        public bool BodyAvailable;
        public bool LocalFound;
        public bool LocalIsSaveGame;
        public bool LocalIsBeingEdited;
        public bool LocalIdenticalToEntry;
        public bool GroupExists;
        public bool GroupIsSaveGame;
    }

    public static class KkSharedStaticPlanner
    {
        public static KkSharedApplyDecision Decide(KkSharedApplyFacts f)
        {
            if (!KkSharedStaticsFormat.IsSupportedFormat(f.FormatValue))
                return KkSharedApplyDecision.SkipUnsupportedFormat;
            if (!KkSharedStaticsFormat.IsValidKey(f.Key) || string.IsNullOrEmpty(f.ModelName) || string.IsNullOrEmpty(f.BodyName) || string.IsNullOrEmpty(f.EntryUuid))
                return KkSharedApplyDecision.RejectMalformed;
            if (f.EntryBytes > KkSharedStaticsFormat.MaxEntryBytes)
                return KkSharedApplyDecision.RejectTooLarge;
            if (KkSharedStaticsFormat.IsUuidKey(f.Key) && KkSharedStaticsFormat.UuidOf(f.Key) != f.EntryUuid.Trim())
                return KkSharedApplyDecision.RejectKeyMismatch;
            if (f.AlreadyAppliedSameEntry)
                return KkSharedApplyDecision.SkipAlreadyApplied;
            if (!f.ModelAvailable)
                return KkSharedApplyDecision.RejectMissingModel;
            if (!f.BodyAvailable)
                return KkSharedApplyDecision.RejectMissingBody;
            if (f.LocalFound && !string.Equals(f.LocalModelName, f.ModelName, StringComparison.Ordinal))
                return KkSharedApplyDecision.RejectModelMismatch;
            if (f.LocalFound && f.LocalIsSaveGame)
                return KkSharedApplyDecision.RejectSaveGameInstance;
            if (f.LocalFound && f.LocalIsBeingEdited)
                return KkSharedApplyDecision.DeferEditing;
            if (f.GroupExists && f.GroupIsSaveGame)
                return KkSharedApplyDecision.RejectSaveGameGroup;
            if (!f.GroupExists)
                return KkSharedApplyDecision.RejectMissingGroup;
            if (f.LocalFound && f.LocalIdenticalToEntry)
                return KkSharedApplyDecision.SkipIdentical;
            return f.LocalFound ? KkSharedApplyDecision.Replace : KkSharedApplyDecision.SpawnNew;
        }

        public static bool IsProblem(KkSharedApplyDecision d)
        {
            switch (d)
            {
                case KkSharedApplyDecision.Replace:
                case KkSharedApplyDecision.SpawnNew:
                case KkSharedApplyDecision.SkipAlreadyApplied:
                case KkSharedApplyDecision.SkipIdentical:
                    return false;
                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// The shared entries one client knows: what it received from the server at join (sent back unchanged, so an old
    /// server that overwrites the whole blob does not lose them) and what the local player saved in a KK editor this
    /// session (sent so everybody gets it). Entries are kept as serialized text keyed by identity.
    /// </summary>
    public sealed class KkSharedStaticSet
    {
        private readonly Dictionary<string, string> _received = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _own = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        public int OwnCount { get { lock (_lock) return _own.Count; } }
        public int ReceivedCount { get { lock (_lock) return _received.Count; } }

        /// <summary>
        /// Replaces the received entries with the server's copy (a new join). Own entries the server now has are dropped:
        /// from here on the server copy is the truth, so a stale own copy cannot undo a newer edit by someone else.
        /// Own entries the server lacks (the upload never got there) are kept and sent again.
        /// </summary>
        public void SetReceived(IEnumerable<KeyValuePair<string, string>> entries)
        {
            lock (_lock)
            {
                _received.Clear();
                foreach (var e in entries ?? Enumerable.Empty<KeyValuePair<string, string>>())
                {
                    if (!KkSharedStaticsFormat.IsValidKey(e.Key) || e.Value == null || _received.ContainsKey(e.Key))
                        continue;
                    _received[e.Key] = e.Value;
                }
                foreach (var key in _own.Keys.Where(_received.ContainsKey).ToList())
                    _own.Remove(key);
            }
        }

        /// <summary>Records the local player's latest save of a static. Returns false when it was refused or did not change.</summary>
        public bool SetOwn(string key, string text, out string refusal)
        {
            refusal = null;
            if (!KkSharedStaticsFormat.IsValidKey(key) || string.IsNullOrEmpty(text))
            {
                refusal = "invalid identity or empty entry";
                return false;
            }
            if (KkSharedStaticsFormat.MeasureContent(text) > KkSharedStaticsFormat.MaxEntryBytes)
            {
                refusal = $"entry is larger than {KkSharedStaticsFormat.MaxEntryBytes} bytes";
                return false;
            }
            lock (_lock)
            {
                if (!_own.ContainsKey(key) && _own.Count >= KkSharedStaticsFormat.MaxEntries)
                {
                    refusal = $"already sharing {KkSharedStaticsFormat.MaxEntries} statics this session";
                    return false;
                }
                if (_own.TryGetValue(key, out var old) && old == text)
                    return false;
                var ownBytes = _own.Where(e => e.Key != key).Sum(e => KkSharedStaticsFormat.MeasureContent(e.Value));
                if (ownBytes + KkSharedStaticsFormat.MeasureContent(text) > KkSharedStaticsFormat.MaxContainerBytes)
                {
                    refusal = $"your shared statics would exceed {KkSharedStaticsFormat.MaxContainerBytes / 1024} KB";
                    return false;
                }
                _own[key] = text;
                return true;
            }
        }

        public bool TryGetReceived(string key, out string text)
        {
            lock (_lock) return _received.TryGetValue(key, out text);
        }

        public List<KeyValuePair<string, string>> Received()
        {
            lock (_lock) return _received.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// What goes into the upload: own entries first (they win over a received copy with the same identity), then
        /// the received ones, at most <see cref="KkSharedStaticsFormat.MaxEntries"/>, in a stable order so the
        /// scenario hash only changes when an entry does.
        /// </summary>
        public List<KeyValuePair<string, string>> ForUpload()
        {
            lock (_lock)
            {
                var all = _own.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                all.AddRange(_received.Where(e => !_own.ContainsKey(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal));
                var result = new List<KeyValuePair<string, string>>();
                var bytes = 0;
                foreach (var e in all)
                {
                    var size = KkSharedStaticsFormat.MeasureContent(e.Value);
                    if (result.Count >= KkSharedStaticsFormat.MaxEntries || bytes + size > KkSharedStaticsFormat.MaxContainerBytes)
                        continue;
                    bytes += size;
                    result.Add(e);
                }
                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _received.Clear();
                _own.Clear();
            }
        }
    }

    /// <summary>
    /// Guards KK's own scenario load. KK's CareerState.LoadFacilitiesUUID reads <c>myFacilities[Index]</c> of the static
    /// with that UUID for every saved facility state, with no bounds check, and <c>int.Parse(Index)</c>. A state saved by a
    /// player whose copy of the static has a facility (for example one added in the Facility Editor) therefore throws on
    /// every client whose copy has fewer, and KK's OnLoad stops half-way (launch site states, ground stations not
    /// loaded). Such states are removed from the node before KK reads it; the server keeps them (additive container).
    /// </summary>
    public static class KkFacilityStateGuard
    {
        public static bool IsLoadable(string indexValue, int localFacilityCount)
        {
            return int.TryParse(indexValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                   index >= 0 && index < localFacilityCount;
        }

        /// <summary>
        /// Positions of the saved states (their Index values, in order) to remove for one UUID. The facility count must
        /// be read when KK's CareerState.Load starts, when every static exists: pack statics (game start), shared statics
        /// (applied before KK's OnLoad body) and save-game buildings (made by KK inside OnLoad, just before). A null count
        /// means KK has no static with that UUID; KK skips those itself, so nothing is removed.
        /// </summary>
        public static List<int> ToRemove(IList<string> indexValues, int? localFacilityCount)
        {
            var result = new List<int>();
            if (indexValues == null || localFacilityCount == null)
                return result;
            for (var i = 0; i < indexValues.Count; i++)
            {
                if (!IsLoadable(indexValues[i], localFacilityCount.Value))
                    result.Add(i);
            }
            return result;
        }
    }

    /// <summary>Lets each distinct problem through once per session so a broken entry cannot spam the log every load.</summary>
    public sealed class OncePerKeyGate
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly int _capacity;
        private readonly object _lock = new object();

        public OncePerKeyGate(int capacity = 512) { _capacity = Math.Max(1, capacity); }

        public bool ShouldLog(string problemKey)
        {
            lock (_lock)
            {
                if (_seen.Contains(problemKey ?? string.Empty) || _seen.Count >= _capacity)
                    return false;
                _seen.Add(problemKey ?? string.Empty);
                return true;
            }
        }

        public void Reset()
        {
            lock (_lock) _seen.Clear();
        }
    }
}
