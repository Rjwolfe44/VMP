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
            Dictionary<string, int> missions = null)
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
            return sb.ToString();
        }

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
        public void OnlyKerbalKonstructsIsMerged()
        {
            Assert.IsTrue(ScenarioNodeMerge.IsMergedModule(Module));
            Assert.IsFalse(ScenarioNodeMerge.IsMergedModule("ResourceScenario"));
            Assert.IsFalse(ScenarioNodeMerge.IsMergedModule(null));
        }
    }
}
