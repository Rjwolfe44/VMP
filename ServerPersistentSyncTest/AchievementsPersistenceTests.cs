using LmpCommon.PersistentSync;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using Server.System.PersistentSync;
using System;
using System.Linq;
using System.Text;

namespace ServerPersistentSyncTest
{
    /// <summary>
    /// The Achievements domain used to write each achievement's contents under <c>Progress</c> without its name and to
    /// append another unnamed wrapper node on every write. After a restart the server's achievements were empty, and
    /// the clients never got usable ones, so every milestone and record reward was collected again each session.
    /// Real clients send the node CONTENTS (KSP's <c>ConfigNode.WriteNode</c> output, no header) with the Id beside it.
    /// </summary>
    [TestClass]
    public class AchievementsPersistenceTests
    {
        private const string KerbinContents = "reached = 100\nOrbit\n{\n\tcompletedUnmanned = 200\n\tvessel\n\t{\n\t\tname = The Spark\n\t}\n}\nEscape\n{\n\tcompletedUnmanned = 300\n}\n";

        [TestInitialize]
        public void Setup()
        {
            PersistentSyncRegistry.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
        }

        [TestMethod]
        public void ContentsPayloadIsStoredUnderItsNameAndSurvivesARestart()
        {
            var store = NewStore(EmptyScenario());

            Assert.IsTrue(Send(store, "Kerbin", KerbinContents).Changed);
            Assert.IsTrue(Send(store, "RecordsSpeed", "reached = 5\nrecord = 69.5\n").Changed);

            // What the file looks like after the server wrote it, read back by a fresh process.
            var savedText = ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString();
            var restarted = new ConfigNode(savedText);
            ScenarioStoreSystem.CurrentScenarios["ProgressTracking"] = restarted;

            var progressNodes = restarted.GetNodes("Progress");
            Assert.AreEqual(1, progressNodes.Count, "one Progress node");
            Assert.AreEqual(0, restarted.GetAllNodes().Count(n => string.IsNullOrEmpty(n.Name)), "no unnamed wrapper nodes");
            var children = progressNodes.Single().Value.GetAllNodes().Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "Kerbin", "RecordsSpeed" }, children);

            var second = new AchievementsPersistentSyncDomainStore();
            second.LoadFromPersistence(false);
            var snapshot = AchievementSnapshotPayloadSerializer.Deserialize(second.GetCurrentSnapshot().Payload);
            Assert.AreEqual(2, snapshot.Length, "both achievements are back after the restart");

            var kerbin = snapshot.Single(s => s.Id == "Kerbin");
            var text = Encoding.UTF8.GetString(kerbin.Data, 0, kerbin.NumBytes);
            StringAssert.Contains(text, "reached = 100");
            StringAssert.Contains(text, "completedUnmanned = 200");
            StringAssert.Contains(text, "name = The Spark");
            StringAssert.Contains(text, "completedUnmanned = 300");
            Assert.IsFalse(text.StartsWith("Kerbin"), "snapshot data is contents only, the Id travels beside it");
        }

        [TestMethod]
        public void SnapshotDataParsesBackToTheSameContents()
        {
            var store = NewStore(EmptyScenario());
            Send(store, "Kerbin", KerbinContents);

            var kerbin = AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Single();
            var parsed = new ConfigNode(Encoding.UTF8.GetString(kerbin.Data, 0, kerbin.NumBytes));

            Assert.AreEqual("100", parsed.GetValue("reached").Value);
            Assert.AreEqual(2, parsed.GetAllNodes().Count);
            Assert.AreEqual("The Spark", parsed.GetNode("Orbit").Value.GetNode("vessel").Value.GetValue("name").Value);
        }

        [TestMethod]
        public void RepeatedWritesDoNotGrowTheSave()
        {
            var store = NewStore(EmptyScenario());

            for (var i = 0; i < 200; i++)
            {
                Send(store, "RecordsSpeed", $"reached = 5\nrecord = {i + 1}.5\n");
                Send(store, "Kerbin", KerbinContents);
            }

            var saved = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString());
            Assert.AreEqual(1, saved.GetAllNodes().Count, "only the Progress node at the top level");
            Assert.AreEqual(2, saved.GetNode("Progress").Value.GetAllNodes().Count);
            Assert.IsTrue(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString().Length < 2000, "the file stays small");
        }

        [TestMethod]
        public void LegacySaveWithUnnamedBlocksLoadsEmptyAndIsRewrittenClean()
        {
            var legacy = new StringBuilder("name = ProgressTracking\nscene = 7, 8, 5\nProgress\n{\n}\n");
            for (var i = 0; i < 300; i++)
            {
                legacy.Append("\n{\n\tProgress\n\t{\n\t\treached = 0\n\t\trecord = 69.9\n\t\tOrbit\n\t\t{\n\t\t\tcompleted = 5\n\t\t}\n\t}\n}\n");
            }

            var store = NewStore(new ConfigNode(legacy.ToString()));

            Assert.AreEqual(0, AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Length);
            var saved = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString());
            Assert.AreEqual(1, saved.GetAllNodes().Count, "unnamed wrappers are gone");
            Assert.AreEqual(1, saved.GetNodes("Progress").Count);
            Assert.AreEqual("ProgressTracking", saved.GetValue("name").Value, "other scenario values are kept");
            Assert.IsTrue(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString().Length < 500);

            Assert.IsTrue(Send(store, "Mun", "reached = 7\n").Changed);
            var afterWrite = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString());
            CollectionAssert.AreEqual(new[] { "Mun" }, afterWrite.GetNode("Progress").Value.GetAllNodes().Select(n => n.Name).ToArray());
        }

        [TestMethod]
        public void CleanSaveIsNotRewrittenOnLoad()
        {
            var text = "name = ProgressTracking\nscene = 7\nProgress\n{\n\tKerbin\n\t{\n\t\treached = 1\n\t}\n}\n";
            var scenario = new ConfigNode(text);
            var before = scenario.ToString();
            ScenarioStoreSystem.CurrentScenarios["ProgressTracking"] = scenario;

            var store = new AchievementsPersistentSyncDomainStore();
            store.LoadFromPersistence(false);

            Assert.AreSame(scenario, ScenarioStoreSystem.CurrentScenarios["ProgressTracking"]);
            Assert.AreEqual(before, scenario.ToString(), "a clean save is left alone");
            Assert.AreEqual(1, AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Length);
        }

        [TestMethod]
        public void HeaderWrappedPayloadIsStillAccepted()
        {
            var store = NewStore(EmptyScenario());

            Assert.IsTrue(SendRaw(store, "Duna", "Duna\n{\n\treached = 9\n}\n").Changed);

            var duna = AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Single();
            Assert.AreEqual("reached = 9\n", Encoding.UTF8.GetString(duna.Data, 0, duna.NumBytes));
        }

        [TestMethod]
        public void MergeNeverTakesProgressAway()
        {
            var store = NewStore(EmptyScenario());
            Send(store, "RecordsSpeed", "reached = 50\nrecord = 900\n");
            Send(store, "Kerbin", KerbinContents);

            // A stale client reports an older picture of both.
            var speedResult = Send(store, "RecordsSpeed", "reached = 10\nrecord = 70\n");
            var kerbinResult = Send(store, "Kerbin", "reached = 100\n");

            Assert.IsFalse(speedResult.Changed, "a lower record changes nothing");
            Assert.IsFalse(kerbinResult.Changed, "a report missing child nodes changes nothing");
            var speed = Read(store, "RecordsSpeed");
            StringAssert.Contains(speed, "record = 900");
            var kerbin = Read(store, "Kerbin");
            StringAssert.Contains(kerbin, "Orbit");
            StringAssert.Contains(kerbin, "Escape");
        }

        [TestMethod]
        public void MergeRaisesRecordsAndCompletionStates()
        {
            var store = NewStore(EmptyScenario());
            Send(store, "RecordsSpeed", "reached = 50\nrecord = 70\n");
            Assert.IsTrue(Send(store, "RecordsSpeed", "reached = 60\nrecord = 900\n").Changed);
            StringAssert.Contains(Read(store, "RecordsSpeed"), "record = 900");
            StringAssert.Contains(Read(store, "RecordsSpeed"), "reached = 50", "the earliest date is kept");

            Send(store, "Mun", "reached = 10\n");
            Assert.IsTrue(Send(store, "Mun", "completedUnmanned = 20\n").Changed);
            StringAssert.Contains(Read(store, "Mun"), "completedUnmanned = 20");
            Assert.IsFalse(Read(store, "Mun").Contains("reached"), "reached is replaced by the completion");

            Assert.IsTrue(Send(store, "Mun", "completedManned = 30\n").Changed);
            var mun = Read(store, "Mun");
            StringAssert.Contains(mun, "completed = 20");
            Assert.IsFalse(mun.Contains("completedManned"));
            Assert.IsFalse(mun.Contains("completedUnmanned"));

            Assert.IsFalse(Send(store, "Mun", "reached = 5\n").Changed, "reached never downgrades a completed node");
        }

        [TestMethod]
        public void MergeUnionsChildNodesFromDifferentPlayers()
        {
            var store = NewStore(EmptyScenario());
            Send(store, "Kerbin", "reached = 1\nOrbit\n{\n\tcompletedUnmanned = 5\n}\n");

            Assert.IsTrue(Send(store, "Kerbin", "reached = 1\nOrbit\n{\n\tcompletedManned = 6\n}\nLanding\n{\n\tcompleted = 9\n}\n").Changed);

            var parsed = new ConfigNode(Read(store, "Kerbin"));
            Assert.AreEqual("completed", parsed.GetNode("Orbit").Value.GetAllValues().Single().Key, "manned + unmanned makes completed");
            Assert.AreEqual("9", parsed.GetNode("Landing").Value.GetValue("completed").Value);
        }

        [TestMethod]
        public void IdenticalReportDoesNotBumpTheRevision()
        {
            var store = NewStore(EmptyScenario());
            Assert.IsTrue(Send(store, "Kerbin", KerbinContents).Changed);
            var revision = store.RevisionForTests;

            Assert.IsFalse(Send(store, "Kerbin", KerbinContents).Changed);
            Assert.AreEqual(revision, store.RevisionForTests);
        }

        [TestMethod]
        public void UnsafeIdsAreIgnored()
        {
            var store = NewStore(EmptyScenario());

            foreach (var id in new[] { "", " Kerbin", "Ker{bin", "Ker}bin", "A=B", "bad\nname", "../x", new string('a', 65) })
            {
                var result = SendRaw(store, id, "reached = 1\n");
                Assert.IsFalse(result.Changed, $"id '{id}'");
            }

            Assert.AreEqual(0, AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Length);
            var saved = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString());
            Assert.AreEqual(0, saved.GetNode("Progress").Value.GetAllNodes().Count);
        }

        [TestMethod]
        public void OversizedOrEmptyPayloadsAreIgnored()
        {
            var store = NewStore(EmptyScenario());

            Assert.IsFalse(SendRaw(store, "Kerbin", "").Changed);
            Assert.IsFalse(SendRaw(store, "Kerbin", "\n\n").Changed);
            Assert.IsFalse(SendRaw(store, "Kerbin", "reached = 1\n" + new string('x', AchievementNodeData.MaxNodeBytes)).Changed);
        }

        [TestMethod]
        public void NumberOfAchievementsIsCapped()
        {
            var store = NewStore(EmptyScenario());
            for (var i = 0; i < AchievementNodeData.MaxAchievements + 20; i++)
            {
                Send(store, "Body" + i, "reached = 1\n");
            }

            Assert.AreEqual(AchievementNodeData.MaxAchievements, AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Length);
        }

        [TestMethod]
        public void KspLoaderShapeIsWhatTheServerWrites()
        {
            // KSP's ProgressTree.Load(node): for each tree node, node.HasNode(id) then Load(node.GetNode(id)).
            var store = NewStore(EmptyScenario());
            Send(store, "FirstLaunch", "completed = 12\n");

            var progress = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ProgressTracking"].ToString()).GetNode("Progress").Value;
            Assert.AreEqual("12", progress.GetNode("FirstLaunch").Value.GetValue("completed").Value);
        }

        private static AchievementsPersistentSyncDomainStore NewStore(ConfigNode scenario)
        {
            ScenarioStoreSystem.CurrentScenarios["ProgressTracking"] = scenario;
            var store = new AchievementsPersistentSyncDomainStore();
            store.LoadFromPersistence(false);
            return store;
        }

        private static ConfigNode EmptyScenario() => new ConfigNode("name = ProgressTracking\nscene = 7, 8, 5\nProgress\n{\n}\n");

        private static PersistentSyncDomainApplyResult Send(AchievementsPersistentSyncDomainStore store, string id, string contents)
        {
            return SendRaw(store, id, contents);
        }

        private static PersistentSyncDomainApplyResult SendRaw(AchievementsPersistentSyncDomainStore store, string id, string contents)
        {
            var data = Encoding.UTF8.GetBytes(contents);
            var payload = AchievementSnapshotPayloadSerializer.Serialize(new[]
            {
                new AchievementSnapshotInfo { Id = id, NumBytes = data.Length, Data = data }
            });
            return store.ApplyServerMutation(payload, payload.Length, "AchievementUpdate:" + id);
        }

        private static string Read(AchievementsPersistentSyncDomainStore store, string id)
        {
            var info = AchievementSnapshotPayloadSerializer.Deserialize(store.GetCurrentSnapshot().Payload).Single(s => s.Id == id);
            return Encoding.UTF8.GetString(info.Data, 0, info.NumBytes);
        }
    }
}
