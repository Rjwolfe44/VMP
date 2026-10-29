using LmpCommon.Enums;
using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings;
using Server.Settings.Base;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ServerTest
{
    [TestClass]
    public class SettingsFileTests
    {
        private string _dir;
        private List<string> _log;
        private SettingsTestSupport.StoreSnapshot _snapshot;

        [TestInitialize]
        public void Init()
        {
            _dir = SettingsTestSupport.NewTempDir();
            _log = new List<string>();
            _snapshot = new SettingsTestSupport.StoreSnapshot();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _snapshot.Dispose();
            SettingsTestSupport.TryDelete(_dir);
        }

        private string JsonPath => Path.Combine(_dir, SettingsFile.FileName);

        [TestMethod]
        public void Import_CopiesEveryValue_VerifiesIt_AndMovesTheXmlFilesAside()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            var expected = SettingsTestSupport.WriteLegacyConfig(_dir, file.Sections);
            File.WriteAllText(Path.Combine(_dir, "KSPModControl.xml"), "<mod-control/>");
            foreach (var section in file.Sections)
            {
                Assert.IsFalse(SettingsCatalog.DeepEquals(section.CreateDefaults(), expected[section.SectionId], out _), section.SectionId + " must differ from its defaults for this test to prove anything");
            }

            SettingsTestSupport.ResetToDefaults(file.Sections);
            Assert.AreEqual(SettingsSource.ImportedFromXml, file.Load());

            SettingsTestSupport.AssertStoresEqual(file.Sections, expected, "after the import");
            Assert.AreEqual(14, file.Sections.Count);
            Assert.AreEqual(14, file.ImportedFileCount);
            Assert.IsTrue(File.Exists(JsonPath));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, SettingsFile.ImportCheckFileName)));
            Assert.AreEqual("legacy-xml-20261028-123045", Path.GetFileName(file.LegacyFolder));
            Assert.AreEqual(14, Directory.GetFiles(file.LegacyFolder).Length);
            foreach (var section in file.Sections)
            {
                Assert.IsFalse(File.Exists(Path.Combine(_dir, section.LegacyFilename)), section.LegacyFilename + " must have been moved");
                Assert.IsTrue(File.Exists(Path.Combine(file.LegacyFolder, section.LegacyFilename)));
            }

            Assert.IsTrue(File.Exists(Path.Combine(_dir, "KSPModControl.xml")), "files that are not settings are never touched");
            var info = _log.Single(l => l.StartsWith("INFO Imported 14"));
            StringAssert.Contains(info, "delete Config/vmp-server.json");
            StringAssert.Contains(info, "legacy-xml-20261028-123045");

            // The next start reads the new file and gets the same values.
            SettingsTestSupport.ResetToDefaults(file.Sections);
            var second = SettingsTestSupport.NewFile(_dir, _log);
            Assert.AreEqual(SettingsSource.Json, second.Load());
            SettingsTestSupport.AssertStoresEqual(second.Sections, expected, "after reading vmp-server.json");
        }

        [TestMethod]
        public void Import_Rollback_RestoringTheXmlFilesAndRemovingTheJson_GivesTheOldBehaviourBack()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            var expected = SettingsTestSupport.WriteLegacyConfig(_dir, file.Sections);
            file.Load();

            File.Delete(JsonPath);
            foreach (var path in Directory.GetFiles(file.LegacyFolder)) File.Move(path, Path.Combine(_dir, Path.GetFileName(path)));
            SettingsTestSupport.ResetToDefaults(file.Sections);

            var again = SettingsTestSupport.NewFile(_dir, _log);
            Assert.AreEqual(SettingsSource.ImportedFromXml, again.Load());
            SettingsTestSupport.AssertStoresEqual(again.Sections, expected, "after rollback and a new start");
        }

        [TestMethod]
        public void Import_Aborts_WhenTheCopyFailsVerification_AndMovesNothing()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            var expected = SettingsTestSupport.WriteLegacyConfig(_dir, file.Sections);
            file.AfterImportWrite = path => File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), "\"MaxPlayers\": (\\d+)", "\"MaxPlayers\": 1$1"));

            Assert.AreEqual(SettingsSource.ImportAborted, file.Load());

            Assert.IsFalse(File.Exists(JsonPath), "nothing is switched");
            Assert.IsFalse(File.Exists(Path.Combine(_dir, SettingsFile.ImportCheckFileName)));
            Assert.IsNull(file.LegacyFolder);
            Assert.IsFalse(Directory.GetDirectories(_dir).Any());
            foreach (var section in file.Sections) Assert.IsTrue(File.Exists(Path.Combine(_dir, section.LegacyFilename)), section.LegacyFilename + " must still be in place");
            SettingsTestSupport.AssertStoresEqual(file.Sections, expected, "the server keeps running on the XML values");
            var error = _log.Single(l => l.StartsWith("ERROR"));
            StringAssert.Contains(error, "aborted");
            StringAssert.Contains(error, "general.MaxPlayers");
            Assert.IsTrue(file.SavesBlocked);
            Assert.IsFalse(file.Save());
            Assert.IsFalse(File.Exists(JsonPath));
        }

        [TestMethod]
        public void Import_Aborts_WhenAnXmlFileCannotBeRead()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            SettingsTestSupport.WriteLegacyConfig(_dir, file.Sections);
            File.WriteAllText(Path.Combine(_dir, "CraftSettings.xml"), "this is not xml");

            Assert.AreEqual(SettingsSource.ImportAborted, file.Load());

            Assert.IsFalse(File.Exists(JsonPath));
            Assert.AreEqual(14, Directory.GetFiles(_dir, "*.xml").Length);
            StringAssert.Contains(_log.Single(l => l.StartsWith("ERROR")), "CraftSettings.xml");
        }

        [TestMethod]
        public void Import_UsesTheDefaultsForAnXmlFileThatDoesNotExist()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            var expected = SettingsTestSupport.WriteLegacyConfig(_dir, file.Sections);
            File.Delete(Path.Combine(_dir, "WarpSettings.xml"));
            SettingsTestSupport.ResetToDefaults(file.Sections);

            Assert.AreEqual(SettingsSource.ImportedFromXml, file.Load());

            Assert.AreEqual(13, file.ImportedFileCount);
            var warp = file.Sections.Single(s => s.SectionId == "warp");
            Assert.IsTrue(SettingsCatalog.DeepEquals(warp.CreateDefaults(), warp.Store, out var difference), difference);
            expected.Remove("warp");
            SettingsTestSupport.AssertStoresEqual(file.Sections.Where(s => s.SectionId != "warp").ToList(), expected, "the other sections");
        }

        [TestMethod]
        public void Defaults_AreUsedAndWritten_WhenNothingExists()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            SettingsTestSupport.ResetToDefaults(file.Sections);
            GeneralSettings.SettingsStore.ServerName = "changed in memory";

            Assert.AreEqual(SettingsSource.Defaults, file.Load());

            Assert.IsTrue(File.Exists(JsonPath));
            foreach (var section in file.Sections)
            {
                var fresh = section.SectionId == "gameplay" ? section.CreateDefaults() : Activator.CreateInstance(section.DefinitionType);
                Assert.IsTrue(SettingsCatalog.DeepEquals(fresh, section.Store, out var difference, section.SectionId), difference);
            }

            Assert.AreEqual(0, Directory.GetDirectories(_dir).Length);
            var bytes = File.ReadAllBytes(JsonPath);
            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 without BOM");
        }

        [TestMethod]
        public void Json_RoundTrip_KeepsEnumsAsStrings_AndToleratesMissingAndUnknownSettings()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            file.Load();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            GeneralSettings.SettingsStore.GameDifficulty = GameDifficulty.Hard;
            GeneralSettings.SettingsStore.SnapshotCompression = Server.Settings.Definition.SnapshotCompressionMode.QuickLz;
            GeneralSettings.SettingsStore.MaxPlayers = 33;
            WarpSettings.SettingsStore.WarpMode = WarpMode.None;
            Assert.IsTrue(file.Save());

            var text = File.ReadAllText(JsonPath);
            StringAssert.Contains(text, "\"GameMode\": \"Career\"");
            StringAssert.Contains(text, "\"WarpMode\": \"None\"");
            StringAssert.Contains(text, "\"SnapshotCompression\": \"QuickLz\"");
            StringAssert.Contains(text, "\"general\"");

            // Remove one setting and one section, add an unknown setting and an unknown section.
            var root = JsonNode.Parse(text).AsObject();
            root["general"].AsObject().Remove("Cheats");
            root["general"].AsObject()["SomethingFromTheFuture"] = 5;
            root.Remove("craft");
            root["futureSection"] = new JsonObject { ["A"] = 1 };
            File.WriteAllText(JsonPath, root.ToJsonString(), new UTF8Encoding(false));

            SettingsTestSupport.ResetToDefaults(file.Sections);
            GeneralSettings.SettingsStore.Cheats = false; // would show if the missing value were kept instead of defaulted
            var second = SettingsTestSupport.NewFile(_dir, _log);
            Assert.AreEqual(SettingsSource.Json, second.Load());

            Assert.AreEqual(GameMode.Career, GeneralSettings.SettingsStore.GameMode);
            Assert.AreEqual(GameDifficulty.Hard, GeneralSettings.SettingsStore.GameDifficulty);
            Assert.AreEqual(33, GeneralSettings.SettingsStore.MaxPlayers);
            Assert.AreEqual(WarpMode.None, WarpSettings.SettingsStore.WarpMode);
            Assert.IsTrue(GeneralSettings.SettingsStore.Cheats, "a missing setting gets its default");
            Assert.AreEqual(10, CraftSettings.SettingsStore.MaxCraftsPerUser, "a missing section gets its defaults");
            var rewritten = JsonNode.Parse(File.ReadAllText(JsonPath)).AsObject();
            Assert.IsTrue(rewritten["general"].AsObject().ContainsKey("Cheats"), "new settings appear in the file");
            Assert.IsTrue(rewritten.ContainsKey("craft"));
            Assert.IsTrue(File.Exists(Path.Combine(_dir, SettingsFile.BackupFileName)));
        }

        [TestMethod]
        public void Save_IsAtomic_LeavesNoTempFile_AndKeepsOnlyTheLastVersionAsBak()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            file.Load();
            var v1 = File.ReadAllText(JsonPath);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, SettingsFile.BackupFileName)));

            GeneralSettings.SettingsStore.ServerName = "second";
            Assert.IsTrue(file.Save());
            var v2 = File.ReadAllText(JsonPath);
            Assert.AreNotEqual(v1, v2);
            Assert.AreEqual(v1, File.ReadAllText(Path.Combine(_dir, SettingsFile.BackupFileName)));

            GeneralSettings.SettingsStore.ServerName = "third";
            Assert.IsTrue(file.Save());
            Assert.AreEqual(v2, File.ReadAllText(Path.Combine(_dir, SettingsFile.BackupFileName)), "only the version before the last save is kept");

            var v3 = File.ReadAllText(JsonPath);
            Assert.IsTrue(file.Save()); // nothing changed: nothing is written and the .bak is not rotated
            Assert.AreEqual(v3, File.ReadAllText(JsonPath));
            Assert.AreEqual(v2, File.ReadAllText(Path.Combine(_dir, SettingsFile.BackupFileName)));

            var names = Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { SettingsFile.FileName, SettingsFile.BackupFileName }, names);
        }

        [TestMethod]
        public void UnreadableJson_KeepsTheFileAndRunsOnDefaults()
        {
            File.WriteAllText(JsonPath, "{ this is not json");
            var file = SettingsTestSupport.NewFile(_dir, _log);

            Assert.AreEqual(SettingsSource.JsonUnreadable, file.Load());

            Assert.AreEqual("{ this is not json", File.ReadAllText(JsonPath));
            Assert.IsFalse(file.Save());
            Assert.AreEqual("{ this is not json", File.ReadAllText(JsonPath));
            Assert.AreEqual("KSP Server", GeneralSettings.SettingsStore.ServerName);
            StringAssert.Contains(_log.First(l => l.StartsWith("ERROR")), "will not be changed");
        }

        [TestMethod]
        public void WrongTypeInOneSection_BlocksSavingButOtherSectionsLoad()
        {
            var file = SettingsTestSupport.NewFile(_dir, _log);
            file.Load();
            var root = JsonNode.Parse(File.ReadAllText(JsonPath)).AsObject();
            root["general"].AsObject()["MaxPlayers"] = "many";
            root["connection"].AsObject()["Port"] = 9999;
            File.WriteAllText(JsonPath, root.ToJsonString(), new UTF8Encoding(false));
            var text = File.ReadAllText(JsonPath);

            var second = SettingsTestSupport.NewFile(_dir, _log);
            Assert.AreEqual(SettingsSource.JsonUnreadable, second.Load());

            Assert.AreEqual(9999, ConnectionSettings.SettingsStore.Port);
            Assert.AreEqual(20, GeneralSettings.SettingsStore.MaxPlayers, "the broken section falls back to its defaults");
            Assert.AreEqual(text, File.ReadAllText(JsonPath));
        }

        /// <summary>
        /// Skipped unless VMP_TEST_LIVE_CONFIG points to a folder with a copy of the real settings XML files (secrets may be blanked). It imports
        /// them into a temporary Config and compares every property of every section with an independent read of the XML. It prints counts only.
        /// </summary>
        [TestMethod]
        public void ImportsTheRealLiveConfig()
        {
            var source = Environment.GetEnvironmentVariable("VMP_TEST_LIVE_CONFIG");
            if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) Assert.Inconclusive("VMP_TEST_LIVE_CONFIG is not set to a folder");

            var file = SettingsTestSupport.NewFile(_dir, _log);
            var expected = new Dictionary<string, object>();
            var present = 0;
            foreach (var path in Directory.GetFiles(source))
            {
                File.Copy(path, Path.Combine(_dir, Path.GetFileName(path)));
            }

            SettingsTestSupport.ResetToDefaults(file.Sections);
            foreach (var section in file.Sections)
            {
                var path = Path.Combine(source, section.LegacyFilename);
                if (File.Exists(path))
                {
                    present++;
                    expected[section.SectionId] = LunaXmlSerializer.ReadXmlFromPath(section.DefinitionType, path);
                }
            }

            Assert.IsTrue(present > 0, "the folder holds none of the settings files");
            var others = Directory.GetFiles(_dir).Select(Path.GetFileName).Where(n => file.Sections.All(s => s.LegacyFilename != n)).ToArray();

            Assert.AreEqual(SettingsSource.ImportedFromXml, file.Load(), string.Join(" | ", _log));
            Assert.AreEqual(present, file.ImportedFileCount);
            Assert.AreEqual(present, Directory.GetFiles(file.LegacyFolder).Length);
            foreach (var other in others) Assert.IsTrue(File.Exists(Path.Combine(_dir, other)), other + " is not a settings file and must stay");

            var properties = 0;
            foreach (var section in file.Sections.Where(s => expected.ContainsKey(s.SectionId)))
            {
                Assert.IsTrue(SettingsCatalog.DeepEquals(expected[section.SectionId], section.Store, out var difference, section.SectionId), "in memory: " + difference);
                properties += SettingsCatalog.PropertiesOf(section.DefinitionType).Length;
            }

            SettingsTestSupport.ResetToDefaults(file.Sections);
            var second = SettingsTestSupport.NewFile(_dir, _log);
            Assert.AreEqual(SettingsSource.Json, second.Load());
            foreach (var section in second.Sections.Where(s => expected.ContainsKey(s.SectionId)))
            {
                Assert.IsTrue(SettingsCatalog.DeepEquals(expected[section.SectionId], section.Store, out var difference, section.SectionId), "after reading vmp-server.json: " + difference);
            }

            Console.WriteLine($"Live config import: {present} XML files, {properties} properties compared in memory and again after reloading vmp-server.json, all equal; {others.Length} other files untouched.");
        }
    }
}
