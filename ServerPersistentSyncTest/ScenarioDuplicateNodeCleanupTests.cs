using LmpCommon.PersistentSync;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using Server.System.PersistentSync;
using Server.Utilities;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ServerPersistentSyncTest
{
    /// <summary>
    /// LunaConfigNode's ConfigNode.RemoveNode(ConfigNode) leaves the node in the serialized output, so every
    /// "remove all, add the new set" rewrite used to append a full copy to the save. On the train server that grew
    /// ResearchAndDevelopment to 80,008 Science nodes for 368 subjects, and the first science change after each
    /// restart froze the whole server for 1 to 4 minutes. These tests pin the replacement behaviour.
    /// (ProgressTracking/Achievements had its own, separate save-format problem; see AchievementsPersistenceTests.)
    /// </summary>
    [TestClass]
    public class ScenarioDuplicateNodeCleanupTests
    {
        [TestInitialize]
        public void Setup()
        {
            PersistentSyncRegistry.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
        }

        [TestMethod]
        public void LibraryRemoveNodeByInstanceStillSavesTheNode()
        {
            // Documents the library bug ConfigNodeRemoval works around.
            var parent = new ConfigNode("name = Test\nScience\n{\n id = a\n}\nScience\n{\n id = b\n}\n");
            var first = parent.GetNodes("Science").First().Value;

            parent.RemoveNode(first);

            Assert.AreEqual(1, parent.GetNodes("Science").Count, "the lookup view drops it");
            Assert.AreEqual(2, CountSavedNodes(parent, "Science"), "but the serializer still writes it");
        }

        [TestMethod]
        public void RemoveNodesWhereRemovesFromTheSavedOutput()
        {
            var parent = new ConfigNode("name = Test\nTech\n{\n id = a\n}\nTech\n{\n id = b\n}\nTech\n{\n id = c\n}\nOther\n{\n x = 1\n}\n");

            ConfigNodeRemoval.RemoveNodesWhere(parent, "Tech", node => node.GetValue("id").Value == "b");

            var saved = new ConfigNode(parent.ToString());
            CollectionAssert.AreEqual(new[] { "a", "c" }, saved.GetNodes("Tech").Select(n => n.Value.GetValue("id").Value).ToArray());
            Assert.AreEqual(1, saved.GetNodes("Other").Count);
        }

        [TestMethod]
        public void ScienceSubjectWritesReplaceInsteadOfAppending()
        {
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = CreateResearchAndDevelopment(1, "a", "b");
            var store = new ScienceSubjectsPersistentSyncDomainStore();
            store.LoadFromPersistence(false);

            for (var i = 0; i < 5; i++)
            {
                var payload = ScienceSubjectSnapshotPayloadSerializer.Serialize(new[] { CreateSubject("c" + i, i + 1) });
                Assert.IsTrue(store.ApplyServerMutation(payload, payload.Length, "recovery").Changed);
            }

            Assert.AreEqual(7, CountSavedNodes(ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"], "Science"));
        }

        [TestMethod]
        public void ScienceSubjectLoadCollapsesDuplicatesKeepsNewestCopyAndStaysFast()
        {
            // 50 subjects saved 200 times, like the bloated train-server save but smaller; copy N has science = N.
            var ids = Enumerable.Range(0, 50).Select(i => "subject" + i).ToArray();
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = CreateResearchAndDevelopment(200, ids);
            var store = new ScienceSubjectsPersistentSyncDomainStore();

            var watch = Stopwatch.StartNew();
            store.LoadFromPersistence(false);
            var payload = ScienceSubjectSnapshotPayloadSerializer.Serialize(new[] { CreateSubject("subject0", 999) });
            var result = store.ApplyServerMutation(payload, payload.Length, "recovery");
            watch.Stop();

            Assert.IsTrue(result.Changed);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"load + first write took {watch.Elapsed}");

            var saved = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"].ToString());
            var savedSubjects = saved.GetNodes("Science").Select(n => n.Value).ToList();
            Assert.AreEqual(50, savedSubjects.Count);
            Assert.AreEqual("999", savedSubjects.Single(n => n.GetValue("id").Value == "subject0").GetValue("science").Value);
            Assert.AreEqual("200", savedSubjects.Single(n => n.GetValue("id").Value == "subject7").GetValue("science").Value);
            Assert.AreEqual(1, saved.GetNodes("Tech").Count, "other R&D nodes are untouched");
        }

        [TestMethod]
        public void ExperimentalPartsLoadCollapsesDuplicateNodes()
        {
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = new ConfigNode(
                "name = ResearchAndDevelopment\nExpParts\n{\n liquidEngine = 1\n}\nExpParts\n{\n liquidEngine = 2\n solarPanel = 1\n}\n");
            var store = new ExperimentalPartsPersistentSyncDomainStore();

            store.LoadFromPersistence(false);

            var saved = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"].ToString());
            Assert.AreEqual(1, saved.GetNodes("ExpParts").Count);
            Assert.AreEqual("2", saved.GetNodes("ExpParts").Single().Value.GetValue("liquidEngine").Value);
            Assert.AreEqual("1", saved.GetNodes("ExpParts").Single().Value.GetValue("solarPanel").Value);
        }

        [TestMethod]
        public void TechnologyWriteDropsDuplicateTechNodesFromTheSave()
        {
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = new ConfigNode(
                "name = ResearchAndDevelopment\n" +
                "Tech\n{\n id = basicRocketry\n state = Available\n cost = 5\n part = liquidEngine\n}\n" +
                "Tech\n{\n id = basicRocketry\n state = Available\n cost = 5\n part = liquidEngine\n}\n" +
                "Tech\n{\n id = engineering101\n state = Available\n cost = 15\n}\n");
            var store = new TechnologyPersistentSyncDomainStore();
            store.LoadFromPersistence(false);

            var advRocketry = Encoding.UTF8.GetBytes("id = advRocketry\nstate = Available\ncost = 45\npart = advLiquidEngine\n");
            var payload = TechnologySnapshotPayloadSerializer.Serialize(new[]
            {
                new TechnologySnapshotInfo { TechId = "advRocketry", NumBytes = advRocketry.Length, Data = advRocketry }
            });
            Assert.IsTrue(store.ApplyServerMutation(payload, payload.Length, "Unlock advRocketry").Changed);

            var saved = new ConfigNode(ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"].ToString());
            var savedIds = saved.GetNodes("Tech").Select(n => n.Value.GetValue("id").Value).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "advRocketry", "basicRocketry", "engineering101" }, savedIds);
        }

        private static int CountSavedNodes(ConfigNode parent, string name)
        {
            return new ConfigNode(parent.ToString()).GetNodes(name).Count;
        }

        private static ConfigNode CreateResearchAndDevelopment(int copies, params string[] subjectIds)
        {
            var builder = new StringBuilder();
            builder.AppendLine("name = ResearchAndDevelopment");
            builder.AppendLine("scene = 5, 6, 7, 8");
            builder.AppendLine("sci = 10");
            builder.AppendLine("Tech");
            builder.AppendLine("{");
            builder.AppendLine("    id = start");
            builder.AppendLine("    state = Available");
            builder.AppendLine("    cost = 0");
            builder.AppendLine("}");
            for (var copy = 1; copy <= copies; copy++)
            {
                foreach (var id in subjectIds)
                {
                    builder.AppendLine("Science");
                    builder.AppendLine("{");
                    builder.AppendLine($"    id = {id}");
                    builder.AppendLine($"    science = {copy.ToString(CultureInfo.InvariantCulture)}");
                    builder.AppendLine("    scienceCap = 1000");
                    builder.AppendLine("}");
                }
            }

            return new ConfigNode(builder.ToString());
        }

        private static ScienceSubjectSnapshotInfo CreateSubject(string id, float science)
        {
            var data = Encoding.UTF8.GetBytes($"id = {id}\nscience = {science.ToString(CultureInfo.InvariantCulture)}\nscienceCap = 1000\n");
            return new ScienceSubjectSnapshotInfo { Id = id, NumBytes = data.Length, Data = data };
        }
    }
}
