using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

[TestClass]
public class TrayStateTests
{
    private static PollSnapshot Api(int players, string name = "KSP Server", double uptime = 11520)
    {
        var status = new StatusDto { PlayersOnline = players, UptimeSeconds = uptime, Settings = new SettingsHighlightsDto { ServerName = name } };
        for (var i = 0; i < players; i++) status.Players.Add(new PlayerDto { Name = "P" + i });
        return new PollSnapshot { Api = ApiAvailability.Ok, Status = status };
    }

    private static PollSnapshot NoApi(ApiAvailability api, bool? running, int? players = null) =>
        new() { Api = api, Script = new ScriptStatus { Running = running, Players = players } };

    [TestMethod]
    public void Mapping_GreyRedBlueGreen()
    {
        Assert.AreEqual(TrayState.Connecting, TrayStateMapper.Map(null));
        Assert.AreEqual(TrayState.Connecting, TrayStateMapper.Map(new PollSnapshot { Api = ApiAvailability.Unknown }));
        Assert.AreEqual(TrayState.Idle, TrayStateMapper.Map(Api(0)));
        Assert.AreEqual(TrayState.Active, TrayStateMapper.Map(Api(2)));
    }

    [TestMethod]
    public void WithoutTheApi_TheControlScriptDecides()
    {
        Assert.AreEqual(TrayState.Connecting, TrayStateMapper.Map(new PollSnapshot { Api = ApiAvailability.Unreachable }), "script not asked yet");
        Assert.AreEqual(TrayState.Down, TrayStateMapper.Map(NoApi(ApiAvailability.Unreachable, false)));
        Assert.AreEqual(TrayState.Down, TrayStateMapper.Map(NoApi(ApiAvailability.Unreachable, null)), "script failed = unreachable");
        Assert.AreEqual(TrayState.Idle, TrayStateMapper.Map(NoApi(ApiAvailability.NotSupported, true)));
        Assert.AreEqual(TrayState.Idle, TrayStateMapper.Map(NoApi(ApiAvailability.NotSupported, true, 0)));
        Assert.AreEqual(TrayState.Active, TrayStateMapper.Map(NoApi(ApiAvailability.NotSupported, true, 3)));
        Assert.AreEqual(TrayState.Down, TrayStateMapper.Map(NoApi(ApiAvailability.Unauthorized, false)));
    }

    [TestMethod]
    public void Tooltip_NameOnlineAndUptime()
    {
        Assert.AreEqual("KSP Server - 2 online - up 3h 12m", TrayStateMapper.Tooltip(Api(2), "profile"));
        Assert.AreEqual("KSP Server - 0 online - up 3h 12m", TrayStateMapper.Tooltip(Api(0), "profile"));
        Assert.AreEqual("Train PC - connecting...", TrayStateMapper.Tooltip(null, "Train PC"));
        Assert.AreEqual("Train PC - server down", TrayStateMapper.Tooltip(NoApi(ApiAvailability.Unreachable, false), "Train PC"));
        Assert.AreEqual("Train PC - unreachable", TrayStateMapper.Tooltip(NoApi(ApiAvailability.Unreachable, null), "Train PC"));
        Assert.AreEqual("Train PC - running, 1 online (no admin API)", TrayStateMapper.Tooltip(NoApi(ApiAvailability.NotSupported, true, 1), "Train PC"));
        Assert.IsTrue(TrayStateMapper.Tooltip(Api(1, new string('x', 300)), "p").Length <= 127);
    }

    [TestMethod]
    public void Notifications_FirstSnapshotIsSilent_ThenJoinsLeavesDownAndBack()
    {
        var t = new NotificationTracker();
        Assert.AreEqual(0, t.Process(Api(1)).Count);

        CollectionAssert.AreEqual(new[] { "P1 joined" }, t.Process(Api(2)));
        Assert.AreEqual(0, t.Process(Api(2)).Count);
        CollectionAssert.AreEqual(new[] { "P1 left" }, t.Process(Api(1)));

        var down = NoApi(ApiAvailability.Unreachable, false);
        Assert.AreEqual(0, t.Process(down).Count, "one failed poll is not an announcement");
        CollectionAssert.AreEqual(new[] { "Server went down" }, t.Process(down));
        Assert.AreEqual(0, t.Process(down).Count, "announced once");
        CollectionAssert.AreEqual(new[] { "Server is back" }, t.Process(Api(0)));
        Assert.AreEqual(0, t.Process(Api(0)).Count);
    }

    [TestMethod]
    public void Notifications_ABlipOfOneFailedPollIsNotReported_AndConnectingIsIgnored()
    {
        var t = new NotificationTracker();
        t.Process(Api(0));
        Assert.AreEqual(0, t.Process(NoApi(ApiAvailability.Unreachable, false)).Count);
        Assert.AreEqual(0, t.Process(Api(0)).Count);
        Assert.AreEqual(0, t.Process(new PollSnapshot()).Count);
        Assert.AreEqual(0, t.Process(NoApi(ApiAvailability.Unreachable, false)).Count);
        Assert.AreEqual(0, new NotificationTracker().Process(NoApi(ApiAvailability.Unreachable, false)).Count, "never was up");
    }

    [TestMethod]
    public void ControlScriptOutput_IsParsed_FromBothScripts()
    {
        var running = ControlOutputParser.ParseStatus("Server is RUNNING (process 1234).\r\nPlayers online: 2  (Vlad, Jeb)\r\nLog: lmpserver_x.log  (last written 2026-10-29T01:00:00)");
        Assert.AreEqual(true, running.Running);
        Assert.AreEqual(2, running.Players);
        CollectionAssert.AreEqual(new[] { "Vlad", "Jeb" }, running.Names);

        var quiet = ControlOutputParser.ParseStatus("Server is RUNNING (process 1234).\nPlayers online: nobody.");
        Assert.AreEqual(true, quiet.Running);
        Assert.AreEqual(0, quiet.Players);

        Assert.AreEqual(false, ControlOutputParser.ParseStatus("Server is NOT running.").Running);
        Assert.AreEqual(true, ControlOutputParser.ParseStatus("{\"Running\":true,\"ProcessId\":4321}").Running);
        Assert.AreEqual(false, ControlOutputParser.ParseStatus("{\r\n  \"Running\": false,\r\n  \"ProcessId\": null\r\n}").Running);
        Assert.IsNull(ControlOutputParser.ParseStatus("Could not reach or run on the train PC (ssh exit 255).").Running);
        Assert.IsNull(ControlOutputParser.ParseStatus("").Running);
        Assert.IsNull(ControlOutputParser.ParseStatus(null).Running);
    }
}

[TestClass]
public class FormattingTests
{
    [TestMethod]
    public void Bytes_UseBinaryUnitsWithOneDecimal()
    {
        Assert.AreEqual("-", Fmt.Bytes(null));
        Assert.AreEqual("0 B", Fmt.Bytes(0));
        Assert.AreEqual("1023 B", Fmt.Bytes(1023));
        Assert.AreEqual("1.0 KB", Fmt.Bytes(1024));
        Assert.AreEqual("1.5 KB", Fmt.Bytes(1536));
        Assert.AreEqual("11.0 MB", Fmt.Bytes(11506464));
        Assert.AreEqual("247.8 MB", Fmt.Bytes(259825664));
        Assert.AreEqual("61.6 GB", Fmt.Bytes(66124611584));
        Assert.AreEqual("1.8 TB", Fmt.Bytes(1999325253632));
        Assert.AreEqual("-1.0 KB", Fmt.Bytes(-1024));
    }

    [TestMethod]
    public void Rates_Percent_Ratio_Count()
    {
        Assert.AreEqual("0 B/s", Fmt.Rate(0));
        Assert.AreEqual("14.2 KB/s", Fmt.Rate(14540));
        Assert.AreEqual("0.4%", Fmt.Percent(0.4));
        Assert.AreEqual("12.0%", Fmt.Percent(12));
        Assert.AreEqual("3.15x", Fmt.Ratio(3.15));
        Assert.AreEqual("4.0x", Fmt.Ratio(4));
        Assert.AreEqual("-", Fmt.Ratio(0));
        Assert.AreEqual("9,415,469", Fmt.Count(9415469));
    }

    [TestMethod]
    public void Durations_ReadLikeTheTooltip()
    {
        Assert.AreEqual("-", Fmt.Duration(null));
        Assert.AreEqual("-", Fmt.Duration(double.NaN));
        Assert.AreEqual("0s", Fmt.Duration(0));
        Assert.AreEqual("0s", Fmt.Duration(-5));
        Assert.AreEqual("45s", Fmt.Duration(45.9));
        Assert.AreEqual("1m 00s", Fmt.Duration(60));
        Assert.AreEqual("12m 05s", Fmt.Duration(725));
        Assert.AreEqual("3h 12m", Fmt.Duration(3 * 3600 + 12 * 60 + 30));
        Assert.AreEqual("1d 0h", Fmt.Duration(86400));
        Assert.AreEqual("4d 7h", Fmt.Duration(4 * 86400 + 7 * 3600 + 1234));
    }

    [TestMethod]
    public void Output_DoesNotDependOnTheCurrentCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.AreEqual("1.5 KB", Fmt.Bytes(1536));
            Assert.AreEqual("3.15x", Fmt.Ratio(3.15));
            Assert.AreEqual("9,415,469", Fmt.Count(9415469));
            Assert.AreEqual("0.4%", Fmt.Percent(0.4));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [TestMethod]
    public void BackoffSchedule_1_2_5_10_30_then_60()
    {
        var seconds = Enumerable.Range(1, 9).Select(i => (int)Backoff.Delay(i).TotalSeconds).ToArray();

        CollectionAssert.AreEqual(new[] { 1, 2, 5, 10, 30, 60, 60, 60, 60 }, seconds);
        Assert.AreEqual(1, (int)Backoff.Delay(0).TotalSeconds);
        Assert.AreEqual(1, (int)Backoff.Delay(-4).TotalSeconds);
        Assert.AreEqual(60, (int)Backoff.Delay(int.MaxValue).TotalSeconds);
    }
}

/// <summary>The API client against a real HttpListener on a free local port.</summary>
[TestClass]
public class ApiClientTests
{
    private const string Token = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface";

    internal sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public string LastAuth { get; private set; }
        public string LastBody { get; private set; }
        public Func<string, string, (int Status, string Body, int DelayMs)> Handler = (m, p) => (200, "{}", 0);

        public FakeServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                LastAuth = ctx.Request.Headers["Authorization"];
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) LastBody = await reader.ReadToEndAsync();
                var (status, body, delay) = Handler(ctx.Request.HttpMethod, ctx.Request.Url.AbsolutePath);
                if (delay > 0) await Task.Delay(delay);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static TokenSource Tokens() => new(_ => Task.FromResult<(string, TokenProblem, string)>((Token, TokenProblem.None, null)));

    [TestMethod]
    public async Task Get_SendsTheBearerToken_AndParsesTheBody()
    {
        using var server = new FakeServer { Handler = (m, p) => (200, Fixtures.Status, 0) };
        using var client = new AdminApiClient(server.Port, Tokens());

        var result = await client.GetAsync<StatusDto>("status", CancellationToken.None);

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual("Bearer " + Token, server.LastAuth);
        Assert.AreEqual("KSP Server", result.Value.Settings.ServerName);
    }

    [TestMethod]
    public async Task Post_SendsJson_AndTheStatusCodesMapToOutcomes()
    {
        using var server = new FakeServer();
        using var client = new AdminApiClient(server.Port, Tokens());

        server.Handler = (m, p) => (200, "{\"ok\":true}", 0);
        var say = await client.PostAsync<OkDto>("say", new { text = "hello" }, CancellationToken.None);
        Assert.IsTrue(say.IsOk && say.Value.Ok);
        Assert.AreEqual("{\"text\":\"hello\"}", server.LastBody);

        server.Handler = (m, p) => (404, "{\"error\":\"player not found\"}", 0);
        var kick = await client.PostAsync<OkDto>("kick", new { name = "x", reason = "" }, CancellationToken.None);
        Assert.AreEqual(ApiOutcome.Failed, kick.Outcome);
        Assert.AreEqual("player not found", kick.Error);

        server.Handler = (m, p) => (404, "{\"error\":\"not found\"}", 0);
        Assert.AreEqual(ApiOutcome.NotSupported, (await client.GetAsync<StatusDto>("status", CancellationToken.None)).Outcome);

        server.Handler = (m, p) => (409, "{\"error\":\"a backup is already running\"}", 0);
        var busy = await client.PostAsync<BackupResultDto>("backups", null, CancellationToken.None);
        Assert.AreEqual(ApiOutcome.Failed, busy.Outcome);
        Assert.AreEqual("a backup is already running", busy.Error);

        server.Handler = (m, p) => (200, "<html>not json</html>", 0);
        Assert.AreEqual(ApiOutcome.Failed, (await client.GetAsync<StatusDto>("status", CancellationToken.None)).Outcome);
    }

    [TestMethod]
    public async Task A401_IsReported_WithoutDetail_AndForgetsTheToken()
    {
        var fetches = 0;
        var tokens = new TokenSource(_ =>
        {
            fetches++;
            return Task.FromResult<(string, TokenProblem, string)>((Token, TokenProblem.None, null));
        });
        using var server = new FakeServer { Handler = (m, p) => (401, "{\"error\":\"unauthorized\"}", 0) };
        using var client = new AdminApiClient(server.Port, tokens);

        var result = await client.GetAsync<StatusDto>("status", CancellationToken.None);
        await client.GetAsync<StatusDto>("status", CancellationToken.None);

        Assert.AreEqual(ApiOutcome.Unauthorized, result.Outcome);
        Assert.IsFalse((result.Error ?? "").Contains(Token));
        Assert.AreEqual(1, fetches, "the token is read once; a re-read after a 401 is rate limited");
    }

    [TestMethod]
    public async Task NothingListening_OrTooSlow_IsUnreachable()
    {
        int port;
        using (var server = new FakeServer()) port = server.Port;
        using (var client = new AdminApiClient(port, Tokens()))
        {
            Assert.AreEqual(ApiOutcome.Unreachable, (await client.GetAsync<StatusDto>("status", CancellationToken.None, 2)).Outcome);
        }

        using var slow = new FakeServer { Handler = (m, p) => (200, "{}", 3000) };
        using var slowClient = new AdminApiClient(slow.Port, Tokens());
        var result = await slowClient.GetAsync<StatusDto>("status", CancellationToken.None, 1);
        Assert.AreEqual(ApiOutcome.Unreachable, result.Outcome);
        Assert.AreEqual("timed out", result.Error);
    }

    [TestMethod]
    public async Task Cancelling_ThrowsInsteadOfReportingUnreachable()
    {
        using var slow = new FakeServer { Handler = (m, p) => (200, "{}", 3000) };
        using var client = new AdminApiClient(slow.Port, Tokens());
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.GetAsync<StatusDto>("status", cts.Token, 5));
    }

    [TestMethod]
    public async Task NoTokenFile_MeansAnOldServer_AndAnSshFailureMeansUnreachable()
    {
        var empty = Path.Combine(Path.GetTempPath(), "vmpcontrol-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            using var client = new AdminApiClient(1, TokenSource.ForFile(empty));
            Assert.AreEqual(ApiOutcome.NotSupported, (await client.GetAsync<StatusDto>("status", CancellationToken.None)).Outcome);
        }
        finally
        {
            Directory.Delete(empty);
        }

        var sshDown = new TokenSource(_ => Task.FromResult<(string, TokenProblem, string)>((null, TokenProblem.Unreachable, "ssh could not connect")));
        using var client2 = new AdminApiClient(1, sshDown);
        var result = await client2.GetAsync<StatusDto>("status", CancellationToken.None);
        Assert.AreEqual(ApiOutcome.Unreachable, result.Outcome);
        Assert.AreEqual("ssh could not connect", result.Error);
    }
}
