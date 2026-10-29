using LmpCommon.Enums;
using LmpCommon.Stats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using Server.Settings;
using Server.Settings.Base;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerTest
{
    [TestClass]
    public class AdminSettingsTests
    {
        private const string Token = "settings-test-token-0123456789abcdef";
        private SettingsTestSupport.StoreSnapshot _snapshot;
        private List<ISettings> _sections;
        private List<string> _log;
        private int _saves;
        private Func<bool> _saveBehaviour;
        private SettingsAdmin _admin;
        private AdminApiHandler _handler;

        [TestInitialize]
        public void Init()
        {
            _snapshot = new SettingsTestSupport.StoreSnapshot();
            _sections = SettingsFile.DiscoverSections();
            SettingsTestSupport.ResetToDefaults(_sections);
            _log = new List<string>();
            _saves = 0;
            _saveBehaviour = () => true;
            _admin = new SettingsAdmin(_sections, () =>
            {
                _saves++;
                return _saveBehaviour();
            }, _log.Add);
            _handler = new AdminApiHandler(new NoBackend(), Token, null, _admin);
        }

        [TestCleanup]
        public void Cleanup() => _snapshot.Dispose();

        private sealed class NoBackend : IAdminApiBackend
        {
            public StatusInfo GetStatus() => new StatusInfo();
            public NetworkInfo GetNetwork() => new NetworkInfo();
            public List<ClientInfo> GetClients() => new List<ClientInfo>();
            public List<string> ReadLog(int lines) => new List<string>();
            public ReportsInfo GetReports() => new ReportsInfo();
            public List<BackupFileEntry> ListBackups() => new List<BackupFileEntry>();
            public Task<BackupCreateInfo> CreateBackupAsync() => Task.FromResult<BackupCreateInfo>(null);
            public void Say(string text) { }
            public bool Kick(string name, string reason) => false;
        }

        private AdminApiResponse Call(string method, string path, string body = null, AdminApiHandler handler = null)
        {
            var request = new AdminApiRequest
            {
                Method = method,
                Path = path,
                Authorization = "Bearer " + Token,
                Body = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body),
                RemoteAddress = IPAddress.Loopback,
            };
            return (handler ?? _handler).HandleAsync(request).GetAwaiter().GetResult();
        }

        private AdminApiResponse Put(string changesJson) => Call("PUT", "/api/v1/settings", "{\"changes\":" + changesJson + "}");

        private static JsonElement Parse(AdminApiResponse response) => JsonDocument.Parse(response.Json).RootElement.Clone();

        private static JsonElement Setting(JsonElement root, string key)
        {
            return root.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("settings").EnumerateArray()).Single(s => s.GetProperty("key").GetString() == key);
        }

        private JsonElement Schema()
        {
            var response = Call("GET", "/api/v1/settings");
            Assert.AreEqual(200, response.StatusCode);
            return Parse(response);
        }

        [TestMethod]
        public void Schema_CoversEveryPropertyOfEveryDefinition()
        {
            var root = Schema();
            var sections = root.GetProperty("sections").EnumerateArray().ToList();
            Assert.AreEqual(13, sections.Count);
            Assert.AreEqual(13, _sections.Select(s => s.DefinitionType).Distinct().Count());

            var expectedKeys = new List<string>();
            foreach (var section in _sections)
            {
                var id = SettingsCatalog.SectionIdOf(section.DefinitionType);
                foreach (var property in section.DefinitionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) expectedKeys.Add(id + "." + property.Name);
                Assert.AreEqual(0, section.DefinitionType.GetFields(BindingFlags.Public | BindingFlags.Instance).Length, section.SectionId + " has a public field, which would be missing from the file and the API");
            }

            var keys = sections.SelectMany(s => s.GetProperty("settings").EnumerateArray()).Select(s => s.GetProperty("key").GetString()).ToList();
            CollectionAssert.AreEquivalent(expectedKeys, keys);
            Assert.AreEqual(expectedKeys.Count, _admin.SettingCount);
            Assert.AreEqual(keys.Count, keys.Distinct().Count());
            Assert.IsTrue(keys.Count > 100);

            // Every setting is in the file too.
            var file = SettingsFile.Create(SettingsTestSupport.NewTempDir());
            var document = JsonDocument.Parse(file.Serialize()).RootElement;
            foreach (var key in keys)
            {
                var dot = key.IndexOf('.');
                Assert.IsTrue(document.GetProperty(key.Substring(0, dot)).TryGetProperty(key.Substring(dot + 1), out _), key + " is not in vmp-server.json");
            }
        }

        [TestMethod]
        public void Schema_HasLabelsDescriptionsRangesDefaultsAndRestartFlags()
        {
            var root = Schema();

            var name = Setting(root, "general.ServerName");
            Assert.AreEqual("Server Name", name.GetProperty("label").GetString());
            StringAssert.StartsWith(name.GetProperty("description").GetString(), "Name of the server");
            Assert.AreEqual("string", name.GetProperty("type").GetString());
            Assert.AreEqual(30, name.GetProperty("maxLength").GetInt32());
            Assert.AreEqual("KSP Server", name.GetProperty("default").GetString());
            Assert.AreEqual("KSP Server", name.GetProperty("value").GetString());
            Assert.IsFalse(name.GetProperty("modified").GetBoolean());
            Assert.IsFalse(name.GetProperty("restartRequired").GetBoolean());
            Assert.IsFalse(name.GetProperty("secret").GetBoolean());

            Assert.AreEqual(255, Setting(root, "general.ServerMotd").GetProperty("maxLength").GetInt32());
            Assert.AreEqual("Max Vessel Parts", Setting(root, "general.MaxVesselParts").GetProperty("label").GetString());

            var port = Setting(root, "connection.Port");
            Assert.AreEqual("int", port.GetProperty("type").GetString());
            Assert.AreEqual(1, port.GetProperty("min").GetInt32());
            Assert.AreEqual(65535, port.GetProperty("max").GetInt32());
            Assert.AreEqual(8800, port.GetProperty("default").GetInt32());
            Assert.IsTrue(port.GetProperty("restartRequired").GetBoolean());

            Assert.AreEqual(0, Setting(root, "general.MaxPlayers").GetProperty("min").GetInt32());
            Assert.AreEqual(255, Setting(root, "dedicatedServer.Red").GetProperty("max").GetInt32());
            Assert.AreEqual(8192, Setting(root, "connection.MaximumTransmissionUnit").GetProperty("max").GetInt32());

            var mode = Setting(root, "general.GameMode");
            Assert.AreEqual("enum", mode.GetProperty("type").GetString());
            CollectionAssert.AreEqual(new[] { "Sandbox", "Career", "Science" }.OrderBy(x => x).ToArray(), mode.GetProperty("options").EnumerateArray().Select(o => o.GetString()).OrderBy(x => x).ToArray());
            Assert.AreEqual("Sandbox", mode.GetProperty("value").GetString());

            Assert.AreEqual("double", Setting(root, "general.SafetyBubbleDistance").GetProperty("type").GetString());
            Assert.AreEqual("bool", Setting(root, "general.Cheats").GetProperty("type").GetString());
        }

        [TestMethod]
        public void RestartRequired_IsTheDecidedList()
        {
            var root = Schema();
            var live = root.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("settings").EnumerateArray())
                .Where(s => !s.GetProperty("restartRequired").GetBoolean()).Select(s => s.GetProperty("key").GetString()).ToList();

            var expected = new List<string>
            {
                "general.ServerName", "general.ServerMotd", "general.Password", "general.AdminPassword", "general.Cheats", "general.AllowSackKerbals",
            };
            foreach (var section in _sections.Where(s => new[] { "screenshot", "craft", "gameplay" }.Contains(s.SectionId)))
            {
                expected.AddRange(section.DefinitionType.GetProperties().Select(p => section.SectionId + "." + p.Name));
            }

            CollectionAssert.AreEquivalent(expected, live);
            Assert.IsFalse(live.Contains("general.MaxPlayers"), "the connection limit is copied at start");
            Assert.IsFalse(live.Contains("general.ModControl"));
        }

        [TestMethod]
        public void Secrets_AreNeverReturned_OnlyWhetherTheyAreSet()
        {
            GeneralSettings.SettingsStore.Password = "hunter2-secret";
            var response = Call("GET", "/api/v1/settings");
            StringAssert.DoesNotMatch(response.Json, new System.Text.RegularExpressions.Regex("hunter2"));

            var root = Parse(response);
            var password = Setting(root, "general.Password");
            Assert.AreEqual(JsonValueKind.Null, password.GetProperty("value").ValueKind);
            Assert.IsTrue(password.GetProperty("secret").GetBoolean());
            Assert.IsTrue(password.GetProperty("isSet").GetBoolean());
            Assert.IsTrue(password.GetProperty("modified").GetBoolean());
            Assert.IsFalse(Setting(root, "general.AdminPassword").GetProperty("isSet").GetBoolean());
            Assert.IsFalse(Setting(root, "general.ServerName").TryGetProperty("isSet", out _));

            var secrets = root.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("settings").EnumerateArray()).Where(s => s.GetProperty("secret").GetBoolean()).Select(s => s.GetProperty("key").GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "general.Password", "general.AdminPassword" }, secrets);
        }

        [TestMethod]
        public void Put_AppliesEverything_SavesOnce_LogsEachChange_AndReportsRestartRequired()
        {
            var response = Put("{\"general.ServerName\":\"My server\",\"connection.Port\":8801,\"general.Cheats\":false,\"screenshot.MaxScreenshotWidth\":800,\"general.GameMode\":\"career\",\"general.SafetyBubbleDistance\":12.5}");

            Assert.AreEqual(200, response.StatusCode, response.Json);
            var body = Parse(response);
            CollectionAssert.AreEqual(new[] { "general.ServerName", "connection.Port", "general.Cheats", "screenshot.MaxScreenshotWidth", "general.GameMode", "general.SafetyBubbleDistance" },
                body.GetProperty("applied").EnumerateArray().Select(a => a.GetString()).ToArray());
            CollectionAssert.AreEqual(new[] { "connection.Port", "general.GameMode", "general.SafetyBubbleDistance" }, body.GetProperty("restartRequired").EnumerateArray().Select(a => a.GetString()).ToArray());
            Assert.IsTrue(body.GetProperty("saved").GetBoolean());

            Assert.AreEqual("My server", GeneralSettings.SettingsStore.ServerName);
            Assert.AreEqual(8801, ConnectionSettings.SettingsStore.Port);
            Assert.IsFalse(GeneralSettings.SettingsStore.Cheats);
            Assert.AreEqual(800, ScreenshotSettings.SettingsStore.MaxScreenshotWidth);
            Assert.AreEqual(GameMode.Career, GeneralSettings.SettingsStore.GameMode);
            Assert.AreEqual(12.5f, GeneralSettings.SettingsStore.SafetyBubbleDistance);
            Assert.AreEqual(1, _saves);
            Assert.AreEqual(6, _log.Count);
            Assert.IsTrue(_log.Contains("[Settings] general.ServerName: 'KSP Server' -> 'My server' (changed through the admin API)"), string.Join("\n", _log));
            Assert.IsTrue(_log.Any(l => l.Contains("connection.Port: 8800 -> 8801")));
            Assert.IsTrue(_log.Any(l => l.Contains("general.GameMode: Sandbox -> Career")));
        }

        [TestMethod]
        public void Put_MasksSecretsInTheLog_AndNeverEchoesThem()
        {
            var response = Put("{\"general.Password\":\"new-secret-value\",\"general.AdminPassword\":\"admin-secret-value\"}");

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("new-secret-value", GeneralSettings.SettingsStore.Password);
            Assert.AreEqual(2, _log.Count);
            Assert.IsTrue(_log.All(l => l.Contains("*** -> ***")), string.Join("\n", _log));
            Assert.IsFalse(string.Join("\n", _log).Contains("secret-value"));
            Assert.IsFalse(response.Json.Contains("secret-value"));
        }

        [TestMethod]
        public void Put_IsAllOrNone_AndListsEveryError()
        {
            var longName = new string('x', 31);
            var response = Put("{\"general.ServerName\":\"" + longName + "\",\"connection.Port\":70000,\"general.MaxPlayers\":-1,\"general.Cheats\":\"yes\"," +
                               "\"general.GameMode\":\"Creative\",\"warp.Nothing\":1,\"craft.MaxCraftsPerUser\":2.5,\"general.ServerMotd\":5,\"screenshot.MaxScreenshotWidth\":640}");

            Assert.AreEqual(400, response.StatusCode);
            var body = Parse(response);
            var errors = body.GetProperty("errors").EnumerateArray().ToDictionary(e => e.GetProperty("key").GetString(), e => e.GetProperty("message").GetString());
            CollectionAssert.AreEquivalent(new[]
            {
                "general.ServerName", "connection.Port", "general.MaxPlayers", "general.Cheats", "general.GameMode", "warp.Nothing", "craft.MaxCraftsPerUser", "general.ServerMotd",
            }, errors.Keys.ToArray());
            StringAssert.Contains(errors["general.ServerName"], "at most 30");
            StringAssert.Contains(errors["connection.Port"], "at most 65535");
            StringAssert.Contains(errors["general.MaxPlayers"], "at least 0");
            StringAssert.Contains(errors["general.Cheats"], "true or false");
            StringAssert.Contains(errors["general.GameMode"], "Sandbox");
            StringAssert.Contains(errors["warp.Nothing"], "unknown");
            StringAssert.Contains(errors["craft.MaxCraftsPerUser"], "whole number");
            StringAssert.Contains(errors["general.ServerMotd"], "string");
            StringAssert.Contains(body.GetProperty("error").GetString(), "warp.Nothing");

            Assert.AreEqual("KSP Server", GeneralSettings.SettingsStore.ServerName, "nothing is applied when one change is wrong");
            Assert.AreEqual(1920, ScreenshotSettings.SettingsStore.MaxScreenshotWidth, "not even the valid change");
            Assert.AreEqual(0, _saves);
            Assert.AreEqual(0, _log.Count);
        }

        [TestMethod]
        public void Put_RejectsTheAdminApiSection_WithForbidden()
        {
            var response = Put("{\"adminApi.Port\":18901,\"general.ServerName\":\"x\"}");

            Assert.AreEqual(403, response.StatusCode);
            StringAssert.Contains(response.Json, "edit Config/vmp-server.json and restart");
            Assert.AreEqual(18900, AdminApiSettings.SettingsStore.Port);
            Assert.AreEqual("KSP Server", GeneralSettings.SettingsStore.ServerName);
            Assert.AreEqual(0, _saves);
            Assert.AreEqual(403, Put("{\"AdminApi.EnableAdminApi\":false}").StatusCode);
            Assert.IsTrue(AdminApiSettings.SettingsStore.EnableAdminApi);
        }

        [TestMethod]
        public void Put_IsRolledBack_WhenTheSettingsCannotBeSaved()
        {
            _saveBehaviour = () => false;
            var blocked = Put("{\"general.ServerName\":\"blocked\"}");
            Assert.AreEqual(409, blocked.StatusCode);
            Assert.AreEqual("KSP Server", GeneralSettings.SettingsStore.ServerName);

            _saveBehaviour = () => throw new System.IO.IOException("disk full");
            var failed = Put("{\"general.ServerName\":\"failed\",\"general.Cheats\":false}");
            Assert.AreEqual(500, failed.StatusCode);
            Assert.AreEqual("KSP Server", GeneralSettings.SettingsStore.ServerName);
            Assert.IsTrue(GeneralSettings.SettingsStore.Cheats);
        }

        [TestMethod]
        public void Put_Validation_OfBodyShape()
        {
            Assert.AreEqual(400, Call("PUT", "/api/v1/settings", "").StatusCode);
            Assert.AreEqual(400, Call("PUT", "/api/v1/settings", "{not json").StatusCode);
            Assert.AreEqual(400, Call("PUT", "/api/v1/settings", "[]").StatusCode);
            Assert.AreEqual(400, Call("PUT", "/api/v1/settings", "{\"changes\":[]}").StatusCode);
            Assert.AreEqual(400, Call("PUT", "/api/v1/settings", "{\"changes\":{}}").StatusCode);
            Assert.AreEqual(405, Call("POST", "/api/v1/settings", "{}").StatusCode);
            Assert.AreEqual(405, Call("DELETE", "/api/v1/settings").StatusCode);
            Assert.AreEqual(404, Call("GET", "/api/v1/settings", null, new AdminApiHandler(new NoBackend(), Token)).StatusCode, "without a settings service the route does not exist");
            Assert.AreEqual(0, _saves);
        }

        [TestMethod]
        public void BodyLimit_Is64KbForThePutAndFourKbForEverythingElse()
        {
            Assert.AreEqual(65536, AdminApiHandler.BodyLimit("PUT", "/api/v1/settings"));
            Assert.AreEqual(65536, AdminApiHandler.BodyLimit("put", "/api/v1/settings/"));
            Assert.AreEqual(4096, AdminApiHandler.BodyLimit("GET", "/api/v1/settings"));
            Assert.AreEqual(4096, AdminApiHandler.BodyLimit("POST", "/api/v1/say"));

            var padding = new string(' ', 10000);
            Assert.AreEqual(200, Call("PUT", "/api/v1/settings", "{\"changes\":{\"general.ServerName\":\"ok\"}}" + padding).StatusCode);
            Assert.AreEqual(413, Call("POST", "/api/v1/say", "{\"text\":\"hi\"}" + padding).StatusCode);
            Assert.AreEqual(413, Call("PUT", "/api/v1/settings", "{\"changes\":{}}" + new string(' ', 70000)).StatusCode);
        }
    }
}
