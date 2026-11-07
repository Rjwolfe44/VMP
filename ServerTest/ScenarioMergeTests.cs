using LmpCommon.KerbalKonstructs;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using Server.System.Scenario;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Two players share one Kerbal Konstructs save blob on the server. These tests replay the join/upload
    /// protocol for two simulated clients and check that neither client's changes are lost.
    /// </summary>
    [TestClass]
    public class ScenarioMergeTests
    {
        private const string Module = "KerbalKonstructsSettings";

        private sealed class Client
        {
            public readonly ConcurrentDictionary<string, ConfigNode> Baselines = new ConcurrentDictionary<string, ConfigNode>();

            /// <summary>What the client's game holds after loading the server copy at join.</summary>
            public string Local;

            public Client(string name) { Name = name; }
            public string Name { get; }

            public void Join()
            {
                Local = ScenarioStoreSystem.GetScenarioInConfigNodeFormat(Module);
                ScenarioClientMerge.RecordBaseline(Baselines, Module, Local);
            }

            public void Upload(string text)
            {
                ScenarioClientMerge.ApplyUpload(Baselines, Name, Module, text);
            }
        }

        private static string Blob(
            string lastSite = "LaunchPad",
            string savetime = "100",
            Dictionary<string, bool> sites = null,
            string hangar = "Closed",
            string[] buildings = null,
            bool includeSeaFacility = true,
            Dictionary<string, int> missions = null,
            string[] shared = null)
        {
            sites = sites ?? new Dictionary<string, bool> { ["LAUNCHPAD"] = true, ["NYE_ISLAND"] = false, ["KSC2"] = false };
            var sb = new StringBuilder();
            sb.Append("name = ").AppendLine(Module);
            sb.Append("savetime = ").AppendLine(savetime);
            sb.AppendLine("KKSettings\n{\n\tlastLaunchSiteUsed = " + lastSite + "\n\tmapShowOpen = True\n}");
            sb.AppendLine("Facilities\n{");
            sb.AppendLine("\tfac-tower\n\t{\n\t\tFacilityName = Tower\n\t\tHangar\n\t\t{\n\t\t\tOpenCloseState = " + hangar + "\n\t\t}\n\t}");
            if (includeSeaFacility)
                sb.AppendLine("\tfac-sea\n\t{\n\t\tFacilityName = Sea Base\n\t\tMerchant\n\t\t{\n\t\t\tisOpen = True\n\t\t}\n\t}");
            sb.AppendLine("}");
            sb.AppendLine("LaunchSites\n{");
            foreach (var site in sites)
            {
                var count = missions != null && missions.TryGetValue(site.Key, out var m) ? m : 0;
                sb.AppendLine("\t" + site.Key + "\n\t{\n\t\tMissionCount = " + count + "\n\t\tisOpen = " + (site.Value ? "True" : "False") + "\n\t}");
            }
            sb.AppendLine("}");
            sb.AppendLine("KKBuildings\n{");
            foreach (var building in buildings ?? new string[0])
            {
                //"uuid" or "uuid:model"
                var parts = building.Split(':');
                var model = parts.Length > 1 ? parts[1] : "crane";
                sb.AppendLine("\tInstance\n\t{\n\t\tUUID = " + parts[0] + "\n\t\tModel = " + model + "\n\t}");
            }
            sb.AppendLine("}");
            //null = an old client (or a new one with nothing shared): KK rewrites its node without the VMP container.
            if (shared != null)
            {
                sb.AppendLine(KkSharedStaticsFormat.ContainerName + "\n{");
                foreach (var entry in shared)
                    sb.Append(entry);
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        /// <summary>One shared static as a client writes it: a KK instance with a launch site and optionally a facility.</summary>
        private static string Shared(string key, string site, string facility = null, string group = "KSC", string extraValue = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\t" + KkSharedStaticsFormat.EntryName + "\n\t{");
            sb.AppendLine("\t\tVmpFormat = 1");
            if (key != null)
                sb.AppendLine("\t\t" + KkSharedStaticsFormat.KeyField + " = " + key);
            sb.AppendLine("\t\tModelName = launchpad_model");
            sb.AppendLine("\t\tGroup = " + group);
            if (extraValue != null)
                sb.AppendLine("\t\tLaunchSiteDescription = " + extraValue);
            sb.AppendLine("\t\tLaunchSite\n\t\t{\n\t\t\tLaunchSiteName = " + site + "\n\t\t\tLaunchPadTransform = spawn\n\t\t}");
            if (facility != null)
                sb.AppendLine("\t\tFacility\n\t\t{\n\t\t\tFacilityType = " + facility + "\n\t\t}");
            sb.AppendLine("\t}");
            return sb.ToString();
        }

        private static List<ConfigNode> SharedEntries()
        {
            var container = Stored().GetNode(KkSharedStaticsFormat.ContainerName)?.Value;
            return container == null
                ? new List<ConfigNode>()
                : container.GetNodes(KkSharedStaticsFormat.EntryName).Select(n => n.Value).ToList();
        }

        private static ConfigNode SharedEntry(string key) =>
            SharedEntries().FirstOrDefault(n => n.GetValue(KkSharedStaticsFormat.KeyField)?.Value == key);

        private static string SiteNameOf(ConfigNode entry) => entry?.GetNode("LaunchSite")?.Value.GetValue("LaunchSiteName")?.Value;

        private static Dictionary<string, bool> Sites(params string[] open)
        {
            var all = new[] { "LAUNCHPAD", "NYE_ISLAND", "KSC2" };
            return all.ToDictionary(s => s, s => open.Contains(s));
        }

        private static ConfigNode Stored()
        {
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
                return new ConfigNode(ScenarioStoreSystem.CurrentScenarios[Module].ToString());
        }

        private static string Value(string container, string entry, string key)
        {
            var node = Stored().GetNode(container)?.Value.GetNode(entry)?.Value;
            return node?.GetValue(key)?.Value;
        }

        private static ConfigNode Instance(string uuid) =>
            Stored().GetNode("KKBuildings").Value.GetNodes("Instance").Select(n => n.Value).FirstOrDefault(n => n.GetValue("UUID")?.Value == uuid);

        private static int InstanceCount() => Stored().GetNode("KKBuildings").Value.GetNodes("Instance").Count;

        private static bool Has(string container, string entry) =>
            Stored().GetNode(container)?.Value.GetNode(entry) != null;

        [TestInitialize]
        public void Seed()
        {
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
            {
                ScenarioStoreSystem.CurrentScenarios.Clear();
                ScenarioStoreSystem.CurrentScenarios[Module] = new ConfigNode(Blob());
            }
        }

        [TestCleanup]
        public void Clean()
        {
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
                ScenarioStoreSystem.CurrentScenarios.Clear();
        }

        [TestMethod]
        public void TwoPlayersEditingDifferentEntries_KeepBothChanges()
        {
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(sites: Sites("LAUNCHPAD", "NYE_ISLAND"), savetime: "200"));
            b.Upload(Blob(sites: Sites("LAUNCHPAD"), hangar: "Open", savetime: "150"));

            Assert.AreEqual("True", Value("LaunchSites", "NYE_ISLAND", "isOpen"), "A opened Nye Island and B's upload must not close it again.");
            Assert.AreEqual("Open", Stored().GetNode("Facilities").Value.GetNode("fac-tower").Value.GetNode("Hangar").Value.GetValue("OpenCloseState").Value, "B's hangar change must be kept.");
        }

        [TestMethod]
        public void StalePlayerUploadingUnrelatedChange_DoesNotRevertAnotherPlayersOpenedSite()
        {
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(sites: Sites("LAUNCHPAD", "KSC2"), savetime: "200"));

            //B never saw A's change; only a preference and the save time differ in B's upload.
            b.Upload(Blob(lastSite: "Runway", sites: Sites("LAUNCHPAD"), savetime: "230"));

            Assert.AreEqual("True", Value("LaunchSites", "KSC2", "isOpen"));
            Assert.AreEqual("Runway", Stored().GetNode("KKSettings").Value.GetValue("lastLaunchSiteUsed").Value);
        }

        [TestMethod]
        public void SameEntry_LastWriterWins_AndALaterRejoinSeesIt()
        {
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(sites: Sites("LAUNCHPAD", "NYE_ISLAND")));
            b.Upload(Blob(sites: Sites("LAUNCHPAD", "NYE_ISLAND", "KSC2")));
            //A closes Nye Island again (refund) after B's upload.
            a.Upload(Blob(sites: Sites("LAUNCHPAD")));

            Assert.AreEqual("False", Value("LaunchSites", "NYE_ISLAND", "isOpen"));
            Assert.AreEqual("True", Value("LaunchSites", "KSC2", "isOpen"));

            var c = new Client("C");
            c.Join();
            var handedToC = new ConfigNode(c.Local).GetNode("LaunchSites").Value.GetNode("KSC2").Value.GetValue("isOpen").Value;
            Assert.AreEqual("True", handedToC, "A joining player is handed the merged result.");
        }

        [TestMethod]
        public void ClientMissingAModDefinedEntry_NeverDeletesIt()
        {
            var friend = new Client("F");
            friend.Join();

            //The friend's install lacks the sea base facility and the KSC2 site, so KK does not save them.
            friend.Upload(Blob(sites: new Dictionary<string, bool> { ["LAUNCHPAD"] = true, ["NYE_ISLAND"] = false }, includeSeaFacility: false));

            Assert.IsTrue(Has("Facilities", "fac-sea"));
            Assert.IsTrue(Has("LaunchSites", "KSC2"));

            //An upload that carries neither container at all must not wipe them either.
            var text = Blob();
            var cut = text.IndexOf("Facilities", System.StringComparison.Ordinal);
            var end = text.IndexOf("KKBuildings", System.StringComparison.Ordinal);
            friend.Upload(text.Remove(cut, end - cut));
            Assert.IsTrue(Has("Facilities", "fac-sea"));
            Assert.IsTrue(Has("LaunchSites", "KSC2"));
        }

        private static void SetStored(string text)
        {
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
                ScenarioStoreSystem.CurrentScenarios[Module] = new ConfigNode(text);
        }

        [TestMethod]
        public void TwoPlayersEachPlacingABuilding_KeepBoth()
        {
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            //Every placed building is a node called Instance, so they cannot be told apart by name.
            a.Upload(Blob(buildings: new[] { "crane-1" }));
            b.Upload(Blob(buildings: new[] { "crane-2" }));

            Assert.AreEqual(2, InstanceCount());
            Assert.IsNotNull(Instance("crane-1"));
            Assert.IsNotNull(Instance("crane-2"));
        }

        [TestMethod]
        public void StalePlayerEditingOneBuilding_DoesNotRevertAnotherPlayersEdit()
        {
            SetStored(Blob(buildings: new[] { "crane-1", "crane-2" }));
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(buildings: new[] { "crane-1:crane-tall", "crane-2" }));
            b.Upload(Blob(buildings: new[] { "crane-1", "crane-2:crane-short" }));

            Assert.AreEqual(2, InstanceCount());
            Assert.AreEqual("crane-tall", Instance("crane-1").GetValue("Model").Value);
            Assert.AreEqual("crane-short", Instance("crane-2").GetValue("Model").Value);
        }

        [TestMethod]
        public void ClientMissingPlacedBuildings_NeverDeletesThem()
        {
            //A client that cannot load a building's model drops it from its own save; that must not delete it for everyone.
            SetStored(Blob(buildings: new[] { "crane-1", "crane-2" }));
            var b = new Client("B");
            b.Join();

            b.Upload(Blob(buildings: new string[0]));

            Assert.AreEqual(2, InstanceCount());
        }

        [TestMethod]
        public void UploadWithoutBaseline_KeepsServerValuesAndAddsOnlyMissingEntries()
        {
            //Fresh universe: the client joined before the server had any copy, so it received nothing to compare against.
            var late = new Client("L");
            var sites = Sites("LAUNCHPAD", "NYE_ISLAND");
            sites["BRAND_NEW"] = true;
            late.Upload(Blob(sites: sites));

            Assert.AreEqual("False", Value("LaunchSites", "NYE_ISLAND", "isOpen"), "The server's value is kept.");
            Assert.AreEqual("True", Value("LaunchSites", "BRAND_NEW", "isOpen"), "An entry the server lacked is added.");

            //From now on the client has a baseline and its own changes apply normally.
            late.Upload(Blob(sites: new Dictionary<string, bool> { ["LAUNCHPAD"] = true, ["NYE_ISLAND"] = false, ["KSC2"] = true, ["BRAND_NEW"] = true }));
            Assert.AreEqual("True", Value("LaunchSites", "KSC2", "isOpen"));
        }

        [TestMethod]
        public void MissionCounts_FromTwoPlayersLaunchingFromTheSameSite_Add()
        {
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(missions: new Dictionary<string, int> { ["LAUNCHPAD"] = 1 }));
            b.Upload(Blob(missions: new Dictionary<string, int> { ["LAUNCHPAD"] = 1 }));

            Assert.AreEqual("2", Value("LaunchSites", "LAUNCHPAD", "MissionCount"));
        }

        [TestMethod]
        public void SharedStatic_ReachesALateJoiner_AndAnOldClientsUploadDoesNotRemoveIt()
        {
            var a = new Client("A");
            var old = new Client("O");
            a.Join();
            old.Join();

            a.Upload(Blob(shared: new[] { Shared("uuid:pad-2", "KSC_PAD_2", facility: "Hangar") }));
            //An old client never writes the container (KK rebuilds its node on save); that is no delete.
            old.Upload(Blob(lastSite: "Runway"));

            var c = new Client("C");
            c.Join();
            var handed = new ConfigNode(c.Local).GetNode(KkSharedStaticsFormat.ContainerName)?.Value.GetNodes(KkSharedStaticsFormat.EntryName);
            Assert.AreEqual(1, handed?.Count, "The late joiner is handed the shared static.");
            Assert.AreEqual("KSC_PAD_2", SiteNameOf(handed[0].Value));
            Assert.AreEqual("Hangar", handed[0].Value.GetNode("Facility").Value.GetValue("FacilityType").Value, "Facility child kept exactly.");
            Assert.AreEqual("Runway", Stored().GetNode("KKSettings").Value.GetValue("lastLaunchSiteUsed").Value, "The old client's own change still applies.");
        }

        [TestMethod]
        public void SharedStatic_SameStaticEditedByTwo_LastWriterWinsWholeEntry_NoMixing()
        {
            SetStored(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_V1") }));
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_A", facility: "Hangar", extraValue: "from-a") }));
            b.Upload(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_B") }));

            Assert.AreEqual(1, SharedEntries().Count);
            var entry = SharedEntry("uuid:pad-2");
            Assert.AreEqual("PAD_B", SiteNameOf(entry), "The later save wins.");
            Assert.IsNull(entry.GetNode("Facility"), "Nothing of A's entry survives inside B's: it is replaced as a whole.");
            Assert.IsNull(entry.GetValue("LaunchSiteDescription"));
        }

        [TestMethod]
        public void SharedStatic_StaleEchoFromAnotherClient_DoesNotRevertTheEdit()
        {
            SetStored(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_V1"), Shared("uuid:tower", "TOWER_V1") }));
            var a = new Client("A");
            var b = new Client("B");
            a.Join();
            b.Join();

            a.Upload(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_V2"), Shared("uuid:tower", "TOWER_V1") }));
            //B sends back what it received at join (V1) plus its own edit of the tower.
            b.Upload(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_V1"), Shared("uuid:tower", "TOWER_B") }));

            Assert.AreEqual("PAD_V2", SiteNameOf(SharedEntry("uuid:pad-2")), "B's unchanged copy must not undo A's edit.");
            Assert.AreEqual("TOWER_B", SiteNameOf(SharedEntry("uuid:tower")));
        }

        [TestMethod]
        public void SharedStatic_ClientWithoutBaseline_OnlyAddsNewEntries()
        {
            SetStored(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_SERVER") }));
            var late = new Client("L");

            late.Upload(Blob(shared: new[] { Shared("uuid:pad-2", "PAD_STALE"), Shared("uuid:new", "NEW_SITE") }));

            Assert.AreEqual("PAD_SERVER", SiteNameOf(SharedEntry("uuid:pad-2")));
            Assert.AreEqual("NEW_SITE", SiteNameOf(SharedEntry("uuid:new")));
        }

        [TestMethod]
        public void SharedStatic_LimitsAndBadEntries_AreRefused_AndNoted()
        {
            var a = new Client("A");
            a.Join();
            var huge = new string('x', KkSharedStaticsFormat.MaxEntryBytes + 1);
            var entries = new[]
            {
                Shared("uuid:ok", "OK_SITE"),
                Shared(null, "NO_KEY"),
                Shared("not-a-key", "BAD_KEY"),
                Shared("uuid:ok", "DUPLICATE"),
                Shared("uuid:huge", "HUGE", extraValue: huge)
            };
            var notes = new List<string>();
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
            {
                ScenarioNodeMerge.Merge(a.Baselines[Module], new ConfigNode(Blob(shared: entries)), ScenarioStoreSystem.CurrentScenarios[Module], notes);
            }

            Assert.AreEqual(1, SharedEntries().Count);
            Assert.AreEqual("OK_SITE", SiteNameOf(SharedEntry("uuid:ok")), "The first copy of a repeated key is used.");
            Assert.AreEqual(4, notes.Count(n => n.StartsWith("refused")), string.Join("\n", notes));
            Assert.AreEqual(1, notes.Count(n => n == "added shared static uuid:ok"));
        }

        [TestMethod]
        public void SharedStatic_CountLimit_RefusesNewEntriesButStillUpdatesExistingOnes()
        {
            var full = Enumerable.Range(0, KkSharedStaticsFormat.MaxEntries).Select(i => Shared("uuid:s" + i, "SITE_" + i)).ToArray();
            SetStored(Blob(shared: full));
            var a = new Client("A");
            a.Join();

            var upload = Enumerable.Range(0, KkSharedStaticsFormat.MaxEntries).Select(i => Shared("uuid:s" + i, i == 0 ? "EDITED" : "SITE_" + i)).ToList();
            upload.Add(Shared("uuid:one-too-many", "EXTRA"));
            a.Upload(Blob(shared: upload.ToArray()));

            Assert.AreEqual(KkSharedStaticsFormat.MaxEntries, SharedEntries().Count);
            Assert.IsNull(SharedEntry("uuid:one-too-many"));
            Assert.AreEqual("EDITED", SiteNameOf(SharedEntry("uuid:s0")));
        }

        [TestMethod]
        public void SharedStatic_FirstCopyOfTheModule_IsHeldToTheLimits()
        {
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
                ScenarioStoreSystem.CurrentScenarios.Clear();
            var first = new Client("F");

            first.Upload(Blob(shared: new[] { Shared("uuid:ok", "OK_SITE"), Shared(null, "NO_KEY") }));

            Assert.AreEqual(1, SharedEntries().Count);
            Assert.IsNotNull(SharedEntry("uuid:ok"));
        }

        /// <summary>An entry with its VmpKey written twice: LunaConfigNode's GetValue throws "not unique" on such a node.</summary>
        private static string SharedWithTwoKeys(string site) =>
            Shared("uuid:twice", site).Replace("\t\tModelName", "\t\t" + KkSharedStaticsFormat.KeyField + " = uuid:other\n\t\tModelName");

        [TestMethod]
        public void SharedStatic_EntryWithTwoKeys_IsRefused_AndTheRestOfTheUploadMergesExactlyOnce()
        {
            var a = new Client("A");
            a.Join();
            var upload = Blob(missions: new Dictionary<string, int> { ["LAUNCHPAD"] = 1 },
                shared: new[] { SharedWithTwoKeys("HOSTILE"), Shared("uuid:ok", "OK_SITE") });

            a.Upload(upload);
            a.Upload(upload); //the same upload again (every 30 s): nothing may be counted twice

            Assert.AreEqual("1", Value("LaunchSites", "LAUNCHPAD", "MissionCount"), "The launch counted once, not once per upload.");
            Assert.IsNotNull(SharedEntry("uuid:ok"));
            Assert.AreEqual(1, SharedEntries().Count, "The entry with two keys is refused.");
        }

        [TestMethod]
        public void PlacedBuildingWithTwoUuids_DoesNotBreakTheMerge()
        {
            //Same library trap in the older KKBuildings identity: a repeated UUID must not throw.
            var a = new Client("A");
            a.Join();
            var text = Blob(buildings: new[] { "crane-1" }, missions: new Dictionary<string, int> { ["LAUNCHPAD"] = 1 })
                .Replace("UUID = crane-1", "UUID = crane-1\n\t\tUUID = crane-9");

            a.Upload(text);

            Assert.AreEqual("1", Value("LaunchSites", "LAUNCHPAD", "MissionCount"));
            Assert.AreEqual(1, InstanceCount());
        }

        [TestMethod]
        public void SharedStatic_FirstCopy_ExtraContainersAndStrayValuesAreDropped()
        {
            lock (ScenarioStoreSystem.ConfigTreeAccessLock)
                ScenarioStoreSystem.CurrentScenarios.Clear();
            var text = Blob(shared: new[] { Shared("uuid:ok", "OK_SITE") })
                       + KkSharedStaticsFormat.ContainerName + "\n{\n" + Shared("uuid:sneaky", "UNCHECKED") + "}\n";
            text = text.Replace(KkSharedStaticsFormat.ContainerName + "\n{\n", KkSharedStaticsFormat.ContainerName + "\n{\n\tstray = value\n\tjunk\n\t{\n\t}\n");

            new Client("F").Upload(text);

            var containers = Stored().GetNodes(KkSharedStaticsFormat.ContainerName);
            Assert.AreEqual(1, containers.Count, "Only the first container is stored.");
            Assert.AreEqual(0, containers[0].Value.GetAllValues().Count, "No plain values inside the container.");
            Assert.AreEqual(1, containers[0].Value.GetAllNodes().Count, "Only SharedStatic entries inside the container.");
            Assert.IsNotNull(SharedEntry("uuid:ok"));
        }

        [TestMethod]
        public void SharedStatic_TotalSizeIsCapped()
        {
            var a = new Client("A");
            a.Join();
            var big = new string('x', KkSharedStaticsFormat.MaxEntryBytes - 1000);
            var entries = Enumerable.Range(0, 60).Select(i => Shared("uuid:big" + i, "SITE_" + i, extraValue: big)).ToArray();

            a.Upload(Blob(shared: entries));

            var stored = SharedEntries();
            var bytes = stored.Sum(e => Encoding.UTF8.GetByteCount(e.ToString()));
            Assert.IsTrue(stored.Count > 0 && stored.Count < 60, $"stored {stored.Count}");
            Assert.IsTrue(bytes <= KkSharedStaticsFormat.MaxContainerBytes, $"{bytes} bytes stored");
        }

        /// <summary>An entry whose size by the client's measure (KkSharedStaticsFormat.MeasureContent of KSP's text) is exactly <paramref name="target"/>.</summary>
        private static string EntryOfClientSize(string key, int target)
        {
            var lines = new List<string> { "VmpFormat = 1", KkSharedStaticsFormat.KeyField + " = " + key, "ModelName = m", "UUID = " + KkSharedStaticsFormat.UuidOf(key), "LaunchSiteDescription = " };
            var clientText = string.Join("\n", lines) + "\n";
            //The measure trims lines, so the space after "=" only counts once the value is not empty.
            var pad = target - KkSharedStaticsFormat.MeasureContent(clientText) - 1;
            lines[lines.Count - 1] += new string('x', pad);
            Assert.AreEqual(target, KkSharedStaticsFormat.MeasureContent(string.Join("\n", lines) + "\n"));
            return "\t" + KkSharedStaticsFormat.EntryName + "\n\t{\n" + string.Concat(lines.Select(l => "\t\t" + l + "\n")) + "\t}\n";
        }

        [TestMethod]
        public void SharedStatic_SizeLimit_ServerCountsLikeTheClient_AtTheBoundary()
        {
            var a = new Client("A");
            a.Join();

            a.Upload(Blob(shared: new[] { EntryOfClientSize("uuid:edge", KkSharedStaticsFormat.MaxEntryBytes), EntryOfClientSize("uuid:over", KkSharedStaticsFormat.MaxEntryBytes + 1) }));

            Assert.IsNotNull(SharedEntry("uuid:edge"), "An entry the client accepts at exactly the limit must not be refused by the server.");
            Assert.IsNull(SharedEntry("uuid:over"));
        }

        [TestMethod]
        public void RefusalLog_IsCappedPerConnection_NotForTheWholeServer()
        {
            var a = new Client("A");
            a.Join();
            //300 distinct refusals in one upload (each key twice: the second copy is refused with its key in the note).
            var entries = Enumerable.Range(0, 300).SelectMany(i => new[] { Shared("uuid:d" + i, "S"), Shared("uuid:d" + i, "S") }).ToArray();
            var loggedA = new ConcurrentDictionary<string, byte>();
            ScenarioClientMerge.ApplyUpload(a.Baselines, "A", Module, Blob(shared: entries), loggedA);

            Assert.IsTrue(loggedA.Count <= 101, $"{loggedA.Count} logged for one connection");

            var b = new Client("B");
            b.Join();
            var loggedB = new ConcurrentDictionary<string, byte>();
            ScenarioClientMerge.ApplyUpload(b.Baselines, "B", Module, Blob(shared: new[] { Shared(null, "NO_KEY") }), loggedB);
            Assert.AreEqual(1, loggedB.Count, "Another player's refusal is still logged after one player hit its cap.");
        }

        [TestMethod]
        public void OnlyKerbalKonstructsIsMerged()
        {
            Assert.IsTrue(ScenarioNodeMerge.IsMergedModule(Module));
            Assert.IsFalse(ScenarioNodeMerge.IsMergedModule("ResourceScenario"));
            Assert.IsFalse(ScenarioNodeMerge.IsMergedModule(null));
        }
    }
}
