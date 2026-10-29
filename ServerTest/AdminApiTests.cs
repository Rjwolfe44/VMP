using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerTest
{
    [TestClass]
    public class AdminApiTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private static readonly IPAddress Local = IPAddress.Loopback;

        private sealed class FakeBackend : IAdminApiBackend
        {
            public int StatusCalls;
            public int? LogLinesAsked;
            public List<string> LogToReturn = new List<string> { "one", "two" };
            public string Said;
            public string KickedName;
            public string KickedReason;
            public bool KickResult = true;
            public bool BackupRunning;
            public int BackupCalls;

            public StatusInfo GetStatus()
            {
                StatusCalls++;
                return new StatusInfo
                {
                    Version = "9.9.9",
                    PlayersOnline = 1,
                    Machine = new MachineInfo { RamTotalBytes = 16000, RamFreeBytes = 4000, OsUptimeSeconds = 90.5, DiskFreeBytes = 500, DiskTotalBytes = 1000, DiskRoot = "C:\\" },
                };
            }

            public NetworkInfo GetNetwork() => new NetworkInfo { WindowsRecorded = 3 };

            public List<ClientInfo> GetClients() => new List<ClientInfo> { new ClientInfo { Name = "Jeb", BytesIn = 5, BytesOut = 7 } };

            public List<string> ReadLog(int lines)
            {
                LogLinesAsked = lines;
                return LogToReturn;
            }

            public ReportsInfo GetReports() => new ReportsInfo { ZipCount = 2 };

            public List<BackupFileEntry> ListBackups() => new List<BackupFileEntry> { new BackupFileEntry { Name = "Universe-20261028-120000.tar.zst", Bytes = 10, Kind = "tar.zst" } };

            public Task<BackupCreateInfo> CreateBackupAsync()
            {
                BackupCalls++;
                return Task.FromResult(BackupRunning ? null : new BackupCreateInfo { FileName = "Universe-20261028-120000.tar.zst", RawBytes = 100, CompressedBytes = 10 });
            }

            public void Say(string text) => Said = text;

            public bool Kick(string name, string reason)
            {
                KickedName = name;
                KickedReason = reason;
                return KickResult;
            }
        }

        private static AdminApiRequest Req(string method, string path, string auth = "Bearer " + Token, string query = "", string body = null, IPAddress remote = null)
        {
            return new AdminApiRequest
            {
                Method = method,
                Path = path,
                Query = query,
                Authorization = auth,
                Body = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body),
                RemoteAddress = remote ?? Local,
            };
        }

        private static AdminApiResponse Send(AdminApiHandler handler, AdminApiRequest request) => handler.HandleAsync(request).GetAwaiter().GetResult();

        private static JsonElement Parse(AdminApiResponse response) => JsonDocument.Parse(response.Json).RootElement.Clone();

        [TestMethod]
        public void Status_IncludesTheMachineBlock_WithCamelCaseFields()
        {
            var response = Send(new AdminApiHandler(new FakeBackend(), Token), Req("GET", "/api/v1/status"));

            Assert.AreEqual(200, response.StatusCode);
            var machine = Parse(response).GetProperty("machine");
            Assert.AreEqual(16000, machine.GetProperty("ramTotalBytes").GetInt64());
            Assert.AreEqual(4000, machine.GetProperty("ramFreeBytes").GetInt64());
            Assert.AreEqual(90.5, machine.GetProperty("osUptimeSeconds").GetDouble());
            Assert.AreEqual(500, machine.GetProperty("diskFreeBytes").GetInt64());
            Assert.AreEqual(1000, machine.GetProperty("diskTotalBytes").GetInt64());
            Assert.AreEqual("C:\\", machine.GetProperty("diskRoot").GetString());
        }

        [TestMethod]
        public void MachineProbe_ReadsTheRealMachine_AndNeverThrows()
        {
            var info = MachineProbe.Read(AppDomain.CurrentDomain.BaseDirectory);

            Assert.IsFalse(string.IsNullOrEmpty(info.DiskRoot));
            Assert.IsTrue(info.DiskTotalBytes > 0 && info.DiskFreeBytes >= 0 && info.DiskFreeBytes <= info.DiskTotalBytes);
            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue(info.RamTotalBytes > 0);
                Assert.IsTrue(info.RamFreeBytes >= 0 && info.RamFreeBytes <= info.RamTotalBytes);
                Assert.IsTrue(info.OsUptimeSeconds > 0);
            }
            else
            {
                Assert.IsNull(info.RamTotalBytes);
                Assert.IsNull(info.OsUptimeSeconds);
            }

            // A folder that does not exist still resolves to its drive root; an empty argument falls back to the app folder.
            Assert.IsNotNull(MachineProbe.Read(null).DiskRoot);
        }

        [TestMethod]
        public void NoToken_Is401_WithoutDetail_AndBackendIsNotTouched()
        {
            var backend = new FakeBackend();
            var response = Send(new AdminApiHandler(backend, Token), Req("GET", "/api/v1/status", auth: null));

            Assert.AreEqual(401, response.StatusCode);
            Assert.AreEqual("{\"error\":\"unauthorized\"}", response.Json);
            Assert.AreEqual(0, backend.StatusCalls);
        }

        [TestMethod]
        public void WrongOrMalformedToken_Is401_ForEveryRoute()
        {
            // The clock jumps a minute per look, so the failed-authentication lockout (tested in AdminHardeningTests) never kicks in here.
            var clock = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
            var handler = new AdminApiHandler(new FakeBackend(), Token, null, null, () => clock = clock.AddMinutes(1));
            var wrong = new[]
            {
                "Bearer " + Token.Substring(0, Token.Length - 1) + "0",
                "Bearer " + Token + "x",
                "Bearer " + Token.Substring(1),
                "Bearer ",
                "Bearer",
                "Basic " + Token,
                Token,
                string.Empty,
            };
            foreach (var auth in wrong)
            {
                foreach (var path in new[] { "/api/v1/status", "/api/v1/network", "/api/v1/clients", "/api/v1/log", "/api/v1/reports", "/api/v1/backups", "/api/v1/nope" })
                {
                    var response = Send(handler, Req("GET", path, auth));
                    Assert.AreEqual(401, response.StatusCode, "auth='" + auth + "' path=" + path);
                    Assert.AreEqual("{\"error\":\"unauthorized\"}", response.Json);
                }

                Assert.AreEqual(401, Send(handler, Req("POST", "/api/v1/backups", auth)).StatusCode);
                Assert.AreEqual(401, Send(handler, Req("POST", "/api/v1/say", auth, body: "{\"text\":\"hi\"}")).StatusCode);
                Assert.AreEqual(401, Send(handler, Req("POST", "/api/v1/kick", auth, body: "{\"name\":\"Jeb\"}")).StatusCode);
            }
        }

        [TestMethod]
        public void RightToken_ReturnsData_AndSchemeIsCaseInsensitive()
        {
            var handler = new AdminApiHandler(new FakeBackend(), Token);

            var status = Send(handler, Req("GET", "/api/v1/status"));
            Assert.AreEqual(200, status.StatusCode);
            Assert.AreEqual("9.9.9", Parse(status).GetProperty("version").GetString());

            Assert.AreEqual(200, Send(handler, Req("GET", "/api/v1/status", "bearer " + Token)).StatusCode);
            Assert.AreEqual(3, Parse(Send(handler, Req("GET", "/api/v1/network"))).GetProperty("windowsRecorded").GetInt32());
            Assert.AreEqual("Jeb", Parse(Send(handler, Req("GET", "/api/v1/clients"))).GetProperty("clients")[0].GetProperty("name").GetString());
            Assert.AreEqual(2, Parse(Send(handler, Req("GET", "/api/v1/reports"))).GetProperty("zipCount").GetInt32());
            Assert.AreEqual("tar.zst", Parse(Send(handler, Req("GET", "/api/v1/backups"))).GetProperty("backups")[0].GetProperty("kind").GetString());
            Assert.AreEqual(200, Send(handler, Req("GET", "/api/v1/status/")).StatusCode, "a trailing slash is fine");
        }

        [TestMethod]
        public void NonLoopbackPeer_IsRefused_EvenWithTheRightToken()
        {
            var backend = new FakeBackend();
            var handler = new AdminApiHandler(backend, Token);

            foreach (var address in new[] { "192.168.1.20", "10.0.0.5", "8.8.8.8", "2001:db8::1", "::ffff:10.0.0.5" })
            {
                var response = Send(handler, Req("GET", "/api/v1/status", remote: IPAddress.Parse(address)));
                Assert.AreEqual(403, response.StatusCode, address);
            }

            Assert.AreEqual(403, Send(handler, Req("GET", "/api/v1/status", remote: IPAddress.Any)).StatusCode);
            var noRemote = Req("GET", "/api/v1/status");
            noRemote.RemoteAddress = null;
            Assert.AreEqual(403, Send(handler, noRemote).StatusCode);
            Assert.AreEqual(0, backend.StatusCalls);

            foreach (var address in new[] { "127.0.0.1", "127.5.5.5", "::1", "::ffff:127.0.0.1" })
            {
                Assert.AreEqual(200, Send(handler, Req("GET", "/api/v1/status", remote: IPAddress.Parse(address))).StatusCode, address);
            }
        }

        [TestMethod]
        public void UnknownRoute_Is404Json_AndWrongVerb_Is405()
        {
            var handler = new AdminApiHandler(new FakeBackend(), Token);

            foreach (var path in new[] { "/api/v1/restart", "/api/v1/settings", "/api/v1/files", "/api/v1/", "/api/v1", "/api/v2/status", "/status", "/" })
            {
                var response = Send(handler, Req("GET", path));
                Assert.AreEqual(404, response.StatusCode, path);
                Assert.AreEqual("not found", Parse(response).GetProperty("error").GetString());
            }

            Assert.AreEqual(405, Send(handler, Req("POST", "/api/v1/status")).StatusCode);
            Assert.AreEqual(405, Send(handler, Req("GET", "/api/v1/say")).StatusCode);
            Assert.AreEqual(405, Send(handler, Req("DELETE", "/api/v1/backups")).StatusCode);
            Assert.AreEqual(404, Send(handler, Req("POST", "/api/v1/stop")).StatusCode);
        }

        [TestMethod]
        public void LogLines_AreCappedAt500_AndValidated()
        {
            var backend = new FakeBackend();
            var handler = new AdminApiHandler(backend, Token);

            Send(handler, Req("GET", "/api/v1/log"));
            Assert.AreEqual(100, backend.LogLinesAsked);

            Send(handler, Req("GET", "/api/v1/log", query: "lines=25"));
            Assert.AreEqual(25, backend.LogLinesAsked);

            Send(handler, Req("GET", "/api/v1/log", query: "lines=100000"));
            Assert.AreEqual(500, backend.LogLinesAsked);

            Send(handler, Req("GET", "/api/v1/log", query: "x=1&lines=501"));
            Assert.AreEqual(500, backend.LogLinesAsked);

            foreach (var bad in new[] { "lines=0", "lines=-5", "lines=abc", "lines=", "lines=1e3", "lines=99999999999999999999" })
            {
                backend.LogLinesAsked = null;
                Assert.AreEqual(400, Send(handler, Req("GET", "/api/v1/log", query: bad)).StatusCode, bad);
                Assert.IsNull(backend.LogLinesAsked, bad);
            }
        }

        [TestMethod]
        public void LogLines_StripControlCharacters_AndNeverExceedTheRequestedCount()
        {
            var backend = new FakeBackend();
            backend.LogToReturn = new List<string> { "old", "esc\u001b[31mred\u0007\r", "tab\there", "c1\u0085\u009bx", "last" };
            var handler = new AdminApiHandler(backend, Token);

            var response = Send(handler, Req("GET", "/api/v1/log", query: "lines=3"));
            var lines = Parse(response).GetProperty("lines").EnumerateArray().Select(e => e.GetString()).ToList();

            Assert.AreEqual(3, lines.Count, "a backend that returns too much is cut to the requested count");
            Assert.AreEqual("tab here", lines[0]);
            Assert.AreEqual("c1  x", lines[1]);
            Assert.AreEqual("last", lines[2]);
            Assert.IsFalse(response.Json.Any(c => c < 0x20), "no raw control characters in the JSON");

            var all = Send(handler, Req("GET", "/api/v1/log", query: "lines=500"));
            Assert.IsTrue(Parse(all).GetProperty("lines")[1].GetString().StartsWith("esc [31mred", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BodyOver4KB_Is413_AndNothingRuns()
        {
            var backend = new FakeBackend();
            var handler = new AdminApiHandler(backend, Token);

            var tooBig = new AdminApiRequest
            {
                Method = "POST",
                Path = "/api/v1/say",
                Authorization = "Bearer " + Token,
                Body = new byte[AdminApiHandler.MaxBodyBytes + 1],
                RemoteAddress = Local,
            };
            Assert.AreEqual(413, Send(handler, tooBig).StatusCode);
            Assert.IsNull(backend.Said);

            tooBig.Path = "/api/v1/kick";
            Assert.AreEqual(413, Send(handler, tooBig).StatusCode);
            Assert.IsNull(backend.KickedName);

            // Exactly at the limit is accepted by the size check (the content is then rejected as invalid JSON, not as too big).
            var atLimit = new AdminApiRequest
            {
                Method = "POST",
                Path = "/api/v1/say",
                Authorization = "Bearer " + Token,
                Body = new byte[AdminApiHandler.MaxBodyBytes],
                RemoteAddress = Local,
            };
            Assert.AreEqual(400, Send(handler, atLimit).StatusCode);
        }

        [TestMethod]
        public void Say_RoutesToTheBackend_WithCleanText()
        {
            var backend = new FakeBackend();
            var handler = new AdminApiHandler(backend, Token);

            var ok = Send(handler, Req("POST", "/api/v1/say", body: "{\"text\":\"  Server restarts in 5 minutes\\nplease land \"}"));
            Assert.AreEqual(200, ok.StatusCode);
            Assert.AreEqual("Server restarts in 5 minutes please land", backend.Said);

            backend.Said = null;
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/say", body: "{\"text\":\"   \"}")).StatusCode);
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/say", body: "{}")).StatusCode);
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/say", body: "not json")).StatusCode);
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/say")).StatusCode);
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/say", body: "{\"text\":\"" + new string('x', AdminApiHandler.MaxSayChars + 1) + "\"}")).StatusCode);
            Assert.IsNull(backend.Said);
        }

        [TestMethod]
        public void Kick_RoutesToTheBackend_AndReportsUnknownPlayers()
        {
            var backend = new FakeBackend();
            var handler = new AdminApiHandler(backend, Token);

            Assert.AreEqual(200, Send(handler, Req("POST", "/api/v1/kick", body: "{\"name\":\"Jeb\",\"reason\":\"AFK in the VAB\"}")).StatusCode);
            Assert.AreEqual("Jeb", backend.KickedName);
            Assert.AreEqual("AFK in the VAB", backend.KickedReason);

            backend.KickResult = false;
            var missing = Send(handler, Req("POST", "/api/v1/kick", body: "{\"name\":\"Nobody\"}"));
            Assert.AreEqual(404, missing.StatusCode);
            Assert.AreEqual("Nobody", backend.KickedName);
            Assert.AreEqual(string.Empty, backend.KickedReason);

            backend.KickedName = null;
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/kick", body: "{\"reason\":\"x\"}")).StatusCode);
            Assert.AreEqual(400, Send(handler, Req("POST", "/api/v1/kick", body: "{\"name\":\"" + new string('n', AdminApiHandler.MaxNameChars + 1) + "\"}")).StatusCode);
            Assert.IsNull(backend.KickedName);
        }

        [TestMethod]
        public void Backups_PostIsRefusedWhileOneRuns_AndErrorsKeepTheirStatus()
        {
            var backend = new FakeBackend();
            var handler = new AdminApiHandler(backend, Token);

            var ok = Send(handler, Req("POST", "/api/v1/backups"));
            Assert.AreEqual(200, ok.StatusCode);
            Assert.AreEqual("Universe-20261028-120000.tar.zst", Parse(ok).GetProperty("fileName").GetString());

            backend.BackupRunning = true;
            Assert.AreEqual(409, Send(handler, Req("POST", "/api/v1/backups")).StatusCode);
            Assert.AreEqual(2, backend.BackupCalls);

            string logged = null;
            var failing = new AdminApiHandler(new ThrowingBackend(), Token, e => logged = e.Message);
            var failure = Send(failing, Req("GET", "/api/v1/status"));
            Assert.AreEqual(500, failure.StatusCode);
            Assert.AreEqual("{\"error\":\"internal error\"}", failure.Json, "no exception detail leaks to the caller");
            Assert.AreEqual("secret path C:\\x", logged);
        }

        private sealed class ThrowingBackend : IAdminApiBackend
        {
            public StatusInfo GetStatus() => throw new InvalidOperationException("secret path C:\\x");
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
        public void Token_IsCreatedOnce_AsHex_AndShortFilesAreRefused()
        {
            var folder = Path.Combine(Path.GetTempPath(), "vmp-admin-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var first = AdminApiToken.LoadOrCreate(folder, out var created);
                Assert.IsTrue(created);
                Assert.AreEqual(64, first.Length);
                Assert.IsTrue(first.All(c => Uri.IsHexDigit(c) && !char.IsUpper(c)));
                Assert.AreEqual(first, File.ReadAllText(Path.Combine(folder, "admin-token.txt")));

                var second = AdminApiToken.LoadOrCreate(folder, out created);
                Assert.IsFalse(created, "an existing token is never replaced");
                Assert.AreEqual(first, second);

                File.WriteAllText(Path.Combine(folder, "admin-token.txt"), "short");
                Assert.ThrowsException<InvalidDataException>(() => AdminApiToken.LoadOrCreate(folder, out created));
                Assert.AreEqual("short", File.ReadAllText(Path.Combine(folder, "admin-token.txt")), "the bad file is left alone");

                var other = Path.Combine(folder, "second");
                Assert.AreNotEqual(first, AdminApiToken.LoadOrCreate(other, out created));
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }

        [TestMethod]
        public void LogTail_ReadsTheEndOfAGrowingFile()
        {
            var folder = Path.Combine(Path.GetTempPath(), "vmp-admin-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "log.txt");
                Assert.AreEqual(0, AdminLogTail.ReadTail(path, 10).Count, "a missing file is just empty");

                File.WriteAllText(path, string.Join("\r\n", Enumerable.Range(1, 1000).Select(i => "line " + i)) + "\r\n");
                var tail = AdminLogTail.ReadTail(path, 3);
                CollectionAssert.AreEqual(new[] { "line 998", "line 999", "line 1000" }, tail);

                // Held open for writing by the server: still readable.
                using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    var bytes = Encoding.UTF8.GetBytes("line 1001\n");
                    writer.Write(bytes, 0, bytes.Length);
                    writer.Flush();
                    CollectionAssert.AreEqual(new[] { "line 1000", "line 1001" }, AdminLogTail.ReadTail(path, 2));

                    // Only the last few bytes are looked at; a cut-off first line is dropped, never shown as a bogus line.
                    var small = AdminLogTail.ReadTail(path, 500, 40);
                    Assert.IsTrue(small.Count >= 2 && small.Count <= 5);
                    Assert.IsTrue(small.All(l => l.StartsWith("line ", StringComparison.Ordinal) && l.Length >= 6));
                    Assert.AreEqual("line 1001", small[small.Count - 1]);
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }

        [TestMethod]
        public void CpuMeter_ReportsShareOfTheWholeMachine()
        {
            var start = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
            var meter = new CpuMeter(4, TimeSpan.Zero, start);

            Assert.AreEqual(0, meter.Sample(TimeSpan.FromSeconds(0.3), start.AddMilliseconds(500)), "too soon, keeps the previous figure");
            Assert.AreEqual(12.5, meter.Sample(TimeSpan.FromSeconds(5), start.AddSeconds(10)), 0.001);
            Assert.AreEqual(50, meter.Sample(TimeSpan.FromSeconds(25), start.AddSeconds(20)), 0.001);
            Assert.AreEqual(0, meter.Sample(TimeSpan.FromSeconds(25), start.AddSeconds(30)), 0.001);
        }

        [TestMethod]
        public async Task RealSocket_ServesTheApi_OnLoopbackOnly()
        {
            var backend = new FakeBackend();
            using (var server = new AdminApiServer(new AdminApiHandler(backend, Token)))
            {
                server.Start(0);
                Assert.IsTrue(server.Port > 0);

                var ok = await Raw(server.Port, "GET /api/v1/status HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer " + Token + "\r\n\r\n");
                StringAssert.StartsWith(ok, "HTTP/1.1 200 OK");
                StringAssert.Contains(ok, "\"version\":\"9.9.9\"");
                Assert.IsFalse(ok.Contains("Access-Control-"), "no CORS headers");

                var denied = await Raw(server.Port, "GET /api/v1/status HTTP/1.1\r\nHost: localhost\r\n\r\n");
                StringAssert.StartsWith(denied, "HTTP/1.1 401");
                StringAssert.EndsWith(denied, "{\"error\":\"unauthorized\"}");

                var post = "{\"text\":\"hello\"}";
                var said = await Raw(server.Port, "POST /api/v1/say HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer " + Token + "\r\nContent-Length: " + post.Length + "\r\n\r\n" + post);
                StringAssert.StartsWith(said, "HTTP/1.1 200");
                Assert.AreEqual("hello", backend.Said);

                // A body that claims to be huge is refused from the header alone, before any of it is read.
                var huge = await Raw(server.Port, "POST /api/v1/say HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer " + Token + "\r\nContent-Length: 100000\r\n\r\n");
                StringAssert.StartsWith(huge, "HTTP/1.1 413");

                var chunked = await Raw(server.Port, "POST /api/v1/say HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer " + Token + "\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
                StringAssert.StartsWith(chunked, "HTTP/1.1 400");

                var garbage = await Raw(server.Port, "this is not http\r\n\r\n");
                StringAssert.StartsWith(garbage, "HTTP/1.1 400");

                // The server answers 431 and closes with part of the request unread, so the OS may reset the connection before the client
                // reads the answer; either outcome is a refusal.
                string bigHeader;
                try
                {
                    bigHeader = await Raw(server.Port, "GET /api/v1/status HTTP/1.1\r\nX-Pad: " + new string('a', 9000) + "\r\n\r\n");
                }
                catch (IOException)
                {
                    bigHeader = "HTTP/1.1 431 (connection reset)";
                }

                StringAssert.StartsWith(bigHeader, "HTTP/1.1 431");

                // Bound to 127.0.0.1 only: this machine's own LAN address must not connect.
                var lan = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (lan != null)
                {
                    using (var client = new TcpClient())
                    using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    {
                        try
                        {
                            await client.ConnectAsync(lan, server.Port, timeout.Token);
                            Assert.Fail("the admin API must not be reachable on " + lan);
                        }
                        catch (SocketException)
                        {
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                }
            }
        }

        private static async Task<string> Raw(int port, string request)
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var bytes = Encoding.ASCII.GetBytes(request);
                await stream.WriteAsync(bytes, 0, bytes.Length);

                var text = new StringBuilder();
                var buffer = new byte[4096];
                using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    int n;
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                    {
                        text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    }
                }

                return text.ToString();
            }
        }
    }
}
