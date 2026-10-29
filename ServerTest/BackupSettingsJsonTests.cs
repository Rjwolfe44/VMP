using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings;
using Server.Settings.Structures;
using Server.System.Backup;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ServerTest
{
    [TestClass]
    public class BackupSettingsJsonTests
    {
        private const string LegacyXmlWithSecret = "<GeneralSettingsDefinition><Password>legacy-secret-pw</Password></GeneralSettingsDefinition>";
        private string _root;
        private SettingsTestSupport.StoreSnapshot _snapshot;

        [TestInitialize]
        public void Init()
        {
            _root = SettingsTestSupport.NewTempDir();
            _snapshot = new SettingsTestSupport.StoreSnapshot();
            Directory.CreateDirectory(Path.Combine(_root, "Universe"));
            File.WriteAllText(Path.Combine(_root, "Universe", "Vessels.txt"), "x");
        }

        [TestCleanup]
        public void Cleanup()
        {
            _snapshot.Dispose();
            SettingsTestSupport.TryDelete(_root);
        }

        private static string NamesOf(BackupPlan plan) => string.Join(", ", plan.Entries.Select(e => e.ArchiveName));

        [TestMethod]
        public void UnifiedSettings_AreBackedUpOnlyAsSanitizedCopy_WithoutBakLegacyFoldersOrToken()
        {
            var config = Path.Combine(_root, "Config");
            var log = new System.Collections.Generic.List<string>();
            var file = SettingsTestSupport.NewFile(config, log);
            file.Load();
            GeneralSettings.SettingsStore.Password = "join-secret-pw";
            GeneralSettings.SettingsStore.AdminPassword = "admin-secret-pw";
            GeneralSettings.SettingsStore.ServerName = "Backup Test Server";
            file.Save();
            GeneralSettings.SettingsStore.ServerName = "Backup Test Server 2";
            file.Save(); // the previous version, with the credentials, becomes the .bak

            var legacy = Path.Combine(config, "legacy-xml-20261028-123045");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "GeneralSettings.xml"), LegacyXmlWithSecret);
            File.WriteAllText(Path.Combine(config, "admin-token.txt"), new string('a', 64));
            File.WriteAllText(Path.Combine(config, "KSPModControl.xml"), "<mods/>");
            Assert.IsTrue(File.ReadAllText(Path.Combine(config, SettingsFile.BackupFileName)).Contains("join-secret-pw"), "the .bak really holds the secret, so leaving it out matters");

            var plan = BackupSourceSelector.Build(_root);

            var names = plan.Entries.Select(e => e.ArchiveName).ToList();
            CollectionAssert.Contains(names, "Config/vmp-server.json.sanitized");
            CollectionAssert.Contains(names, "Config/KSPModControl.xml");
            CollectionAssert.Contains(names, "Universe/Vessels.txt");
            Assert.IsFalse(names.Contains("Config/vmp-server.json"), NamesOf(plan));
            Assert.IsFalse(names.Any(n => n.Contains("vmp-server.json.bak")), NamesOf(plan));
            Assert.IsFalse(names.Any(n => n.Contains("legacy-xml")), NamesOf(plan));
            Assert.IsFalse(names.Any(n => n.Contains("admin-token")), NamesOf(plan));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("vmp-server.json.bak")));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("legacy-xml-*")));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("admin-token.txt")));

            var entry = plan.Entries.Single(e => e.ArchiveName == "Config/vmp-server.json.sanitized");
            var text = new System.Text.UTF8Encoding(false).GetString(entry.Content);
            Assert.IsFalse(text.Contains("join-secret-pw") || text.Contains("admin-secret-pw"));
            var general = JsonDocument.Parse(text).RootElement.GetProperty("general");
            Assert.AreEqual("", general.GetProperty("Password").GetString());
            Assert.AreEqual("", general.GetProperty("AdminPassword").GetString());
            Assert.AreEqual("Backup Test Server 2", general.GetProperty("ServerName").GetString(), "everything else stays");
            Assert.AreEqual(8800, JsonDocument.Parse(text).RootElement.GetProperty("connection").GetProperty("Port").GetInt32());
        }

        [TestMethod]
        public void SanitizeJson_BlanksCredentialsAtAnyDepth_AndRefusesInvalidJson()
        {
            const string json = "{\"a\":{\"Password\":\"p\",\"ApiKey\":\"k\",\"Name\":\"n\",\"Nested\":[{\"authToken\":\"t\",\"X\":1}],\"MySecret\":12,\"EmptyPassword\":\"\"}}";

            Assert.IsTrue(BackupSourceSelector.TrySanitizeJson(json, out var clean, out var blanked));

            var a = JsonDocument.Parse(clean).RootElement.GetProperty("a");
            Assert.AreEqual("", a.GetProperty("Password").GetString());
            Assert.AreEqual("", a.GetProperty("ApiKey").GetString());
            Assert.AreEqual("n", a.GetProperty("Name").GetString());
            Assert.AreEqual("", a.GetProperty("Nested")[0].GetProperty("authToken").GetString());
            Assert.AreEqual(1, a.GetProperty("Nested")[0].GetProperty("X").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, a.GetProperty("MySecret").ValueKind);
            CollectionAssert.AreEquivalent(new[] { "Password", "ApiKey", "authToken", "MySecret" }, blanked.ToArray());

            Assert.IsFalse(BackupSourceSelector.TrySanitizeJson("{ nope", out _, out _));
            Assert.IsFalse(BackupSourceSelector.TrySanitizeJson("[1,2]", out _, out _));
        }

        [TestMethod]
        public void InvalidUnifiedSettings_AreLeftOutOfTheBackup_NotPackedRaw()
        {
            var config = Path.Combine(_root, "Config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "vmp-server.json"), "{ \"general\": { \"Password\": \"raw-secret\" ");

            var plan = BackupSourceSelector.Build(_root);

            Assert.IsFalse(plan.Entries.Any(e => e.ArchiveName.StartsWith("Config/vmp-server", StringComparison.Ordinal)), NamesOf(plan));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("vmp-server.json") && e.Contains("not valid JSON")));
        }
    }
}
