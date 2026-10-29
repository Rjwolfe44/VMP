using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

/// <summary>Sample responses. The status, network, clients, log, reports and backups strings are real output of the admin API on the scratch server (trimmed); the populated ones follow the same shapes.</summary>
internal static class Fixtures
{
    // GET status from a server build that has the machine block (scratch server, 2026-10-29).
    public const string Status = "{\"version\":\"0.2.0-train.20261023.10\",\"startedUtc\":\"2026-10-29T04:02:51.4817633Z\",\"uptimeSeconds\":13.8,\"workingSetBytes\":263659520,\"cpuPercent\":0.3,\"playersOnline\":0,\"players\":[],\"vesselCount\":60,\"universeBytes\":11506464,\"settings\":{\"serverName\":\"KSP Server\",\"gameMode\":\"Career\",\"warpMode\":\"Subspace\",\"maxPlayers\":20,\"passwordProtected\":false,\"cheats\":true,\"gamePort\":8811},\"machine\":{\"ramTotalBytes\":66124611584,\"ramFreeBytes\":39560269824,\"osUptimeSeconds\":99223.1,\"diskFreeBytes\":387769745408,\"diskTotalBytes\":1999325253632,\"diskRoot\":\"C:\\\\\"}}";

    // GET status from the first admin-API build (no machine block).
    public const string StatusOld = "{\"version\":\"0.2.0-train.20261023.10\",\"startedUtc\":\"2026-10-29T03:45:30.3426424Z\",\"uptimeSeconds\":11.6,\"workingSetBytes\":207986688,\"cpuPercent\":0.4,\"playersOnline\":0,\"players\":[],\"vesselCount\":60,\"universeBytes\":11506464,\"settings\":{\"serverName\":\"KSP Server\",\"gameMode\":\"Career\",\"warpMode\":\"Subspace\",\"maxPlayers\":20,\"passwordProtected\":false,\"cheats\":true,\"gamePort\":8811}}";

    public const string StatusWithPlayers = "{\"version\":\"1.0\",\"uptimeSeconds\":11520,\"playersOnline\":2,\"players\":[{\"name\":\"Vlad\",\"subspace\":3,\"connectedSinceUtc\":\"2026-10-29T01:00:00Z\",\"connectedSeconds\":8040.5},{\"name\":\"Jeb\",\"subspace\":5,\"connectedSinceUtc\":\"2026-10-29T02:00:00Z\",\"connectedSeconds\":120}],\"settings\":{\"serverName\":\"Train\"}}";

    // GET network right after start: no window has closed yet, so lastMinute and lastTenMinutes are null.
    public const string NetworkFresh = "{\"startedUtc\":\"2026-10-29T03:45:32.0928231Z\",\"uptimeSeconds\":9.8953091,\"windowsRecorded\":0,\"totals\":{\"sentBytes\":0,\"sentMessages\":0,\"receivedBytes\":0,\"receivedMessages\":0},\"lastMinute\":null,\"lastTenMinutes\":null,\"sinceStart\":{\"startUtc\":\"2026-10-29T03:45:32.0928231Z\",\"endUtc\":\"2026-10-29T03:45:41.9881322Z\",\"seconds\":9.8953091,\"out\":{\"bytes\":0,\"messages\":0,\"bytesPerSecond\":0,\"types\":[]},\"in\":{\"bytes\":0,\"messages\":0,\"bytesPerSecond\":0,\"types\":[]},\"snapshots\":[]},\"snapshotCompression\":[]}";

    public const string NetworkBusy = "{\"startedUtc\":\"2026-10-29T03:00:00Z\",\"uptimeSeconds\":3600,\"windowsRecorded\":10,\"totals\":{\"sentBytes\":5000000,\"sentMessages\":900,\"receivedBytes\":2000000,\"receivedMessages\":700},\"lastMinute\":{\"startUtc\":\"2026-10-29T03:59:00Z\",\"endUtc\":\"2026-10-29T04:00:00Z\",\"seconds\":60,\"out\":{\"bytes\":120000,\"messages\":40,\"bytesPerSecond\":2000,\"types\":[{\"type\":\"VesselUpdateMsg\",\"bytes\":90000,\"messages\":30,\"bytesPerSecond\":1500},{\"type\":\"ChatMsg\",\"bytes\":30000,\"messages\":10,\"bytesPerSecond\":500}]},\"in\":{\"bytes\":3000,\"messages\":5,\"bytesPerSecond\":50,\"types\":[]},\"snapshots\":[{\"domain\":\"Vessel\",\"snapshots\":12,\"rawBytes\":60000,\"wireBytes\":20000,\"ratio\":3,\"encodings\":{\"raw\":1,\"quickLz\":0,\"zstd\":2,\"zstdDictionary\":9}}]},\"lastTenMinutes\":null,\"sinceStart\":null,\"snapshotCompression\":[]}";

    public const string Clients = "{\"clients\":[{\"name\":\"Vlad\",\"authenticated\":true,\"subspace\":3,\"connectedSinceUtc\":\"2026-10-29T01:00:00Z\",\"connectedSeconds\":100,\"bytesIn\":5,\"bytesOut\":7,\"wireBytesIn\":8,\"wireBytesOut\":null,\"build\":\"0.2.0\",\"latencyMs\":41.5},{\"name\":\"Old\"}]}";
    public const string ClientsEmpty = "{\"clients\":[]}";

    // GET log?lines=5 (the Russian text is part of the real server's start-up line).
    public const string Log = "{\"requested\":5,\"count\":5,\"lines\":[\"[03:45:31][VMP]: Starting \\u0027KSP Server\\u0027 on Address 127.0.0.1 Port 8811... \",\"[03:45:32][VMP]: [AdminApi] listening on 127.0.0.1:8901 (local only, bearer token required)\",\"[03:45:32][VMP]: All systems up and running. \\u041F\\u043E\\u0435\\u0445\\u0430\\u043B\\u0438!\",\"[03:45:32][Debug]: Performing a GarbageCollection\",\"[03:45:32][Warning]: Configuration specifies Dual Stack but does not use IPv6 local address; Dual stack will not work.\"]}";

    public const string ReportsEmpty = "{\"zipCount\":0,\"count\":0,\"entries\":[]}";
    public const string Reports = "{\"zipCount\":2,\"count\":2,\"entries\":[{\"file\":\"Jeb_20261028-101500Z-stall.zip\",\"player\":\"Jeb\",\"utc\":\"2026-10-28T10:15:00Z\",\"bytes\":2480113,\"reason\":\"stall\"},{\"file\":\"Vlad_20261027-221030Z.zip\",\"player\":\"Vlad\",\"utc\":\"2026-10-27T22:10:30Z\",\"bytes\":5902210,\"reason\":\"manual\"}]}";

    public const string BackupsEmpty = "{\"backups\":[]}";
    public const string Backups = "{\"backups\":[{\"name\":\"Universe-20261028-120000.tar.zst\",\"bytes\":6420113,\"createdUtc\":\"2026-10-28T12:00:00Z\",\"kind\":\"tar.zst\"}]}";
    public const string BackupResult = "{\"fileName\":\"Universe-20261028-120000.tar.zst\",\"files\":1912,\"rawBytes\":38412980,\"compressedBytes\":6455000,\"ratio\":5.95,\"seconds\":1.8,\"level\":9,\"verified\":true,\"sha256\":\"ab\",\"excluded\":[\"Backups\",\"Logs\"],\"skipped\":[],\"pruned\":[\"old.zip\"]}";
}

[TestClass]
public class ApiModelTests
{
    [TestMethod]
    public void RealStatus_ParsesEveryField_IncludingTheMachineBlock()
    {
        var s = ApiJson.TryParse<StatusDto>(Fixtures.Status);

        Assert.AreEqual("0.2.0-train.20261023.10", s.Version);
        Assert.AreEqual(13.8, s.UptimeSeconds);
        Assert.AreEqual(263659520L, s.WorkingSetBytes);
        Assert.AreEqual(60, s.VesselCount);
        Assert.AreEqual(11506464L, s.UniverseBytes);
        Assert.AreEqual("KSP Server", s.Settings.ServerName);
        Assert.AreEqual("Career", s.Settings.GameMode);
        Assert.AreEqual(8811, s.Settings.GamePort);
        Assert.AreEqual(0, s.PlayerCount);
        Assert.AreEqual(66124611584L, s.Machine.RamTotalBytes);
        Assert.AreEqual(39560269824L, s.Machine.RamFreeBytes);
        Assert.AreEqual(99223.1, s.Machine.OsUptimeSeconds);
        Assert.AreEqual(387769745408L, s.Machine.DiskFreeBytes);
        Assert.AreEqual(1999325253632L, s.Machine.DiskTotalBytes);
        Assert.AreEqual("C:\\", s.Machine.DiskRoot);
        Assert.AreEqual(DateTimeKind.Utc, s.StartedUtc.Value.Kind);
    }

    [TestMethod]
    public void StatusFromAServerWithoutTheMachineBlock_HasNullMachine()
    {
        var s = ApiJson.TryParse<StatusDto>(Fixtures.StatusOld);

        Assert.IsNotNull(s);
        Assert.IsNull(s.Machine);
        Assert.AreEqual(60, s.VesselCount);
    }

    [TestMethod]
    public void Players_AreParsed_AndPlayerCountIsTheLargerOfCountAndList()
    {
        var s = ApiJson.TryParse<StatusDto>(Fixtures.StatusWithPlayers);

        Assert.AreEqual(2, s.Players.Count);
        Assert.AreEqual("Vlad", s.Players[0].Name);
        Assert.AreEqual(3, s.Players[0].Subspace);
        Assert.AreEqual(8040.5, s.Players[0].ConnectedSeconds);
        Assert.AreEqual(2, s.PlayerCount);
        Assert.AreEqual(3, new StatusDto { PlayersOnline = 1, Players = { new PlayerDto(), new PlayerDto(), new PlayerDto() } }.PlayerCount);
        Assert.AreEqual(4, new StatusDto { PlayersOnline = 4 }.PlayerCount);
    }

    [TestMethod]
    public void MissingAndUnknownFields_AreTolerated()
    {
        var s = ApiJson.TryParse<StatusDto>("{\"version\":\"1\",\"brandNewField\":{\"a\":[1,2,3]},\"players\":null,\"settings\":{\"serverName\":\"X\",\"somethingNew\":true}}");

        Assert.AreEqual("1", s.Version);
        Assert.AreEqual("X", s.Settings.ServerName);
        Assert.IsNotNull(s.Players);
        Assert.AreEqual(0, s.Players.Count);
        Assert.AreEqual(0, s.VesselCount);
        Assert.IsNull(s.Machine);
        Assert.IsNotNull(ApiJson.TryParse<StatusDto>("{}"));
    }

    [TestMethod]
    public void NumbersSentAsStrings_AndCaseDifferences_AreAccepted()
    {
        var s = ApiJson.TryParse<StatusDto>("{\"VesselCount\":\"12\",\"CPUPERCENT\":1.5}");

        Assert.AreEqual(12, s.VesselCount);
        Assert.AreEqual(1.5, s.CpuPercent);
    }

    [TestMethod]
    public void NotJson_OrTheWrongType_GivesNullInsteadOfThrowing()
    {
        Assert.IsNull(ApiJson.TryParse<StatusDto>(""));
        Assert.IsNull(ApiJson.TryParse<StatusDto>(null));
        Assert.IsNull(ApiJson.TryParse<StatusDto>("<html>nope</html>"));
        Assert.IsNull(ApiJson.TryParse<StatusDto>("{\"vesselCount\":\"many\"}"));
        Assert.IsNull(ApiJson.TryParse<StatusDto>("{\"players\":5}"));
        Assert.AreEqual("player not found", ApiJson.ErrorMessageOf("{\"error\":\"player not found\"}"));
        Assert.IsNull(ApiJson.ErrorMessageOf("not json"));
    }

    [TestMethod]
    public void Network_Fresh_HasNullWindowsAndEmptyTables()
    {
        var n = ApiJson.TryParse<NetworkDto>(Fixtures.NetworkFresh);

        Assert.AreEqual(0, n.WindowsRecorded);
        Assert.IsNull(n.LastMinute);
        Assert.IsNull(n.LastTenMinutes);
        Assert.AreEqual(0L, n.Totals.SentBytes);
        Assert.AreEqual(0, n.SinceStart.Out.Types.Count);
        Assert.AreEqual(0, n.SnapshotCompression.Count);
        Assert.AreEqual(9.8953091, n.UptimeSeconds, 1e-9);
    }

    [TestMethod]
    public void Network_Busy_ParsesTypesSnapshotsAndEncodings()
    {
        var n = ApiJson.TryParse<NetworkDto>(Fixtures.NetworkBusy);

        Assert.AreEqual(5000000L, n.Totals.SentBytes);
        Assert.AreEqual(2000.0, n.LastMinute.Out.BytesPerSecond);
        Assert.AreEqual(2, n.LastMinute.Out.Types.Count);
        Assert.AreEqual("VesselUpdateMsg", n.LastMinute.Out.Types[0].Type);
        Assert.AreEqual(90000L, n.LastMinute.Out.Types[0].Bytes);
        Assert.AreEqual(30L, n.LastMinute.Out.Types[0].Messages);
        Assert.AreEqual(1500.0, n.LastMinute.Out.Types[0].BytesPerSecond);
        var snapshot = n.LastMinute.Snapshots.Single();
        Assert.AreEqual("Vessel", snapshot.Domain);
        Assert.AreEqual(3.0, snapshot.Ratio);
        Assert.AreEqual("zstd-dict 9, zstd 2, raw 1", snapshot.Encodings.Summary());
        Assert.AreEqual("-", new EncodingCountsDto().Summary());
        Assert.IsNull(n.SinceStart);
    }

    [TestMethod]
    public void Clients_NullableWireAndLatency_AndSparseEntries()
    {
        var c = ApiJson.TryParse<ClientsDto>(Fixtures.Clients).Clients;

        Assert.AreEqual(2, c.Count);
        Assert.AreEqual(41.5, c[0].LatencyMs);
        Assert.AreEqual(8L, c[0].WireBytesIn);
        Assert.IsNull(c[0].WireBytesOut);
        Assert.AreEqual("0.2.0", c[0].Build);
        Assert.AreEqual("Old", c[1].Name);
        Assert.IsNull(c[1].LatencyMs);
        Assert.AreEqual(0, ApiJson.TryParse<ClientsDto>(Fixtures.ClientsEmpty).Clients.Count);
    }

    [TestMethod]
    public void Log_KeepsUnicode_AndCount()
    {
        var log = ApiJson.TryParse<LogDto>(Fixtures.Log);

        Assert.AreEqual(5, log.Lines.Count);
        StringAssert.Contains(log.Lines[0], "Starting 'KSP Server'");
        StringAssert.Contains(log.Lines[2], "Поехали!");
        StringAssert.Contains(log.Lines[4], "[Warning]");
    }

    [TestMethod]
    public void ReportsAndBackups_Parse_AndEmptyOnesAreEmptyNotNull()
    {
        var r = ApiJson.TryParse<ReportsDto>(Fixtures.Reports);
        Assert.AreEqual(2, r.ZipCount);
        Assert.AreEqual("Jeb", r.Entries[0].Player);
        Assert.AreEqual("stall", r.Entries[0].Reason);
        Assert.AreEqual(2480113L, r.Entries[0].Bytes);
        Assert.AreEqual("Jeb_20261028-101500Z-stall.zip", r.Entries[0].File);
        Assert.AreEqual(0, ApiJson.TryParse<ReportsDto>(Fixtures.ReportsEmpty).Entries.Count);

        var b = ApiJson.TryParse<BackupsDto>(Fixtures.Backups).Backups.Single();
        Assert.AreEqual("tar.zst", b.Kind);
        Assert.AreEqual(6420113L, b.Bytes);
        Assert.AreEqual(new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), b.CreatedUtc);
        Assert.AreEqual(0, ApiJson.TryParse<BackupsDto>(Fixtures.BackupsEmpty).Backups.Count);

        var result = ApiJson.TryParse<BackupResultDto>(Fixtures.BackupResult);
        Assert.AreEqual(5.95, result.Ratio);
        Assert.IsTrue(result.Verified);
        CollectionAssert.AreEqual(new[] { "Backups", "Logs" }, result.Excluded);
        Assert.AreEqual(0, result.Skipped.Count);
        Assert.AreEqual(1, result.Pruned.Count);
    }
}
