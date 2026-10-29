using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

internal static class CompressionFixtures
{
    // GET network from a server that has the message-compression table: top-level totals and one list per window.
    public const string Network = "{\"startedUtc\":\"2026-10-29T03:00:00Z\",\"uptimeSeconds\":3600,\"windowsRecorded\":10,\"totals\":{\"sentBytes\":5000000,\"sentMessages\":900,\"receivedBytes\":2000000,\"receivedMessages\":700},"
        + "\"lastMinute\":{\"seconds\":60,\"out\":{\"bytes\":0,\"messages\":0,\"bytesPerSecond\":0,\"types\":[]},\"in\":{\"bytes\":0,\"messages\":0,\"bytesPerSecond\":0,\"types\":[]},\"snapshots\":[],"
        + "\"messageCompression\":[{\"type\":\"KerbalInfo\",\"messages\":3,\"rawBytes\":9300,\"wireBytes\":3000,\"ratio\":3.1,\"codec\":\"quicklz\"}]},"
        + "\"lastTenMinutes\":{\"seconds\":600,\"snapshots\":[],\"messageCompression\":[]},"
        + "\"sinceStart\":{\"seconds\":3600,\"snapshots\":[],\"messageCompression\":[{\"type\":\"VesselProtoMsgData\",\"messages\":120,\"rawBytes\":1500000,\"wireBytes\":300000,\"ratio\":5.0,\"codec\":\"quicklz\"},{\"type\":\"CraftInfo\",\"messages\":4,\"rawBytes\":2000,\"wireBytes\":0,\"ratio\":0,\"codec\":\"quicklz\"}]},"
        + "\"snapshotCompression\":[],"
        + "\"messageCompression\":[{\"type\":\"VesselProtoMsgData\",\"messages\":120,\"rawBytes\":1500000,\"wireBytes\":300000,\"ratio\":5.0,\"codec\":\"quicklz\"},{\"type\":\"CraftInfo\",\"messages\":4,\"rawBytes\":2000,\"wireBytes\":0,\"ratio\":0,\"codec\":\"quicklz\"}]}";
}

[TestClass]
public class RequestLogsTests
{
    private const string Token = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface";

    private static string Serialize(object body) => JsonSerializer.Serialize(body, ApiJson.Options);

    /// <summary>A real LiveSession (host mode) that talks to the fake server, so the path, verb, header and body are those of the shipped call.</summary>
    private static (LiveSession Session, string Folder) Live(int port)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vmpcontrol-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Config"));
        File.WriteAllText(Path.Combine(folder, "Config", "admin-token.txt"), Token);
        return (new LiveSession(new Profile { Name = "t", Mode = ProfileMode.Host, ServerFolder = folder, ApiPort = port }), folder);
    }

    [TestMethod]
    public void Body_HasTheNameAndTheNote()
    {
        Assert.AreEqual("{\"name\":\"Val\",\"note\":\"please send them\"}", Serialize(RequestLogsBody.Create("Val", "please send them")));
        Assert.AreEqual("{\"name\":\"Val\",\"note\":\"trimmed\"}", Serialize(RequestLogsBody.Create("  Val ", "  trimmed\t")));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   \t")]
    public void Body_LeavesTheNoteOut_WhenItIsEmpty(string note)
    {
        Assert.AreEqual("{\"name\":\"Val\"}", Serialize(RequestLogsBody.Create("Val", note)));
        Assert.IsNull(RequestLogsBody.CleanNote(note));
    }

    [TestMethod]
    public void Note_IsCutTo120Characters_NeverInsideASurrogatePair()
    {
        Assert.AreEqual(120, RequestLogsBody.MaxNoteLength);
        Assert.AreEqual(new string('a', 120), RequestLogsBody.CleanNote(new string('a', 300)));
        Assert.AreEqual(new string('a', 120), RequestLogsBody.CleanNote(new string('a', 120)));
        Assert.AreEqual(new string('a', 119), RequestLogsBody.CleanNote(new string('a', 119) + "\U0001F600 tail"), "the emoji would be cut in half");
    }

    [TestMethod]
    public async Task TheSession_PostsToClientsRequestLogs_WithTheTokenAndTheBody()
    {
        var seen = new List<string>();
        using var server = new ApiClientTests.FakeServer();
        server.Handler = (m, p) =>
        {
            seen.Add(m + " " + p);
            return (200, "{\"requested\":true,\"player\":\"Val\"}", 0);
        };
        var (session, folder) = Live(server.Port);
        using (session)
        {
            try
            {
                var result = await session.RequestLogsAsync("Val", "stalls at 21:00", CancellationToken.None);

                Assert.IsTrue(result.IsOk);
                Assert.AreEqual(200, result.StatusCode);
                Assert.AreEqual(true, result.Value.Requested);
                Assert.AreEqual("Val", result.Value.Player);
                CollectionAssert.AreEqual(new[] { "POST /api/v1/clients/requestlogs" }, seen);
                Assert.AreEqual("Bearer " + Token, server.LastAuth);
                Assert.AreEqual("{\"name\":\"Val\",\"note\":\"stalls at 21:00\"}", server.LastBody);

                await session.RequestLogsAsync("Val", "  ", CancellationToken.None);
                Assert.AreEqual("{\"name\":\"Val\"}", server.LastBody, "an empty note is not sent");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [DataTestMethod]
    [DataRow(404, "player not found or not connected")]
    [DataRow(409, "the game build of this player is not the build of the server, so it cannot be asked")]
    [DataRow(429, "logs were requested from this player less than 60 seconds ago")]
    [DataRow(500, "the request could not be sent")]
    public async Task ErrorAnswers_KeepTheServersMessage(int status, string message)
    {
        using var server = new ApiClientTests.FakeServer { Handler = (m, p) => (status, "{\"error\":\"" + message + "\"}", 0) };
        var (session, folder) = Live(server.Port);
        using (session)
        {
            try
            {
                var result = await session.RequestLogsAsync("Val", null, CancellationToken.None);

                Assert.AreEqual(ApiOutcome.Failed, result.Outcome);
                Assert.AreEqual(status, result.StatusCode);
                Assert.AreEqual(message, result.Error);
                var (text, failed) = RequestLogsText.Describe("Val", result);
                Assert.IsTrue(failed);
                Assert.AreEqual("failed: " + message, text);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [TestMethod]
    public async Task AnOldServer_ThatDoesNotKnowTheCall_IsNotSupported()
    {
        using var server = new ApiClientTests.FakeServer { Handler = (m, p) => (404, "{\"error\":\"not found\"}", 0) };
        var (session, folder) = Live(server.Port);
        using (session)
        {
            try
            {
                var result = await session.RequestLogsAsync("Val", null, CancellationToken.None);

                Assert.AreEqual(ApiOutcome.NotSupported, result.Outcome);
                Assert.AreEqual("failed: the server does not have this admin API call", RequestLogsText.Describe("Val", result).Text);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [TestMethod]
    public async Task Cancelling_ThrowsInsteadOfReportingAFailure()
    {
        using var slow = new ApiClientTests.FakeServer { Handler = (m, p) => (200, "{\"requested\":true}", 3000) };
        var (session, folder) = Live(slow.Port);
        using (session)
        {
            try
            {
                using var cts = new CancellationTokenSource(200);
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => session.RequestLogsAsync("Val", null, cts.Token));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [TestMethod]
    public void TheActivityText_ForSuccessAndOddAnswers()
    {
        var ok = new ApiResult<RequestLogsResultDto> { Outcome = ApiOutcome.Ok, Value = ApiJson.TryParse<RequestLogsResultDto>("{\"requested\":true,\"player\":\"Valentina\"}") };
        Assert.AreEqual(("Asked Valentina to send their game logs.", false), RequestLogsText.Describe("typed", ok));

        var noName = new ApiResult<RequestLogsResultDto> { Outcome = ApiOutcome.Ok, Value = ApiJson.TryParse<RequestLogsResultDto>("{\"requested\":true}") };
        Assert.AreEqual(("Asked Jebediah to send their game logs.", false), RequestLogsText.Describe("Jebediah", noName));

        var refused = new ApiResult<RequestLogsResultDto> { Outcome = ApiOutcome.Ok, Value = ApiJson.TryParse<RequestLogsResultDto>("{\"requested\":false,\"player\":\"Val\"}") };
        Assert.AreEqual(("failed: the server did not accept the request", true), RequestLogsText.Describe("Val", refused));

        Assert.AreEqual(("failed: timed out", true), RequestLogsText.Describe("Val", ApiResult<RequestLogsResultDto>.Fail(ApiOutcome.Unreachable, "timed out")));
        Assert.AreEqual(("failed: the request failed", true), RequestLogsText.Describe("Val", ApiResult<RequestLogsResultDto>.Fail(ApiOutcome.Failed, null)));
    }

    [TestMethod]
    public async Task TheMockServer_AnswersOk_ThenSimulatesTheOnePerMinuteLimit()
    {
        using var mock = new MockSession(null);

        var first = await mock.RequestLogsAsync("Val", null, CancellationToken.None);
        var again = await mock.RequestLogsAsync("val", null, CancellationToken.None);
        var other = await mock.RequestLogsAsync("Jebediah", "x", CancellationToken.None);

        Assert.IsTrue(first.IsOk);
        Assert.AreEqual(ApiOutcome.Failed, again.Outcome);
        Assert.AreEqual(429, again.StatusCode);
        Assert.IsTrue(other.IsOk);
    }
}

[TestClass]
public class MessageCompressionTests
{
    [TestMethod]
    public void TheTable_IsParsed_AtTopLevelAndInEveryWindow()
    {
        var n = ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network);

        var total = n.MessageCompression;
        Assert.AreEqual(2, total.Count);
        Assert.AreEqual("VesselProtoMsgData", total[0].Type);
        Assert.AreEqual(120L, total[0].Messages);
        Assert.AreEqual(1500000L, total[0].RawBytes);
        Assert.AreEqual(300000L, total[0].WireBytes);
        Assert.AreEqual(5.0, total[0].Ratio);
        Assert.AreEqual("quicklz", total[0].Codec);
        Assert.AreEqual("KerbalInfo", n.LastMinute.MessageCompression.Single().Type);
        Assert.AreEqual(3.1, n.LastMinute.MessageCompression.Single().Ratio);
        Assert.AreEqual(0, n.LastTenMinutes.MessageCompression.Count, "an empty list stays empty");
        Assert.AreEqual(2, n.SinceStart.MessageCompression.Count);
    }

    [TestMethod]
    public void AnOlderServer_WithoutTheField_GivesEmptyListsNotNull()
    {
        foreach (var json in new[] { Fixtures.NetworkFresh, Fixtures.NetworkBusy })
        {
            var n = ApiJson.TryParse<NetworkDto>(json);

            Assert.IsNotNull(n.MessageCompression);
            Assert.AreEqual(0, n.MessageCompression.Count);
        }

        Assert.AreEqual(0, ApiJson.TryParse<NetworkDto>(Fixtures.NetworkFresh).SinceStart.MessageCompression.Count);

        var busy = ApiJson.TryParse<NetworkDto>(Fixtures.NetworkBusy);
        Assert.AreEqual(0, busy.LastMinute.MessageCompression.Count);
        Assert.AreEqual(1, busy.LastMinute.Snapshots.Count, "the snapshot table next to it is untouched");

        var nulls = ApiJson.TryParse<NetworkDto>("{\"messageCompression\":null,\"sinceStart\":{\"messageCompression\":null}}");
        Assert.AreEqual(0, nulls.MessageCompression.Count);
        Assert.AreEqual(0, nulls.SinceStart.MessageCompression.Count);
    }

    [TestMethod]
    public void TheRatio_IsTheServers_OrRawOverWire_AndZeroWithoutWireBytes()
    {
        var n = ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network);
        Assert.AreEqual(5.0, n.MessageCompression[0].EffectiveRatio);
        Assert.AreEqual(0.0, n.MessageCompression[1].EffectiveRatio, "wireBytes 0 gives ratio 0");

        var missing = ApiJson.TryParse<MessageCompressionDto>("{\"type\":\"X\",\"rawBytes\":1000,\"wireBytes\":250}");
        Assert.AreEqual(4.0, missing.EffectiveRatio, "ratio left out: raw / wire");
        Assert.AreEqual(5.5, ApiJson.TryParse<MessageCompressionDto>("{\"rawBytes\":1000,\"wireBytes\":250,\"ratio\":5.5}").EffectiveRatio, "the server's ratio wins");
        Assert.AreEqual(0.0, new MessageCompressionDto().EffectiveRatio);
    }

    [TestMethod]
    public void TheRows_UseTheAppsFormatters()
    {
        var n = ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network);

        var rows = MessageCompressionView.Rows(n.MessageCompression);

        Assert.AreEqual(2, rows.Count);
        CollectionAssert.AreEqual(new object[] { "VesselProtoMsgData", "120", Fmt.Bytes(1500000), Fmt.Bytes(300000), Fmt.Bytes(1200000), "5.0x", "quicklz", "-" }, rows[0]);
        Assert.AreEqual("1.4 MB", rows[0][2]);
        Assert.AreEqual("293.0 KB", rows[0][3]);
        Assert.AreEqual("1.1 MB", rows[0][4], "an older server sends no savedBytes: raw - wire");
        CollectionAssert.AreEqual(new object[] { "CraftInfo", "4", "2.0 KB", "0 B", "2.0 KB", "-", "quicklz", "-" }, rows[1], "no wire bytes: ratio shown as a dash");

        var sparse = MessageCompressionView.Rows(new[] { new MessageCompressionDto { Messages = 1234567 } });
        CollectionAssert.AreEqual(new object[] { "-", "1,234,567", "0 B", "0 B", "0 B", "-", "-", "-" }, sparse.Single());
        Assert.AreEqual(MessageCompressionView.EmptyRow().Length, sparse.Single().Length, "the placeholder row has a cell for every column");
        Assert.AreEqual(0, MessageCompressionView.Rows(Array.Empty<MessageCompressionDto>()).Count);
        Assert.AreEqual("No compressed messages yet", MessageCompressionView.EmptyText);
    }

    [TestMethod]
    public void TheWindowSelector_PicksTheMatchingList()
    {
        var n = ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network);

        Assert.AreEqual("KerbalInfo", MessageCompressionView.Pick(n, n.LastMinute).Single().Type);
        Assert.AreEqual(2, MessageCompressionView.Pick(n, n.SinceStart).Count);
        Assert.AreEqual(0, MessageCompressionView.Pick(n, n.LastTenMinutes).Count, "an empty ten-minute window does not borrow the totals");
    }

    [TestMethod]
    public void SinceStart_FallsBackToTheTotals_WhenItsOwnListIsEmpty()
    {
        var n = new NetworkDto { SinceStart = new TrafficWindowDto(), MessageCompression = { new MessageCompressionDto { Type = "A" } } };

        Assert.AreEqual("A", MessageCompressionView.Pick(n, n.SinceStart).Single().Type);
        Assert.AreEqual("A", MessageCompressionView.Pick(n, null).Single().Type);
        Assert.AreEqual(0, MessageCompressionView.Pick(n, new TrafficWindowDto()).Count, "another window is not since-start");
        Assert.AreEqual(0, MessageCompressionView.Pick(null, null).Count);
    }

    [TestMethod]
    public async Task TheMockData_HasTheFiveTypes_InEveryWindowAndAtTopLevel()
    {
        using var mock = new MockSession(null);
        var n = (await mock.GetNetworkAsync(CancellationToken.None)).Value;
        var expected = new Dictionary<string, double> { ["VesselProtoMsgData"] = 5.2, ["KerbalInfo"] = 3.1, ["ScenarioInfo"] = 6.8, ["ContractInfo"] = 4.4, ["CraftInfo"] = 3.9 };

        foreach (var list in new[] { n.MessageCompression, n.LastMinute.MessageCompression, n.LastTenMinutes.MessageCompression, n.SinceStart.MessageCompression })
        {
            CollectionAssert.AreEquivalent(expected.Keys.ToList(), list.Select(m => m.Type).ToList());
            foreach (var m in list)
            {
                Assert.AreEqual(expected[m.Type], m.EffectiveRatio, 0.05, m.Type);
                Assert.AreEqual("zstd", m.Codec);
                Assert.IsTrue(m.WireBytes > 0 && m.RawBytes > m.WireBytes && m.Messages > 0, m.Type);
            }
        }
    }
}
