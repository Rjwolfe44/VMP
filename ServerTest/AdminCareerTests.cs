using LmpCommon.Enums;
using LmpCommon.PersistentSync;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using Server.Settings.Structures;
using Server.System;
using Server.System.PersistentSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerTest
{
    /// <summary>GET /api/v1/career and POST /api/v1/career/funds|science: the handler against a fake backend, and the real backend against a real registry.</summary>
    [TestClass]
    public class AdminCareerTests
    {
        private const string Token = "career-test-token-0123456789abcdef0123456789abcdef";

        private sealed class FakeCareerBackend : IAdminApiBackend
        {
            public CareerInfo Career = new CareerInfo
            {
                GameMode = "Career",
                Funds = new CareerCurrencyInfo { Available = true, Value = 1234.5 },
                Science = new CareerCurrencyInfo { Available = true, Value = 10 },
            };

            public CareerChangeResult Result = new CareerChangeResult { Status = CareerChangeStatus.Changed, Previous = 5000, Current = 105000 };
            public readonly List<(CareerCurrency Currency, CareerChangeMode Mode, double Amount)> Changes = new List<(CareerCurrency, CareerChangeMode, double)>();
            public readonly List<string> Said = new List<string>();
            public bool SayThrows;

            public StatusInfo GetStatus() => new StatusInfo();
            public NetworkInfo GetNetwork() => new NetworkInfo();
            public List<ClientInfo> GetClients() => new List<ClientInfo>();
            public List<string> ReadLog(int lines) => new List<string>();
            public ReportsInfo GetReports() => new ReportsInfo();
            public List<BackupFileEntry> ListBackups() => new List<BackupFileEntry>();
            public Task<BackupCreateInfo> CreateBackupAsync() => Task.FromResult<BackupCreateInfo>(null);
            public bool Kick(string name, string reason) => false;

            public void Say(string text)
            {
                if (SayThrows) throw new InvalidOperationException("chat down");
                Said.Add(text);
            }

            public CareerInfo GetCareer() => Career;

            public CareerChangeResult ChangeCareer(CareerCurrency currency, CareerChangeMode mode, double amount)
            {
                Changes.Add((currency, mode, amount));
                return Result;
            }
        }

        private static AdminApiRequest Req(string method, string path, string body = null, string auth = "Bearer " + Token, IPAddress remote = null)
        {
            return new AdminApiRequest
            {
                Method = method,
                Path = path,
                Authorization = auth,
                Body = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body),
                RemoteAddress = remote ?? IPAddress.Loopback,
            };
        }

        private static AdminApiResponse Send(AdminApiHandler handler, AdminApiRequest request) => handler.HandleAsync(request).GetAwaiter().GetResult();

        // ---- handler against a fake backend ----

        [TestMethod]
        public void Get_AnswersTheExactJsonShape_AndNullValuesWhenUnavailable()
        {
            var backend = new FakeCareerBackend();
            var handler = new AdminApiHandler(backend, Token);

            var career = Send(handler, Req("GET", "/api/v1/career"));
            Assert.AreEqual(200, career.StatusCode);
            Assert.AreEqual("{\"gameMode\":\"Career\",\"funds\":{\"available\":true,\"value\":1234.5},\"science\":{\"available\":true,\"value\":10}}", career.Json);

            backend.Career = new CareerInfo { GameMode = "Sandbox" };
            var sandbox = Send(handler, Req("GET", "/api/v1/career/"));
            Assert.AreEqual("{\"gameMode\":\"Sandbox\",\"funds\":{\"available\":false,\"value\":null},\"science\":{\"available\":false,\"value\":null}}", sandbox.Json);
        }

        [TestMethod]
        public void Routing_AuthMethodsAndUnknownPaths_FollowTheExistingRules()
        {
            var backend = new FakeCareerBackend();
            var handler = new AdminApiHandler(backend, Token);
            const string ok = "{\"mode\":\"add\",\"amount\":1}";

            Assert.AreEqual(401, Send(handler, Req("GET", "/api/v1/career", auth: null)).StatusCode);
            Assert.AreEqual(401, Send(handler, Req("POST", "/api/v1/career/funds", ok, auth: "Bearer wrong")).StatusCode);
            Assert.AreEqual(403, Send(handler, Req("GET", "/api/v1/career", remote: IPAddress.Parse("192.0.2.5"))).StatusCode);
            Assert.AreEqual(403, Send(handler, Req("POST", "/api/v1/career/science", ok, remote: IPAddress.Parse("192.0.2.5"))).StatusCode);
            Assert.AreEqual(0, backend.Changes.Count, "nothing ran for a refused caller");

            Assert.AreEqual(405, Send(handler, Req("POST", "/api/v1/career", ok)).StatusCode);
            Assert.AreEqual(405, Send(handler, Req("GET", "/api/v1/career/funds")).StatusCode);
            Assert.AreEqual(405, Send(handler, Req("PUT", "/api/v1/career/science", ok)).StatusCode);
            Assert.AreEqual(404, Send(handler, Req("POST", "/api/v1/career/reputation", ok)).StatusCode);
            Assert.AreEqual(0, backend.Changes.Count);

            var tooBig = Req("POST", "/api/v1/career/funds");
            tooBig.Body = new byte[AdminApiHandler.MaxBodyBytes + 1];
            Assert.AreEqual(413, Send(handler, tooBig).StatusCode);
            Assert.AreEqual(0, backend.Changes.Count);
        }

        [DataTestMethod]
        [DataRow("funds", "", "a JSON body is required")]
        [DataRow("funds", "not json", "invalid JSON")]
        [DataRow("funds", "[1]", "invalid JSON")]
        [DataRow("funds", "{\"amount\":5}", "mode must be")]
        [DataRow("funds", "{\"mode\":\"multiply\",\"amount\":5}", "mode must be")]
        [DataRow("funds", "{\"mode\":\"add\"}", "amount is required")]
        [DataRow("funds", "{\"mode\":\"add\",\"amount\":null}", "amount is required")]
        [DataRow("funds", "{\"mode\":\"add\",\"amount\":\"5\"}", "invalid JSON")]
        [DataRow("funds", "{\"mode\":\"add\",\"amount\":\"NaN\"}", "finite")]
        [DataRow("funds", "{\"mode\":\"set\",\"amount\":\"Infinity\"}", "finite")]
        [DataRow("science", "{\"mode\":\"set\",\"amount\":\"-Infinity\"}", "finite")]
        [DataRow("funds", "{\"mode\":\"add\",\"amount\":1e999}", "")]
        [DataRow("funds", "{\"mode\":\"add\",\"amount\":1000000000001}", "too large")]
        [DataRow("funds", "{\"mode\":\"set\",\"amount\":-1000000000001}", "too large")]
        [DataRow("science", "{\"mode\":\"add\",\"amount\":1000000001}", "too large")]
        [DataRow("funds", "{\"mode\":\"add\",\"amount\":1,\"note\":\"012345678901234567890123456789012345678901234567890123456789012345678901234567890\"}", "note is too long")]
        public void Post_RejectsBadRequests_With400_AndNeverReachesTheBackend(string currency, string body, string messagePart)
        {
            var backend = new FakeCareerBackend();
            var handler = new AdminApiHandler(backend, Token);

            var response = Send(handler, Req("POST", "/api/v1/career/" + currency, body));

            Assert.AreEqual(400, response.StatusCode, response.Json);
            Assert.IsTrue(JsonDocument.Parse(response.Json).RootElement.GetProperty("error").GetString().Contains(messagePart, StringComparison.Ordinal), response.Json);
            Assert.AreEqual(0, backend.Changes.Count);
        }

        [TestMethod]
        public void Post_BoundsAreInclusive_AndTheBackendDecidesTheRest()
        {
            var backend = new FakeCareerBackend();
            var handler = new AdminApiHandler(backend, Token);

            Assert.AreEqual(200, Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":-1000000000000}")).StatusCode);
            Assert.AreEqual(200, Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"set\",\"amount\":1000000000}")).StatusCode);

            // A result outside the range or a negative science is the backend's call (it knows the current value): 400, a plain message.
            backend.Result = CareerChangeResult.Refused(CareerChangeStatus.OutOfBounds, "science cannot go below 0 (it would end up at -50); nothing was changed");
            var negative = Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"add\",\"amount\":-100}"));
            Assert.AreEqual(400, negative.StatusCode);
            Assert.AreEqual("{\"error\":\"science cannot go below 0 (it would end up at -50); nothing was changed\"}", negative.Json);

            backend.Result = CareerChangeResult.Refused(CareerChangeStatus.Unavailable, "There are no funds in Sandbox mode; they exist only in Career mode.");
            var sandbox = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":1}"));
            Assert.AreEqual(409, sandbox.StatusCode);
            Assert.AreEqual("{\"error\":\"There are no funds in Sandbox mode; they exist only in Career mode.\"}", sandbox.Json);

            backend.Result = CareerChangeResult.Refused(CareerChangeStatus.NotReady, CareerAdmin.NotReadyMessage);
            Assert.AreEqual(409, Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":1}")).StatusCode);

            // A backend without career support (the interface default) is a 409 too, never a 500.
            Assert.AreEqual(409, Send(new AdminApiHandler(new DefaultOnlyBackend(), Token), Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":1}")).StatusCode);
            Assert.AreEqual("{\"gameMode\":\"Sandbox\",\"funds\":{\"available\":false,\"value\":null},\"science\":{\"available\":false,\"value\":null}}",
                Send(new AdminApiHandler(new DefaultOnlyBackend(), Token), Req("GET", "/api/v1/career")).Json);
        }

        private sealed class DefaultOnlyBackend : IAdminApiBackend
        {
            public StatusInfo GetStatus() => null;
            public NetworkInfo GetNetwork() => null;
            public List<ClientInfo> GetClients() => null;
            public List<string> ReadLog(int lines) => null;
            public ReportsInfo GetReports() => null;
            public List<BackupFileEntry> ListBackups() => null;
            public Task<BackupCreateInfo> CreateBackupAsync() => null;
            public void Say(string text) { }
            public bool Kick(string name, string reason) => false;
        }

        [TestMethod]
        public void Post_Success_AnswersTheExactShape_PassesTheRequestOn_AndLogsOneLine()
        {
            var backend = new FakeCareerBackend();
            var lines = new List<string>();
            var handler = new AdminApiHandler(backend, Token, null, null, null, null, lines.Add);

            var response = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"ADD\",\"amount\":100000,\"note\":\"  rescue\\tmission \\u001b[31m \"}"));

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("{\"ok\":true,\"currency\":\"funds\",\"previous\":5000,\"current\":105000}", response.Json);
            CollectionAssert.AreEqual(new[] { (CareerCurrency.Funds, CareerChangeMode.Add, 100000d) }, backend.Changes.ToArray());
            Assert.AreEqual(0, backend.Said.Count, "announce defaults to false");
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("[AdminApi] career funds add 100000: previous 5000, current 105000, note \"rescue mission  [31m\"", lines[0]);

            backend.Result = new CareerChangeResult { Status = CareerChangeStatus.Changed, Previous = 12.5, Current = 40 };
            var science = Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"set\",\"amount\":40}"));
            Assert.AreEqual("{\"ok\":true,\"currency\":\"science\",\"previous\":12.5,\"current\":40}", science.Json);
            Assert.AreEqual((CareerCurrency.Science, CareerChangeMode.Set, 40d), backend.Changes[1]);
            Assert.AreEqual("[AdminApi] career science set 40: previous 12.5, current 40", lines[1], "no note, no note text in the line");

            // A refused change is not logged as an accepted one.
            backend.Result = CareerChangeResult.Refused(CareerChangeStatus.Unavailable, "x");
            Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":1}"));
            Assert.AreEqual(2, lines.Count);
        }

        [TestMethod]
        public void Announce_SendsOneChatLine_ThroughSay_NeverNamingAnyone_AndOnlyAfterSuccess()
        {
            var backend = new FakeCareerBackend();
            var handler = new AdminApiHandler(backend, Token);

            Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":100000,\"announce\":true,\"note\":\"Jeb asked\"}"));
            Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":-2500.5,\"announce\":true}"));
            Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"set\",\"amount\":300,\"announce\":true}"));
            Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"add\",\"amount\":5,\"announce\":false}"));

            CollectionAssert.AreEqual(new[]
            {
                "The host added 100,000 funds.",
                "The host removed 2,500.5 funds.",
                "The host set science to 300.",
            }, backend.Said);

            backend.Result = CareerChangeResult.Refused(CareerChangeStatus.OutOfBounds, "no");
            Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"add\",\"amount\":-9,\"announce\":true}"));
            Assert.AreEqual(3, backend.Said.Count, "a refused change announces nothing");

            // A chat failure never turns a made change into an error.
            backend.Result = new CareerChangeResult { Status = CareerChangeStatus.Changed, Previous = 1, Current = 2 };
            backend.SayThrows = true;
            Exception reported = null;
            var failing = new AdminApiHandler(backend, Token, e => reported = e);
            Assert.AreEqual(200, Send(failing, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":1,\"announce\":true}")).StatusCode);
            Assert.IsNotNull(reported);
        }

        // ---- the real backend against a real registry ----

        private GameMode _savedMode;

        [TestInitialize]
        public void Setup()
        {
            _savedMode = GeneralSettings.SettingsStore.GameMode;
            PersistentSyncRegistry.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
        }

        [TestCleanup]
        public void Teardown()
        {
            PersistentSyncRegistry.Reset();
            ScenarioStoreSystem.CurrentScenarios.Clear();
            GeneralSettings.SettingsStore.GameMode = _savedMode;
        }

        private static void StartRegistry(GameMode mode, string funds, string science)
        {
            GeneralSettings.SettingsStore.GameMode = mode;
            ScenarioStoreSystem.CurrentScenarios["Funding"] = new ConfigNode("funds = " + funds);
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = new ConfigNode("sci = " + science);
            PersistentSyncRegistry.Initialize(false);
        }

        private static (double Value, long Revision) Funds()
        {
            var snapshot = PersistentSyncRegistry.GetSnapshots(new[] { PersistentSyncDomainId.Funds }).Single();
            return (FundsSnapshotPayloadSerializer.Deserialize(snapshot.Payload, snapshot.NumBytes), snapshot.Revision);
        }

        private static (float Value, long Revision) Science()
        {
            var snapshot = PersistentSyncRegistry.GetSnapshots(new[] { PersistentSyncDomainId.Science }).Single();
            return (ScienceSnapshotPayloadSerializer.Deserialize(snapshot.Payload, snapshot.NumBytes), snapshot.Revision);
        }

        [TestMethod]
        public void RealBackend_AddAndSet_ChangeTheValueAndTheSnapshotRevision_AndAreComputedFromTheCurrentValue()
        {
            StartRegistry(GameMode.Career, "25000", "7.5");
            var backend = new ServerAdminApiBackend();
            var lines = new List<string>();
            var handler = new AdminApiHandler(backend, Token, null, null, null, null, lines.Add);

            Assert.AreEqual("{\"gameMode\":\"Career\",\"funds\":{\"available\":true,\"value\":25000},\"science\":{\"available\":true,\"value\":7.5}}", Send(handler, Req("GET", "/api/v1/career")).Json);

            var add = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":100000}"));
            Assert.AreEqual("{\"ok\":true,\"currency\":\"funds\",\"previous\":25000,\"current\":125000}", add.Json);
            Assert.AreEqual((125000d, 1L), Funds());

            // A change made behind the API's back (a player's game, a console command) is what the next add starts from.
            var behind = FundsIntentPayloadSerializer.Serialize(1000, "Contract");
            PersistentSyncRegistry.ApplyServerMutation(PersistentSyncDomainId.Funds, behind, behind.Length, "Contract");
            var remove = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":-250.25}"));
            Assert.AreEqual("{\"ok\":true,\"currency\":\"funds\",\"previous\":1000,\"current\":749.75}", remove.Json);
            Assert.AreEqual((749.75d, 3L), Funds());

            var set = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":-5000}"));
            Assert.AreEqual("{\"ok\":true,\"currency\":\"funds\",\"previous\":749.75,\"current\":-5000}", set.Json);
            Assert.AreEqual((-5000d, 4L), Funds());

            var science = Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"add\",\"amount\":0.1}"));
            Assert.AreEqual("{\"ok\":true,\"currency\":\"science\",\"previous\":7.5,\"current\":7.6}", science.Json, "floats are reported as the short decimal, not 7.599999904632568");
            Assert.AreEqual(1L, Science().Revision);
            Assert.AreEqual(7.6f, Science().Value);

            var sameValue = Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"set\",\"amount\":7.6}"));
            Assert.AreEqual(200, sameValue.StatusCode);
            Assert.AreEqual(1L, Science().Revision, "setting the value it already has does not bump the revision");
            Assert.AreEqual(5, lines.Count, "every accepted change is logged, a no-op included");

            // The new value reaches the persisted scenario, so it survives a restart.
            Assert.AreEqual("-5000", ScenarioStoreSystem.CurrentScenarios["Funding"].GetValue("funds").Value);
            Assert.AreEqual("7.6", ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"].GetValue("sci").Value);
        }

        [TestMethod]
        public void RealBackend_RefusesWhatTheModeLacks_AndResultsOutsideTheBounds_WithoutChangingAnything()
        {
            StartRegistry(GameMode.Sandbox, "25000", "7.5");
            var handler = new AdminApiHandler(new ServerAdminApiBackend(), Token);

            Assert.AreEqual("{\"gameMode\":\"Sandbox\",\"funds\":{\"available\":false,\"value\":null},\"science\":{\"available\":false,\"value\":null}}", Send(handler, Req("GET", "/api/v1/career")).Json);
            var funds = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":1}"));
            var science = Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"set\",\"amount\":1}"));
            Assert.AreEqual(409, funds.StatusCode);
            Assert.AreEqual(409, science.StatusCode);
            StringAssert.Contains(funds.Json, "Sandbox");
            Assert.AreEqual((25000d, 0L), Funds());
            Assert.AreEqual((7.5f, 0L), Science());

            // Science mode has science but no funds.
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            Assert.AreEqual("{\"gameMode\":\"Science\",\"funds\":{\"available\":false,\"value\":null},\"science\":{\"available\":true,\"value\":7.5}}", Send(handler, Req("GET", "/api/v1/career")).Json);
            Assert.AreEqual(409, Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":1}")).StatusCode);
            Assert.AreEqual(200, Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"add\",\"amount\":2.5}")).StatusCode);
            Assert.AreEqual((10f, 1L), Science());

            // Career: rejected, not clamped.
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            var below = Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"add\",\"amount\":-10.5}"));
            Assert.AreEqual(400, below.StatusCode);
            StringAssert.Contains(below.Json, "science cannot go below 0");
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/career/science", "{\"mode\":\"set\",\"amount\":-1}")).StatusCode);
            Assert.AreEqual((10f, 1L), Science());

            Assert.AreEqual(200, Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"set\",\"amount\":1000000000000}")).StatusCode);
            var over = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":1}"));
            Assert.AreEqual(400, over.StatusCode);
            StringAssert.Contains(over.Json, "outside the allowed range");
            Assert.AreEqual((1e12, 1L), Funds());
        }

        [TestMethod]
        public void RealBackend_BeforeTheRegistryIsReady_AnswersNotReady_NotAnError()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            var handler = new AdminApiHandler(new ServerAdminApiBackend(), Token);

            var response = Send(handler, Req("POST", "/api/v1/career/funds", "{\"mode\":\"add\",\"amount\":1}"));

            Assert.AreEqual(409, response.StatusCode);
            StringAssert.Contains(response.Json, "not ready");
            Assert.AreEqual("{\"gameMode\":\"Career\",\"funds\":{\"available\":false,\"value\":null},\"science\":{\"available\":false,\"value\":null}}", Send(handler, Req("GET", "/api/v1/career")).Json);
        }
    }
}
