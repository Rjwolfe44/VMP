using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LmpClient.Extensions;
using LmpCommon.Enums;
using LmpCommon.KerbalKonstructs;

namespace LmpClient.Systems.KerbalKonstructsShare
{
    /// <summary>
    /// Shares the Kerbal Konstructs statics a player edits in game (launch sites, facilities, placed statics) with
    /// everybody on the server, inside the existing <c>KerbalKonstructsSettings</c> scenario. KK itself writes such edits
    /// only to the player's own cfg files in GameData. See docs/kk-live-sharing.md for the design and the data format.
    /// <list type="bullet">
    /// <item>Capture: a static selected in a KK editor gets a snapshot of what KK would write for it. When KK later
    /// saves that static (StaticInstance.SaveConfig from an editor, or the statics editor's "Save all"), the instance
    /// node KK wrote is read back and shared if it differs from the snapshot. SaveConfig calls made by "Save all" itself
    /// (every cfg file) and by KK's own format conversion are ignored.</item>
    /// <item>Upload: after KK's scenario OnSave, a <c>VmpSharedStatics</c> child is appended (own entries plus what was
    /// received, unchanged). The normal 30 s scenario sync sends it; the server merges it per entry.</item>
    /// <item>Apply: before KK's scenario OnLoad body runs (once per main menu to game), every received entry is applied
    /// with KK's own loading code, so KK then loads the saved open/closed and facility state onto the shared statics.
    /// Facility states KK cannot load onto the local static are removed from the node first, because KK would throw.</item>
    /// </list>
    /// Every entry point catches everything: a problem with one entry is logged once and never reaches KK or the game.
    /// </summary>
    internal static class KkSharedStaticsSync
    {
        private const string Tag = "[KKShare]";

        private sealed class TouchInfo
        {
            /// <summary>What KK would write for the static when it was selected, before any change.</summary>
            public string Snapshot;
            /// <summary>Locator identity before any change (used when the UUID differs per client).</summary>
            public string Locator;
        }

        private static readonly object Gate = new object();
        private static KkReflection _kk;
        private static readonly KkSharedStaticSet Set = new KkSharedStaticSet();
        private static readonly Dictionary<string, ConfigNode> OwnNodes = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ConfigNode> ReceivedNodes = new Dictionary<string, ConfigNode>(StringComparer.Ordinal);
        /// <summary>
        /// Text of the entry last applied per identity in this game process. Kept across connections like AppliedKey: it
        /// describes KK's objects, so a rejoin respawns only statics whose entry changed.
        /// </summary>
        private static readonly Dictionary<string, string> AppliedText = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>
        /// Statics this client spawned from a shared entry, with that entry's identity. Kept across connections: it
        /// describes KK's objects in this game process, which stay as applied until the game restarts.
        /// </summary>
        private static readonly Dictionary<object, string> AppliedKey = new Dictionary<object, string>();
        private static readonly Dictionary<object, TouchInfo> Touched = new Dictionary<object, TouchInfo>();
        private static readonly OncePerKeyGate Problems = new OncePerKeyGate();
        private static int _lastLoggedUploadCount = -1;
        private static bool _inSaveAll;
        private static bool _flushPending;
        private static DateTime _lastFlushUtc = DateTime.MinValue;
        /// <summary>Shortest time between two immediate uploads of a captured edit.</summary>
        private static readonly TimeSpan FlushSpacing = TimeSpan.FromSeconds(5);

        public static bool Enabled => _kk != null;

        public static void Initialize(KkReflection kk) => _kk = kk;

        private static bool Connected => MainSystem.NetworkState >= ClientState.Connected;

        private static void Problem(string key, string message)
        {
            if (Problems.ShouldLog(key))
                LunaLog.LogWarning($"{Tag} {message}");
        }

        /// <summary>
        /// Disconnect (ScenarioSystem disabled): nothing of this server may leak into the next one. A captured edit is
        /// uploaded about a second after the save (TakeFlushRequest), so only an edit saved in the last second or so
        /// before a disconnect can be lost.
        /// </summary>
        public static void ResetSession()
        {
            lock (Gate)
            {
                Set.Clear();
                OwnNodes.Clear();
                ReceivedNodes.Clear();
                Touched.Clear();
                _flushPending = false;
                Problems.Reset();
                _lastLoggedUploadCount = -1;
                _inSaveAll = false;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Capture (the player who edits)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// KerbalKonstructs.SelectInstance postfix. Every KK editor works on the static selected here (FacilityEditor and
        /// LaunchSiteEditor read KerbalKonstructs.selectedInstance), and the statics editor's list selects with
        /// isEditing = false, so any selection counts.
        /// </summary>
        public static void OnSelected(object instance)
        {
            lock (Gate)
            {
                if (!Enabled || instance == null || !Connected || Touched.ContainsKey(instance))
                    return;
                try
                {
                    var snapshot = WriteLikeKk(instance);
                    Touched[instance] = new TouchInfo { Snapshot = snapshot == null ? null : TextOf(snapshot), Locator = LocatorOf(instance) };
                }
                catch (Exception e)
                {
                    Problem("snapshot:" + e.GetType().Name, $"Could not snapshot a Kerbal Konstructs static for sharing: {Inner(e).Message}");
                }
            }
        }

        /// <summary>StaticInstance.SaveConfig postfix: KK has just written this static's cfg file.</summary>
        public static void OnInstanceSaved(object instance)
        {
            lock (Gate)
            {
                //"Save all" calls SaveConfig for the first static of every cfg file; it is handled as a whole in OnAllSaved.
                if (!Enabled || instance == null || _inSaveAll || !Touched.ContainsKey(instance))
                    return;
                Capture(instance);
            }
        }

        /// <summary>KerbalKonstructs.SaveObjects prefix.</summary>
        public static void OnSaveAllStarting()
        {
            lock (Gate) _inSaveAll = true;
        }

        /// <summary>KerbalKonstructs.SaveObjects finalizer: runs whether or not KK threw.</summary>
        public static void OnSaveAllEnded()
        {
            lock (Gate) _inSaveAll = false;
        }

        /// <summary>KerbalKonstructs.SaveObjects postfix: capture only the statics selected in an editor this session.</summary>
        public static void OnAllSaved()
        {
            lock (Gate)
            {
                if (!Enabled)
                    return;
                foreach (var instance in Touched.Keys.ToList())
                {
                    //Statics deleted since: KK destroyed them; skip anything no longer in its database.
                    if (!IsLive(instance))
                    {
                        Touched.Remove(instance);
                        continue;
                    }
                    Capture(instance);
                }
            }
        }

        private static void Capture(object instance)
        {
            string key = null;
            try
            {
                if (!Connected || _kk.IsInSavegame(instance))
                    return; //Save-game statics are synced by KK's own KKBuildings node.

                var uuid = _kk.Uuid(instance);
                var model = _kk.ModelNameOf(instance);
                if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(model))
                    return;

                Touched.TryGetValue(instance, out var touch);
                var startup = StartupNode(instance, uuid);
                var uuidStable = _kk.ConfigUrl(instance) == null || startup != null;
                AppliedKey.TryGetValue(instance, out var appliedKey);
                key = KkSharedStaticsFormat.ResolveCaptureKey(appliedKey, KeyOfEntryWithUuid(uuid), uuid, uuidStable, touch?.Locator ?? LocatorOf(instance));
                if (key == null)
                    return;

                //KK wrote the file; a static with no file (spawned here from a shared entry) is written the same way in memory.
                var configPath = _kk.ConfigPath(instance);
                var written = string.IsNullOrEmpty(configPath) ? WriteLikeKk(instance) : ReadInstanceFromFile(configPath, uuid);
                if (written == null)
                {
                    Problem("noread:" + key, $"Cannot share static {model}: its instance was not found in GameData/{configPath} after Kerbal Konstructs saved it.");
                    return;
                }

                //Shared when it changed since it was selected, when it is new this session (placed or duplicated in KK),
                //or when this player already shared it (a revert must be shared too).
                var isNew = _kk.ConfigUrl(instance) == null && appliedKey == null;
                var changed = touch?.Snapshot == null || touch.Snapshot != TextOf(written);
                if (!changed && !isNew && !OwnNodes.ContainsKey(key))
                    return;

                var entry = BuildEntry(instance, key, model, written);
                var text = TextOf(entry);
                if (!Set.SetOwn(key, text, out var refusal))
                {
                    if (refusal != null)
                        Problem("refused:" + key, $"Cannot share static {model} ({key}): {refusal}.");
                    return;
                }

                OwnNodes[key] = entry;
                _flushPending = true;
                var site = _kk.LaunchSiteNameOf(instance);
                LunaLog.Log($"{Tag} Shared your edit of static {model} ({key}){(site != null ? ", launch site " + site : string.Empty)}; uploading it now.");
            }
            catch (Exception e)
            {
                Problem("capture:" + (key ?? e.GetType().Name), $"Could not capture a Kerbal Konstructs edit for sharing: {Inner(e).Message}");
            }
        }

        /// <summary>
        /// Polled every second from ScenarioSystem (in Update, outside KK's GUI code): true when a captured edit waits for
        /// an upload and the last immediate upload is at least 5 s ago. The caller then sends the scenario modules.
        /// </summary>
        public static bool TakeFlushRequest()
        {
            lock (Gate)
            {
                if (!_flushPending || !Connected || DateTime.UtcNow - _lastFlushUtc < FlushSpacing)
                    return false;
                _flushPending = false;
                _lastFlushUtc = DateTime.UtcNow;
                return true;
            }
        }

        /// <summary>Identity of an own or received entry for the same UUID (keeps a loc: identity stable across restarts).</summary>
        private static string KeyOfEntryWithUuid(string uuid)
        {
            foreach (var pair in OwnNodes.Concat(ReceivedNodes))
            {
                if (pair.Value.GetValue("UUID") == uuid)
                    return pair.Key;
            }
            return null;
        }

        /// <summary>
        /// What KK's cfg writer (ConfigParser.WriteInstanceConfig) produces for the static right now, without its side
        /// effect: the writer closes the launch site and its facilities (LaunchSiteParser.WriteConfig calls SetClosed,
        /// KKFacility.WriteConfig sets isOpen = false). Their two state fields are put back afterwards.
        /// </summary>
        private static ConfigNode WriteLikeKk(object instance)
        {
            var restore = new List<KeyValuePair<object, object[]>>();
            var site = _kk.SiLaunchSite.GetValue(instance);
            if (site != null)
                restore.Add(new KeyValuePair<object, object[]>(site, new[] { _kk.LsOpenState.GetValue(site), _kk.LsOpenString.GetValue(site) }));
            if (_kk.SiMyFacilities.GetValue(instance) is IList facilities)
            {
                foreach (var f in facilities)
                {
                    if (f != null)
                        restore.Add(new KeyValuePair<object, object[]>(f, new[] { _kk.FacOpenState.GetValue(f), _kk.FacOpenString.GetValue(f) }));
                }
            }

            var node = new ConfigNode("Instances");
            try
            {
                _kk.WriteInstanceConfig.Invoke(null, new[] { instance, node });
            }
            finally
            {
                foreach (var r in restore)
                {
                    var isSite = ReferenceEquals(r.Key, site);
                    (isSite ? _kk.LsOpenState : _kk.FacOpenState).SetValue(r.Key, r.Value[0]);
                    (isSite ? _kk.LsOpenString : _kk.FacOpenString).SetValue(r.Key, r.Value[1]);
                }
            }
            return node;
        }

        private static ConfigNode BuildEntry(object instance, string key, string model, ConfigNode written)
        {
            var entry = new ConfigNode(KkSharedStaticsFormat.EntryName);
            entry.AddValue(KkSharedStaticsFormat.FormatField, KkSharedStaticsFormat.FormatVersion.ToString());
            entry.AddValue(KkSharedStaticsFormat.KeyField, key);
            entry.AddValue(KkSharedStaticsFormat.ModelField, model);
            foreach (ConfigNode.Value v in written.values)
                entry.AddValue(v.name, v.value);
            foreach (ConfigNode child in written.nodes)
                entry.AddNode(child.CreateCopy());
            //No group description: receivers never create groups from shared data (docs/kk-live-sharing.md).
            return entry;
        }

        /// <summary>The instance node KK wrote: STATIC { pointername; Instances { UUID = ... } } (searched a few levels deep).</summary>
        private static ConfigNode ReadInstanceFromFile(string configPath, string uuid)
        {
            var file = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), configPath);
            if (!File.Exists(file))
                return null;
            return FindInstance(ConfigNode.Load(file), uuid, 0);
        }

        private static ConfigNode FindInstance(ConfigNode node, string uuid, int depth)
        {
            if (node == null || depth > 3)
                return null;
            foreach (ConfigNode child in node.nodes)
            {
                if (child.name == "Instances" && child.GetValue("UUID") == uuid)
                    return child;
                var deeper = FindInstance(child, uuid, depth + 1);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }

        /// <summary>The instance node this static was loaded from when the game started (GameDatabase), or null.</summary>
        private static ConfigNode StartupNode(object instance, string uuid)
        {
            var url = _kk.ConfigUrl(instance);
            if (url?.config == null || string.IsNullOrEmpty(uuid))
                return null;
            return url.config.GetNodes("Instances").FirstOrDefault(n => n.GetValue("UUID") == uuid);
        }

        private static string LocatorOf(object instance)
        {
            try
            {
                var p = _kk.RelativePosition(instance);
                return KkSharedStaticsFormat.LocatorKey(_kk.ModelNameOf(instance), _kk.Body(instance)?.name, _kk.Group(instance), p.x, p.y, p.z);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True while KK's database holds exactly this object under its UUID.</summary>
        private static bool IsLive(object instance)
        {
            var uuid = _kk.Uuid(instance);
            var live = _kk.InstancesByUuid();
            return uuid != null && live != null && live.Contains(uuid) && ReferenceEquals(live[uuid], instance);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Upload (scenario OnSave)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>KerbalKonstructsSettings.OnSave postfix.</summary>
        public static void OnScenarioSaved(ConfigNode node)
        {
            lock (Gate)
            {
                if (!Enabled || node == null || !Connected)
                    return;
                try
                {
                    var entries = Set.ForUpload();
                    if (entries.Count == 0)
                        return;

                    node.RemoveNodes(KkSharedStaticsFormat.ContainerName);
                    var container = node.AddNode(KkSharedStaticsFormat.ContainerName);
                    foreach (var e in entries)
                    {
                        if (OwnNodes.TryGetValue(e.Key, out var own) || ReceivedNodes.TryGetValue(e.Key, out own))
                            container.AddNode(own.CreateCopy());
                    }

                    if (entries.Count != _lastLoggedUploadCount)
                    {
                        _lastLoggedUploadCount = entries.Count;
                        LunaLog.Log($"{Tag} Scenario save carries {entries.Count} shared static(s), {Set.OwnCount} edited by you this session.");
                    }
                }
                catch (Exception e)
                {
                    Problem("save:" + e.GetType().Name, $"Could not add shared statics to the Kerbal Konstructs save: {Inner(e).Message}");
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Apply (scenario OnLoad prefix)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// KerbalKonstructsSettings.OnLoad prefix. KK only processes its scenario once per visit to the main menu
        /// (KerbalKonstructs.gameTime == -1). Applying here, before KK's body, means KK's own ResetFacilitiesOpenState and
        /// CareerState.Load then give the shared statics their saved state, and the facility states the shared statics
        /// make loadable no longer throw.
        /// </summary>
        public static void OnScenarioLoading(ConfigNode node)
        {
            lock (Gate)
            {
                try
                {
                    if (!Enabled || node == null || !Connected || !_kk.OnLoadGateOpen())
                        return;
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    ReadReceived(node);
                    ApplyAll();
                }
                catch (Exception e)
                {
                    Problem("load:" + e.GetType().Name, $"Could not apply shared statics: {Inner(e).Message}");
                }
            }
        }

        /// <summary>
        /// CareerState.Load prefix. KK calls it only from its gated OnLoad, after CareerObjects.LoadBuildings, so at this
        /// point every static exists: pack statics, shared statics (applied in the OnLoad prefix) and the save-game
        /// buildings KK just made. Only then can the facility count of each UUID be checked.
        /// </summary>
        public static void OnCareerStateLoading(ConfigNode node)
        {
            lock (Gate)
            {
                if (!Enabled || node == null || !Connected)
                    return;
                try
                {
                    GuardFacilityStates(node);
                }
                catch (Exception e)
                {
                    Problem("guard:" + e.GetType().Name, $"Could not check saved facility states: {Inner(e).Message}");
                }
            }
        }

        /// <summary>
        /// Removes saved facility states that KK's CareerState.LoadFacilitiesUUID would throw on (index beyond the local
        /// static's facilities, or no number). Without this one facility added by another player stops KK's whole load.
        /// The server keeps the states (its Facilities container is never shrunk by a client).
        /// </summary>
        private static void GuardFacilityStates(ConfigNode node)
        {
            if (!node.HasValue("useUUID"))
                return; //KK's legacy loader (by position) is not used for saves KK wrote itself.
            var facilities = node.GetNode("Facilities");
            var byUuid = _kk.InstancesByUuid();
            if (facilities == null || byUuid == null)
                return;

            foreach (var perStatic in facilities.GetNodes())
            {
                if (!byUuid.Contains(perStatic.name))
                    continue; //KK skips unknown UUIDs itself.
                var count = _kk.SiMyFacilities.GetValue(byUuid[perStatic.name]) is IList list ? list.Count : 0;
                var states = perStatic.GetNodes();
                foreach (var i in KkFacilityStateGuard.ToRemove(states.Select(n => n.GetValue("Index")).ToList(), count))
                {
                    var state = states[i];
                    perStatic.RemoveNode(state);
                    Problem("facility:" + perStatic.name + ":" + state.name,
                        $"Saved {state.name} state for static {perStatic.name} skipped: your copy of that static has {count} facilit(ies); Kerbal Konstructs would stop loading on it (its facility was changed or removed).");
                }
            }
        }

        private static void ReadReceived(ConfigNode node)
        {
            ReceivedNodes.Clear();
            var texts = new List<KeyValuePair<string, string>>();
            var container = node.GetNode(KkSharedStaticsFormat.ContainerName);
            if (container != null)
            {
                foreach (var entry in container.GetNodes(KkSharedStaticsFormat.EntryName))
                {
                    var keys = entry.GetValues(KkSharedStaticsFormat.KeyField);
                    var key = keys.Length == 1 ? keys[0] : null;
                    if (!KkSharedStaticsFormat.IsValidKey(key) || ReceivedNodes.ContainsKey(key))
                        continue;
                    ReceivedNodes[key] = entry.CreateCopy();
                    texts.Add(new KeyValuePair<string, string>(key, TextOf(entry)));
                }
            }
            Set.SetReceived(texts);
            foreach (var key in OwnNodes.Keys.Where(k => ReceivedNodes.ContainsKey(k)).ToList())
            {
                //The server has this player's own edit: the static here already is that edit, nothing to respawn.
                var received = TextOf(ReceivedNodes[key]);
                if (TextOf(OwnNodes[key]) == received)
                    AppliedText[key] = received;
                OwnNodes.Remove(key);
            }
            _lastLoggedUploadCount = -1;
            if (ReceivedNodes.Count > 0)
                LunaLog.Log($"{Tag} Received {ReceivedNodes.Count} shared static(s) from the server.");
        }

        private static void ApplyAll()
        {
            if (ReceivedNodes.Count == 0)
                return;
            int applied = 0, unchanged = 0, skipped = 0;
            foreach (var pair in ReceivedNodes.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
            {
                KkSharedApplyDecision decision;
                try
                {
                    decision = ApplyOne(pair.Key, pair.Value);
                }
                catch (Exception e)
                {
                    Problem("apply:" + pair.Key, $"Shared static {pair.Key} could not be applied: {Inner(e).Message}");
                    decision = KkSharedApplyDecision.RejectMalformed;
                }

                if (decision == KkSharedApplyDecision.Replace || decision == KkSharedApplyDecision.SpawnNew) applied++;
                else if (decision == KkSharedApplyDecision.SkipIdentical || decision == KkSharedApplyDecision.SkipAlreadyApplied) unchanged++;
                else skipped++;
            }
            LunaLog.Log($"{Tag} Shared statics: {applied} applied, {unchanged} already up to date, {skipped} skipped (of {ReceivedNodes.Count}).");
        }

        private static KkSharedApplyDecision ApplyOne(string key, ConfigNode entry)
        {
            var text = TextOf(entry);
            var modelName = entry.GetValue(KkSharedStaticsFormat.ModelField);
            var bodyName = entry.GetValue("CelestialBody");
            var groupName = entry.GetValue("Group");
            if (string.IsNullOrEmpty(groupName)) groupName = "Ungrouped";
            var entryUuid = entry.GetValues("UUID").Length == 1 ? entry.GetValue("UUID") : null;
            var model = _kk.FindModel(modelName);
            var body = FlightGlobals.Bodies?.FirstOrDefault(b => b != null && b.name == bodyName);
            var local = FindLocal(key, entryUuid, modelName);
            var groupKey = bodyName + "_" + groupName;
            var group = _kk.GetGroupCenter(groupKey);

            var facts = new KkSharedApplyFacts
            {
                FormatValue = entry.GetValue(KkSharedStaticsFormat.FormatField),
                Key = key,
                ModelName = modelName,
                BodyName = bodyName,
                EntryUuid = entryUuid,
                LocalModelName = local == null ? null : _kk.ModelNameOf(local),
                EntryBytes = KkSharedStaticsFormat.MeasureContent(text),
                AlreadyAppliedSameEntry = local != null && AppliedText.TryGetValue(key, out var done) && done == text,
                ModelAvailable = model != null,
                BodyAvailable = body != null,
                LocalFound = local != null,
                LocalIsSaveGame = local != null && _kk.IsInSavegame(local),
                LocalIsBeingEdited = local != null && ReferenceEquals(local, _kk.SelectedInstance()),
                //Only a static still as loaded from its file can be compared with that file; one spawned from an older
                //shared entry is compared through AppliedText above.
                LocalIdenticalToEntry = local != null && !AppliedKey.ContainsKey(local) && !Touched.ContainsKey(local) && IsIdenticalToStartup(local, entry),
                GroupExists = group != null,
                GroupIsSaveGame = group != null && _kk.GroupIsInSavegame(group)
            };

            var decision = KkSharedStaticPlanner.Decide(facts);
            if (KkSharedStaticPlanner.IsProblem(decision))
            {
                Problem("decide:" + key + ":" + decision, $"Shared static {key} ({modelName}) not applied: {Describe(decision)}.");
                return decision;
            }
            if (decision == KkSharedApplyDecision.SkipIdentical)
                AppliedText[key] = text;
            if (decision != KkSharedApplyDecision.Replace && decision != KkSharedApplyDecision.SpawnNew)
                return decision;

            var spawnNode = InstanceNodeOf(entry);
            string configPath = null;
            UrlDir.UrlConfig configUrl = null;
            ConfigNode restoreNode = null;
            object restoreModel = null;
            if (local != null)
            {
                //Keep the cfg file KK loaded it from: KK rewrites a cfg file with all its statics, and a static missing
                //from its own pack file would be dropped from that file the next time KK saves it.
                configPath = _kk.ConfigPath(local);
                configUrl = _kk.ConfigUrl(local);
                restoreNode = StartupNode(local, _kk.Uuid(local));
                restoreModel = _kk.Model(local);
                //KK's Destroy unregisters the launch site but leaves it in its group's list; drop it there too.
                var oldSite = _kk.SiLaunchSite.GetValue(local);
                var oldGroup = _kk.GroupCenterOf(local);
                if (oldSite != null && oldGroup != null && _kk.GcLaunchsites.GetValue(oldGroup) is IList groupSites)
                    groupSites.Remove(oldSite);
                _kk.SiDestroy.Invoke(local, null);
                Touched.Remove(local);
                AppliedKey.Remove(local);
            }
            //A static new to this install gets no cfg path: KK never writes it into this player's GameData
            //(its save of a static without a path only logs a warning).

            var spawned = Spawn(spawnNode, model, configPath, configUrl, out var problem);
            if (spawned == null)
            {
                Problem("spawn:" + key, $"Shared static {key} ({modelName}) could not be spawned: {problem}.");
                Restore(key, restoreNode, restoreModel, configPath, configUrl);
                return KkSharedApplyDecision.RejectMalformed;
            }

            AppliedText[key] = text;
            AppliedKey[spawned] = key;
            var site = _kk.LaunchSiteNameOf(spawned);
            LunaLog.Log($"{Tag} Applied shared static {key} ({modelName}{(site != null ? ", launch site " + site : string.Empty)}): " +
                        (decision == KkSharedApplyDecision.Replace ? "replaced the local copy." : "spawned, it was not in your install."));
            return decision;
        }

        /// <summary>
        /// The local static an entry is about. In order: one spawned from this entry earlier in this game process; the
        /// static with the entry's UUID (also for a loc: entry, whose sharer has that UUID in its file after a restart);
        /// for a loc: entry, the static at that locator.
        /// </summary>
        private static object FindLocal(string key, string entryUuid, string modelName)
        {
            foreach (var pair in AppliedKey.ToList())
            {
                if (pair.Value != key)
                    continue;
                if (IsLive(pair.Key))
                    return pair.Key;
                AppliedKey.Remove(pair.Key);
            }

            var byUuid = _kk.InstancesByUuid();
            if (!string.IsNullOrEmpty(entryUuid) && byUuid != null && byUuid.Contains(entryUuid))
                return byUuid[entryUuid];
            if (!KkSharedStaticsFormat.IsLocatorKey(key))
                return null;

            var all = _kk.AllInstances();
            if (all == null)
                return null;
            foreach (var si in all)
            {
                if (si != null && _kk.ModelNameOf(si) == modelName && LocatorOf(si) == key)
                    return si;
            }
            return null;
        }

        private static bool IsIdenticalToStartup(object local, ConfigNode entry)
        {
            var startup = StartupNode(local, _kk.Uuid(local));
            return startup != null && TextOf(startup) == TextOf(InstanceNodeOf(entry));
        }

        /// <summary>The entry as KK's own "Instances" node: VMP's fields and the group description removed.</summary>
        private static ConfigNode InstanceNodeOf(ConfigNode entry)
        {
            var node = entry.CreateCopy();
            node.name = "Instances";
            node.RemoveValues(KkSharedStaticsFormat.FormatField);
            node.RemoveValues(KkSharedStaticsFormat.KeyField);
            node.RemoveValues(KkSharedStaticsFormat.ModelField);
            node.RemoveNodes(KkSharedStaticsFormat.GroupCenterNode);
            return node;
        }

        /// <summary>
        /// Creates a static from an instance node exactly the way KK loads one from a cfg file
        /// (KerbalKonstructs.LoadInstances / CareerObjects.LoadBuilding), but never as a save-game static.
        /// Returns null (and removes the half-made static) when KK refuses it.
        /// </summary>
        private static object Spawn(ConfigNode node, object model, string configPath, UrlDir.UrlConfig configUrl, out string problem)
        {
            problem = null;
            object si = null;
            try
            {
                si = _kk.SiCtor.Invoke(null);
                _kk.SiModel.SetValue(si, model);
                _kk.SiConfigUrl.SetValue(si, configUrl);
                _kk.SiConfigPath.SetValue(si, configPath);
                _kk.SiIsInSavegame.SetValue(si, false);
                _kk.ParseInstanceConfig.Invoke(null, new[] { si, node });

                if (_kk.Body(si) == null || _kk.Group(si) == null || string.IsNullOrEmpty(_kk.Uuid(si)))
                {
                    problem = "no body, group or UUID after parsing";
                    return Discard(si);
                }

                var radial = (UnityEngine.Vector3)_kk.SiRadialPosition.GetValue(si);
                if (radial.Equals(UnityEngine.Vector3.zero) && _kk.RelativePosition(si).Equals(UnityEngine.Vector3.zero))
                {
                    var lat = (double)_kk.SiRefLatitude.GetValue(si);
                    var lon = (double)_kk.SiRefLongitude.GetValue(si);
                    if (lat == 361.0 || lon == 361.0)
                    {
                        problem = "no position (neither RelativePosition, RadialPosition nor RefLatitude/RefLongitude)";
                        return Discard(si);
                    }
                    var body = _kk.Body(si);
                    _kk.SiRadialPosition.SetValue(si, (UnityEngine.Vector3)(body.GetRelSurfaceNVector(lat, lon).normalized * body.Radius));
                }

                //Orientate may flag "convert legacy configs", which makes KK rewrite every cfg file at the next KSC visit.
                var legacy = _kk.KkConvertLegacy.GetValue(null);
                _kk.SiOrientate.Invoke(si, null);
                _kk.KkConvertLegacy.SetValue(null, legacy);

                if (!_kk.HasInstance(si))
                {
                    problem = "Kerbal Konstructs did not accept it (group missing?)";
                    return Discard(si);
                }

                _kk.AttachLaunchSite.Invoke(null, new[] { si, node });
                _kk.AttachFacilities.Invoke(null, new[] { si, node });
                _kk.SiGrassConfigs.SetValue(si, node.GetNodes("GrassColor2").ToList());
                if (_kk.SiMyFacilities.GetValue(si) is IList facilities)
                {
                    foreach (var f in facilities)
                        _kk.FacilityStaticInstance.SetValue(f, si);
                }

                var gc = _kk.GroupCenterOf(si);
                if (gc != null && _kk.GroupIsActive(gc))
                    _kk.SiActivate.Invoke(si, null);
                else
                    _kk.SiDeactivate.Invoke(si, null);
                return si;
            }
            catch (Exception e)
            {
                problem = Inner(e).Message;
                return si == null ? null : Discard(si);
            }
        }

        private static object Discard(object si)
        {
            try { _kk.SiDestroy.Invoke(si, null); }
            catch (Exception) { /* half-made static: KK may refuse to destroy it, nothing more to do */ }
            return null;
        }

        /// <summary>A replace that failed after the local static was removed: put the local one back from its cfg node.</summary>
        private static void Restore(string key, ConfigNode restoreNode, object restoreModel, string configPath, UrlDir.UrlConfig configUrl)
        {
            if (restoreNode == null || restoreModel == null)
                return;
            var back = Spawn(restoreNode, restoreModel, configPath, configUrl, out var problem);
            if (back == null)
                Problem("restore:" + key, $"Shared static {key}: the local copy could not be restored either ({problem}); it returns after a game restart.");
        }

        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Values and child nodes as KSP writes them (KSP's WriteNode leaves out the node's own name).</summary>
        private static string TextOf(ConfigNode node) => Encoding.UTF8.GetString(node.Serialize());

        private static Exception Inner(Exception e) => e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;

        private static string Describe(KkSharedApplyDecision d)
        {
            switch (d)
            {
                case KkSharedApplyDecision.SkipUnsupportedFormat: return "written by a newer VMP version";
                case KkSharedApplyDecision.RejectMalformed: return "the entry is incomplete";
                case KkSharedApplyDecision.RejectTooLarge: return "the entry is too large";
                case KkSharedApplyDecision.RejectKeyMismatch: return "its identity and its UUID disagree";
                case KkSharedApplyDecision.RejectModelMismatch: return "your static with this identity is a different model";
                case KkSharedApplyDecision.RejectMissingModel: return "you do not have this model (different mods)";
                case KkSharedApplyDecision.RejectMissingBody: return "you do not have this celestial body";
                case KkSharedApplyDecision.RejectSaveGameInstance: return "it is a save-game static (synced by Kerbal Konstructs itself)";
                case KkSharedApplyDecision.RejectSaveGameGroup: return "it belongs to a save-game group";
                case KkSharedApplyDecision.RejectMissingGroup: return "its group does not exist in your install";
                case KkSharedApplyDecision.DeferEditing: return "you are editing it right now (applied on the next join)";
                default: return d.ToString();
            }
        }
    }
}
