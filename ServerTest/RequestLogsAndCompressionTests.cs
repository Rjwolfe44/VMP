using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using Server.System;
using Server.System.LogUpload;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerTest
{
    /// <summary>The host-requested log report entry points (admin API, shared policy) and the message compression statistics.</summary>
    [TestClass]
    public class RequestLogsAndCompressionTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private static readonly DateTime T0 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        private sealed class RequestBackend : IAdminApiBackend
        {
            public int Calls;
            public string Name;
            public string Note;
            public LogRequestResult Result = new LogRequestResult { Status = LogRequestStatus.Sent, PlayerName = "Jeb" };

            public StatusInfo GetStatus() => new StatusInfo();
            public NetworkInfo GetNetwork() => new NetworkInfo();
            public List<ClientInfo> GetClients() => new List<ClientInfo>();
            public List<string> ReadLog(int lines) => new List<string>();
            public ReportsInfo GetReports() => new ReportsInfo();
            public List<BackupFileEntry> ListBackups() => new List<BackupFileEntry>();
            public Task<BackupCreateInfo> CreateBackupAsync() => Task.FromResult<BackupCreateInfo>(null);
            public void Say(string text) { }
            public bool Kick(string name, string reason) => false;

            public LogRequestResult RequestLogs(string name, string note)
            {
                Calls++;
                Name = name;
                Note = note;
                return Result;
            }
        }

        private static AdminApiResponse Post(RequestBackend backend, string body, string method = "POST", string auth = "Bearer " + Token)
        {
            var handler = new AdminApiHandler(backend, Token);
            var request = new AdminApiRequest
            {
                Method = method,
                Path = "/api/v1/clients/requestlogs",
                Authorization = auth,
                Body = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body),
                RemoteAddress = IPAddress.Loopback,
            };
            return handler.HandleAsync(request).GetAwaiter().GetResult();
        }

        private static JsonElement Json(AdminApiResponse response) => JsonDocument.Parse(response.Json).RootElement.Clone();

        #region Admin API: POST /api/v1/clients/requestlogs

        [TestMethod]
        public void RequestLogs_Succeeds_AndPassesNameAndNoteToTheBackend()
        {
            var backend = new RequestBackend();
            var response = Post(backend, "{\"name\":\"Jeb\",\"note\":\"after the crash\"}");

            Assert.AreEqual(200, response.StatusCode, response.Json);
            var json = Json(response);
            Assert.IsTrue(json.GetProperty("requested").GetBoolean());
            Assert.AreEqual("Jeb", json.GetProperty("player").GetString());
            Assert.AreEqual("Jeb", backend.Name);
            Assert.AreEqual("after the crash", backend.Note);
        }

        [TestMethod]
        public void RequestLogs_NoteIsOptional_AndControlCharactersBecomeSpaces()
        {
            var backend = new RequestBackend();
            Assert.AreEqual(200, Post(backend, "{\"name\":\"Jeb\"}").StatusCode);
            Assert.AreEqual(string.Empty, backend.Note);

            Assert.AreEqual(200, Post(backend, "{\"name\":\"Jeb\",\"note\":null}").StatusCode);
            Assert.AreEqual(string.Empty, backend.Note);

            Assert.AreEqual(200, Post(backend, "{\"name\":\"Jeb\",\"note\":\"a\\u0007b\\nc\"}").StatusCode);
            Assert.AreEqual("a b c", backend.Note);
        }

        [TestMethod]
        public void RequestLogs_UnknownPlayer_Is404()
        {
            var backend = new RequestBackend { Result = LogRequestResult.NotFound("Ghost") };
            var response = Post(backend, "{\"name\":\"Ghost\"}");
            Assert.AreEqual(404, response.StatusCode);
            StringAssert.Contains(Json(response).GetProperty("error").GetString(), "Ghost");
        }

        [TestMethod]
        public void RequestLogs_ClientOfAnotherBuild_Is409()
        {
            var backend = new RequestBackend { Result = new LogRequestResult { Status = LogRequestStatus.OldClient, PlayerName = "Jeb" } };
            var response = Post(backend, "{\"name\":\"Jeb\"}");
            Assert.AreEqual(409, response.StatusCode);
            StringAssert.Contains(Json(response).GetProperty("error").GetString(), "not the same build");
        }

        [TestMethod]
        public void RequestLogs_AskedTooSoon_Is429WithTheWaitInTheText()
        {
            var backend = new RequestBackend { Result = new LogRequestResult { Status = LogRequestStatus.TooSoon, PlayerName = "Jeb", RetryAfterSeconds = 42 } };
            var response = Post(backend, "{\"name\":\"Jeb\"}");
            Assert.AreEqual(429, response.StatusCode);
            StringAssert.Contains(Json(response).GetProperty("error").GetString(), "42 s");
        }

        [TestMethod]
        public void RequestLogs_RejectsBadRequestsBeforeTheBackendIsAsked()
        {
            var backend = new RequestBackend();
            Assert.AreEqual(400, Post(backend, null).StatusCode, "no body");
            Assert.AreEqual(400, Post(backend, "{nope").StatusCode, "invalid JSON");
            Assert.AreEqual(400, Post(backend, "{}").StatusCode, "no name");
            Assert.AreEqual(400, Post(backend, "{\"name\":\"   \"}").StatusCode, "blank name");
            Assert.AreEqual(400, Post(backend, "{\"name\":\"" + new string('n', AdminApiHandler.MaxNameChars + 1) + "\"}").StatusCode, "long name");
            Assert.AreEqual(400, Post(backend, "{\"name\":\"Jeb\",\"note\":\"" + new string('x', 121) + "\"}").StatusCode, "121 character note");
            Assert.AreEqual(0, backend.Calls);

            Assert.AreEqual(200, Post(backend, "{\"name\":\"Jeb\",\"note\":\"" + new string('x', 120) + "\"}").StatusCode, "120 characters is allowed");
            Assert.AreEqual(1, backend.Calls);
        }

        [TestMethod]
        public void RequestLogs_NeedsPost_AndTheToken()
        {
            var backend = new RequestBackend();
            Assert.AreEqual(405, Post(backend, "{\"name\":\"Jeb\"}", method: "GET").StatusCode);
            Assert.AreEqual(405, Post(backend, "{\"name\":\"Jeb\"}", method: "PUT").StatusCode);
            Assert.AreEqual(401, Post(backend, "{\"name\":\"Jeb\"}", auth: "Bearer wrong").StatusCode);
            Assert.AreEqual(401, Post(backend, "{\"name\":\"Jeb\"}", auth: null).StatusCode);
            Assert.AreEqual(0, backend.Calls);
        }

        [TestMethod]
        public void BackendsThatDoNotSupportTheCallAnswerNotFound()
        {
            // The default implementation on the interface (older fakes keep compiling and nothing is sent anywhere).
            IAdminApiBackend backend = new MinimalBackend();
            Assert.AreEqual(LogRequestStatus.NotFound, backend.RequestLogs("Jeb", "").Status);
        }

        private sealed class MinimalBackend : IAdminApiBackend
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

        #endregion

        #region Shared policy used by the console command and the API

        private LogUploadAssembler NewAssembler()
        {
            var now = T0;
            return new LogUploadAssembler(Path.Combine(Path.GetTempPath(), "vmp-requestlogs-tests", Guid.NewGuid().ToString("N")), () => now);
        }

        [TestMethod]
        public void Policy_PlayerNotConnected_IsNotFound_AndNothingIsRegistered()
        {
            var assembler = NewAssembler();
            var result = LogUploadSystem.EvaluateRequest(assembler, "Jeb", false, "build-a", "build-a");
            Assert.AreEqual(LogRequestStatus.NotFound, result.Status);
            Assert.IsFalse(result.Sent);
            Assert.IsFalse(assembler.HasPendingRequest("Jeb"));
            StringAssert.Contains(result.Describe(), "not found");
        }

        [DataTestMethod]
        [DataRow("build-b")]
        [DataRow("")]
        [DataRow(null)]
        public void Policy_ClientOfAnotherBuild_IsNeverAsked(string clientBuild)
        {
            var assembler = NewAssembler();
            var result = LogUploadSystem.EvaluateRequest(assembler, "Jeb", true, clientBuild, "build-a");
            Assert.AreEqual(LogRequestStatus.OldClient, result.Status);
            Assert.IsFalse(assembler.HasPendingRequest("Jeb"), "no pending request, so a message that cannot be sent cannot be answered either");
        }

        [TestMethod]
        public void Policy_SameBuild_RegistersTheRequest_AndASecondOneWithinAMinuteIsTooSoon()
        {
            var assembler = NewAssembler();
            var first = LogUploadSystem.EvaluateRequest(assembler, "Jeb", true, "build-a", "build-a");
            Assert.AreEqual(LogRequestStatus.Sent, first.Status);
            Assert.IsTrue(first.Sent);
            Assert.IsTrue(assembler.HasPendingRequest("Jeb"));
            StringAssert.Contains(first.Describe(), "Asked Jeb");

            var second = LogUploadSystem.EvaluateRequest(assembler, "Jeb", true, "build-a", "build-a");
            Assert.AreEqual(LogRequestStatus.TooSoon, second.Status);
            Assert.AreEqual(60, second.RetryAfterSeconds);
            StringAssert.Contains(second.Describe(), "60 s");

            Assert.AreEqual(LogRequestStatus.Sent, LogUploadSystem.EvaluateRequest(assembler, "Bob", true, "build-a", "build-a").Status, "another player is not limited");
        }

        [TestMethod]
        public void WholeSeconds_RoundsUp_AndNeverReturnsZero()
        {
            Assert.AreEqual(1, LogRequestResult.WholeSeconds(TimeSpan.Zero));
            Assert.AreEqual(1, LogRequestResult.WholeSeconds(TimeSpan.FromMilliseconds(1)));
            Assert.AreEqual(2, LogRequestResult.WholeSeconds(TimeSpan.FromSeconds(1.2)));
            Assert.AreEqual(60, LogRequestResult.WholeSeconds(TimeSpan.FromSeconds(60)));
        }

        #endregion

        #region Message compression statistics

        private static TrafficSample CompressionSample(int minute, params object[] rows)
        {
            var sample = new TrafficSample { TimeUtc = T0.AddMinutes(minute) };
            for (var i = 0; i < rows.Length; i += 2)
            {
                sample.MessageCompression[(string)rows[i]] = (long[])rows[i + 1];
            }

            return sample;
        }

        [TestMethod]
        public void Compression_WindowIsTheDifferenceBetweenTwoSamples_BiggestRawFirst()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(CompressionSample(0, "VesselProtoMsgData", new long[] { 10, 100000, 20000, 2 }));
            ring.Add(CompressionSample(1,
                "VesselProtoMsgData", new long[] { 14, 180000, 36000, 2 },
                "KerbalInfo", new long[] { 3, 9000, 3000, 2 }));

            var window = ring.Latest();
            Assert.AreEqual(2, window.MessageCompression.Count);
            var vessel = window.MessageCompression[0];
            Assert.AreEqual("VesselProtoMsgData", vessel.Type, "biggest raw size first");
            Assert.AreEqual(4, vessel.Messages);
            Assert.AreEqual(80000, vessel.RawBytes);
            Assert.AreEqual(16000, vessel.WireBytes);
            Assert.AreEqual(5.0, vessel.Ratio, 0.0001);
            Assert.AreEqual("quicklz", vessel.Codec);

            var kerbal = window.MessageCompression[1];
            Assert.AreEqual("KerbalInfo", kerbal.Type, "a type that was not in the first sample counts from zero");
            Assert.AreEqual(3, kerbal.Messages);
            Assert.AreEqual(3.0, kerbal.Ratio, 0.0001);
        }

        [TestMethod]
        public void Compression_IdleTypesAndCountersThatGoBackwardsAddNoRows()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(CompressionSample(0, "A", new long[] { 5, 500, 100, 2 }, "B", new long[] { 9, 900, 300, 2 }));
            ring.Add(CompressionSample(1, "A", new long[] { 5, 500, 100, 2 }, "B", new long[] { 2, 50, 20, 2 }));
            Assert.AreEqual(0, ring.Latest().MessageCompression.Count);
        }

        [TestMethod]
        public void Compression_RatioIsZeroWhenNothingWasSentOnTheWire()
        {
            Assert.AreEqual(0.0, new VmpTrafficStats.CompressionRow { RawBytes = 100, WireBytes = 0 }.Ratio);
            Assert.AreEqual(2.0, new VmpTrafficStats.CompressionRow { RawBytes = 100, WireBytes = 50 }.Ratio, 0.0001);
        }

        [TestMethod]
        public void Compression_WindowsRollOverAndCombineAddsTheRemainingOnes()
        {
            var ring = new TrafficWindowRing(3);
            ring.Add(CompressionSample(0));
            for (var minute = 1; minute <= 5; minute++)
            {
                // 1 message of 1000 raw and 250 wire bytes in each minute.
                ring.Add(CompressionSample(minute, "ScenarioInfo", new long[] { minute, minute * 1000L, minute * 250L, 2 }));
            }

            Assert.AreEqual(3, ring.Count, "minutes 1 and 2 rolled out");
            var all = ring.Combine(10);
            var row = all.MessageCompression.Single();
            Assert.AreEqual(3, row.Messages);
            Assert.AreEqual(3000, row.RawBytes);
            Assert.AreEqual(750, row.WireBytes);
            Assert.AreEqual(4.0, row.Ratio, 0.0001);

            var latest = ring.Combine(1).MessageCompression.Single();
            Assert.AreEqual(1, latest.Messages, "only the newest window");
            Assert.AreEqual(1000, latest.RawBytes);
        }

        [TestMethod]
        public void Compression_CombineMergesTypesAcrossWindows()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(CompressionSample(0));
            ring.Add(CompressionSample(1, "A", new long[] { 1, 400, 100, 2 }, "B", new long[] { 2, 6000, 2000, 2 }));
            ring.Add(CompressionSample(2, "A", new long[] { 4, 1600, 400, 2 }, "B", new long[] { 2, 6000, 2000, 2 }));

            var sum = ring.Combine(10).MessageCompression;
            CollectionAssert.AreEqual(new[] { "B", "A" }, sum.Select(r => r.Type).ToArray());
            Assert.AreEqual(6000, sum[0].RawBytes);
            Assert.AreEqual(4, sum[1].Messages);
            Assert.AreEqual(1600, sum[1].RawBytes);
        }

        [TestMethod]
        public void Compression_StaticCounters_SumRawAndWireAndIgnoreBadInput()
        {
            var type = "CompressionTest" + Guid.NewGuid().ToString("N");
            VmpTrafficStats.RecordMessageCompression(type, SnapshotEncodings.QuickLz, 1000, 250);
            VmpTrafficStats.RecordMessageCompression(type, SnapshotEncodings.QuickLz, 3000, 750);
            VmpTrafficStats.RecordMessageCompression(null, SnapshotEncodings.QuickLz, 1, 1);
            VmpTrafficStats.RecordMessageCompression(string.Empty, SnapshotEncodings.QuickLz, 1, 1);
            VmpTrafficStats.RecordMessageCompression(type, SnapshotEncodings.QuickLz, -5, 10);
            VmpTrafficStats.RecordMessageCompression(type, SnapshotEncodings.QuickLz, 10, -5);

            var row = VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == type);
            Assert.AreEqual(2, row.Messages);
            Assert.AreEqual(4000, row.RawBytes);
            Assert.AreEqual(1000, row.WireBytes);
            Assert.AreEqual(4.0, row.Ratio, 0.0001);
            Assert.AreEqual("quicklz", row.Codec);

            CollectionAssert.AreEqual(new long[] { 2, 4000, 1000, 1L << SnapshotEncodings.QuickLz, 0, 0 }, VmpTrafficStats.CaptureSample(DateTime.UtcNow).MessageCompression[type]);

            var report = VmpTrafficStats.GetReport(DateTime.UtcNow);
            Assert.AreEqual(4000, report.SinceStart.MessageCompression.Single(r => r.Type == type).RawBytes);
        }

        [TestMethod]
        public void Compression_CountersAreExactUnderConcurrentUse()
        {
            var type = "CompressionParallel" + Guid.NewGuid().ToString("N");
            Parallel.For(0, 8, _ =>
            {
                for (var i = 0; i < 5000; i++) VmpTrafficStats.RecordMessageCompression(type, SnapshotEncodings.QuickLz, 100, 25);
            });

            var row = VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == type);
            Assert.AreEqual(40000, row.Messages);
            Assert.AreEqual(4000000, row.RawBytes);
            Assert.AreEqual(1000000, row.WireBytes);
        }

        [TestMethod]
        public void Compression_IsRecordedByTheSharedQuickLzHelpers_OnlyWhenThePayloadReallyIsCompressed()
        {
            var type = "CompressionCommon" + Guid.NewGuid().ToString("N");
            var gate = new object();
            var data = new byte[20000];
            for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 7);
            var length = data.Length;

            LmpCommon.Common.ThreadSafeCompress(gate, ref data, ref length, type);
            Assert.IsTrue(length < 20000, "the repeating pattern compresses");
            var compressedLength = length;
            var row = VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == type);
            Assert.AreEqual(1, row.Messages);
            Assert.AreEqual(20000, row.RawBytes);
            Assert.AreEqual(compressedLength, row.WireBytes);

            // Compressing something that already is compressed does nothing and counts nothing.
            var again = length;
            LmpCommon.Common.ThreadSafeCompress(gate, ref data, ref again, type);
            Assert.AreEqual(1, VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == type).Messages);

            // The receiving side counts the same payload once, with the sizes the other way round.
            LmpCommon.Common.ThreadSafeDecompress(gate, ref data, compressedLength, out var rawLength, type);
            Assert.AreEqual(20000, rawLength);
            row = VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == type);
            Assert.AreEqual(2, row.Messages);
            Assert.AreEqual(40000, row.RawBytes);
            Assert.AreEqual(2L * compressedLength, row.WireBytes);

            // A payload that was never compressed is passed through and not counted.
            LmpCommon.Common.ThreadSafeDecompress(gate, ref data, rawLength, out var same, type);
            Assert.AreEqual(rawLength, same);
            Assert.AreEqual(2, VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == type).Messages);

            // Without a name nothing is recorded at all.
            var unnamed = new byte[20000];
            for (var i = 0; i < unnamed.Length; i++) unnamed[i] = (byte)(i % 5);
            var unnamedLength = unnamed.Length;
            var before = VmpTrafficStats.GetMessageCompressionTotals().Sum(r => r.Messages);
            LmpCommon.Common.ThreadSafeCompress(gate, ref unnamed, ref unnamedLength);
            Assert.AreEqual(before, VmpTrafficStats.GetMessageCompressionTotals().Sum(r => r.Messages));
        }

        [TestMethod]
        public void NetworkJson_HasMessageCompressionAtTopLevelAndInEveryWindow()
        {
            var first = CompressionSample(0);
            var second = CompressionSample(1, "VesselProtoMsgData", new long[] { 12, 600000, 120000, 1L << SnapshotEncodings.Zstd, 108, 12 });
            var ring = new TrafficWindowRing(10);
            ring.Add(first);
            ring.Add(second);
            var report = new TrafficReport
            {
                NowUtc = T0.AddMinutes(1),
                StartedUtc = T0,
                WindowCount = ring.Count,
                LastMinute = ring.Latest(),
                LastTenMinutes = ring.Combine(10),
                SinceStart = TrafficWindowRing.Delta(first, second),
            };

            var json = Json(AdminApiResponse.Ok(AdminApiMapper.ToNetwork(report)));
            var top = json.GetProperty("messageCompression");
            Assert.AreEqual(1, top.GetArrayLength());
            var row = top[0];
            Assert.AreEqual("VesselProtoMsgData", row.GetProperty("type").GetString());
            Assert.AreEqual(12, row.GetProperty("messages").GetInt64());
            Assert.AreEqual(600000, row.GetProperty("rawBytes").GetInt64());
            Assert.AreEqual(120000, row.GetProperty("wireBytes").GetInt64());
            Assert.AreEqual(5.0, row.GetProperty("ratio").GetDouble(), 0.0001);
            Assert.AreEqual("zstd", row.GetProperty("codec").GetString());
            Assert.AreEqual(9.0, row.GetProperty("avgLevel").GetDouble(), 0.0001);
            Assert.AreEqual(480000, row.GetProperty("savedBytes").GetInt64());

            foreach (var window in new[] { "lastMinute", "lastTenMinutes", "sinceStart" })
            {
                Assert.AreEqual(1, json.GetProperty(window).GetProperty("messageCompression").GetArrayLength(), window);
            }
        }

        [TestMethod]
        public void NetworkJson_MessageCompressionIsAnEmptyListWhenNothingWasCompressed()
        {
            var json = Json(AdminApiResponse.Ok(AdminApiMapper.ToNetwork(new TrafficReport { NowUtc = T0, StartedUtc = T0, SinceStart = new TrafficWindow() })));
            Assert.AreEqual(JsonValueKind.Array, json.GetProperty("messageCompression").ValueKind);
            Assert.AreEqual(0, json.GetProperty("messageCompression").GetArrayLength());
            Assert.AreEqual(0, json.GetProperty("sinceStart").GetProperty("messageCompression").GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, json.GetProperty("lastMinute").ValueKind, "windows that have not closed yet stay null as before");
        }

        #endregion
    }
}
