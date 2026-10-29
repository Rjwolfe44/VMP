using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

/// <summary>Phase 5 in VMP Control: the selection and confirmation logic of the Backups tab, the DELETE / prune calls, and the sample data of --mock.</summary>
[TestClass]
public class BackupManagementTests
{
    private const string Token = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface";

    private static BackupEntryDto E(string name, long bytes = 1000) => new() { Name = name, Bytes = bytes, Kind = name.EndsWith(".zip") ? "zip" : name.EndsWith(".tar") ? "screenshots.tar" : "tar.zst" };

    private static BackupsDto Data(params BackupEntryDto[] entries)
    {
        var dto = new BackupsDto { Backups = entries.ToList(), Count = entries.Length, TotalBytes = entries.Sum(e => e.Bytes), CapBytes = 2048L * 1024 * 1024 };
        dto.Policy = new BackupPolicyDto { KeepNewest = 10, KeepDailyDays = 14, KeepScreenshotArchives = 10, LegacyZipKeep = 0, MaxTotalMegabytes = 2048 };
        return dto;
    }

    private static TokenSource Tokens() => new(_ => Task.FromResult<(string, TokenProblem, string)>((Token, TokenProblem.None, null)));

    [TestMethod]
    public void IsDeletableName_MatchesTheServersPatternsExactly()
    {
        foreach (var ok in new[] { "Universe-20261028-120000.tar.zst", "Universe-20261028-120000.tar.zst.sha256", "Universe-20260905-101500.zip", "Universe-20260905-101500.zip.sha256", "Screenshots-20261028-030000.tar" })
            Assert.IsTrue(BackupManagement.IsDeletableName(ok), ok);

        foreach (var bad in new[]
                 {
                     null, "", "..", "../Universe-20261028-120000.tar.zst", "..\\Universe-20261028-120000.tar.zst", "a/Universe-20261028-120000.tar.zst", "universe-20261028-120000.tar.zst",
                     "Universe-20261028-120000.TAR.ZST", "Universe-20261028-120000.tar.zst\n", "Universe-20261028-120000.tar.zst ", "Universe-20261028-120000.tar.zst.part",
                     "Universe-20261028-120000.tar", "Screenshots-20261028-030000.tar.sha256", "Universe-2026102-120000.tar.zst", "vmp-server.json", "Universe-٢٠٢٦١٠٢٨-120000.tar.zst",
                 })
            Assert.IsFalse(BackupManagement.IsDeletableName(bad), bad ?? "<null>");
    }

    [TestMethod]
    public void NewestArchive_IsTheNewestTarZstByTheTimeInItsName()
    {
        var list = new[] { E("Universe-20261027-090000.tar.zst"), E("Universe-20261028-090000.tar.zst"), E("Universe-20261029-090000.zip"), E("Screenshots-20261030-030000.tar"), E("Universe-20261028-090000.tar.zst.part") };
        Assert.AreEqual("Universe-20261028-090000.tar.zst", BackupManagement.NewestArchive(list));
        Assert.IsNull(BackupManagement.NewestArchive(new[] { E("Universe-20261029-090000.zip") }));
        Assert.IsNull(BackupManagement.NewestArchive(null));
    }

    [TestMethod]
    public void Summary_ShowsCountSizeAndCap_OrNoCap_OrJustTheCountForAnOldServer()
    {
        var data = Data(E("Universe-20261028-090000.tar.zst", 3 * 1024 * 1024), E("Universe-20260905-101500.zip", 1024 * 1024));
        Assert.AreEqual($"2 backups, {Fmt.Bytes(4 * 1024 * 1024)} of 2048 MB cap", BackupManagement.Summary(data));

        data.CapBytes = 0;
        data.Policy.MaxTotalMegabytes = 0;
        Assert.AreEqual($"2 backups, {Fmt.Bytes(4 * 1024 * 1024)}, no cap", BackupManagement.Summary(data));

        StringAssert.StartsWith(BackupManagement.Summary(Data(E("Universe-20261028-090000.tar.zst"))), "1 backup, ");

        var old = new BackupsDto { Backups = { E("Universe-20261028-090000.tar.zst", 500), E("Universe-20261027-090000.tar.zst", 500) } };
        Assert.AreEqual($"2 backups, {Fmt.Bytes(1000)}", BackupManagement.Summary(old), "no policy, no cap text");
        Assert.AreEqual(string.Empty, BackupManagement.Summary(null));
    }

    [TestMethod]
    public void RetentionLine_DescribesThePolicy()
    {
        var line = BackupManagement.RetentionLine(new BackupPolicyDto { KeepNewest = 10, KeepDailyDays = 14, KeepScreenshotArchives = 8, LegacyZipKeep = 0, MaxTotalMegabytes = 2048 });
        StringAssert.Contains(line, "newest 10 archives");
        StringAssert.Contains(line, "last 14 days");
        StringAssert.Contains(line, "8 screenshot archives");
        StringAssert.Contains(line, "not pruned automatically");
        StringAssert.Contains(line, "size cap 2048 MB");

        var other = BackupManagement.RetentionLine(new BackupPolicyDto { KeepNewest = 3, KeepDailyDays = 0, KeepScreenshotArchives = 1, LegacyZipKeep = 4, MaxTotalMegabytes = 0 });
        StringAssert.Contains(other, "newest 4 kept");
        StringAssert.Contains(other, "no size cap");
        StringAssert.Contains(BackupManagement.RetentionLine(null), "does not report");
    }

    [TestMethod]
    public void PlanDelete_SkipsTheProtectedNewestArchive_AndUnknownNames_AndAddsUpTheSize()
    {
        var data = Data(E("Universe-20261028-090000.tar.zst", 100), E("Universe-20261027-090000.tar.zst", 200), E("Universe-20260905-101500.zip", 400), E("odd.bin", 800));

        var plan = BackupManagement.PlanDelete(data, new[] { "Universe-20261028-090000.tar.zst", "Universe-20261027-090000.tar.zst", "Universe-20260905-101500.zip", "Universe-20260905-101500.zip", "odd.bin" });

        CollectionAssert.AreEqual(new[] { "Universe-20261027-090000.tar.zst", "Universe-20260905-101500.zip" }, plan.Names);
        CollectionAssert.AreEqual(new[] { "Universe-20261028-090000.tar.zst" }, plan.Protected);
        CollectionAssert.AreEqual(new[] { "odd.bin" }, plan.Unknown);
        Assert.AreEqual(600, plan.Bytes);
        Assert.IsFalse(plan.IsEmpty);
    }

    [TestMethod]
    public void PlanDelete_OnlyTheProtectedOne_IsEmpty_WithAnExplanation()
    {
        var data = Data(E("Universe-20261028-090000.tar.zst"), E("Universe-20261027-090000.tar.zst"));

        var plan = BackupManagement.PlanDelete(data, new[] { "Universe-20261028-090000.tar.zst" });
        Assert.IsTrue(plan.IsEmpty);
        StringAssert.Contains(BackupManagement.EmptyReason(plan, 1), "protected");
        StringAssert.Contains(BackupManagement.EmptyReason(BackupManagement.PlanDelete(data, new string[0]), 0), "Select one or more");
        Assert.AreEqual(1, BackupManagement.PlanDelete(null, new[] { "Universe-20261028-090000.tar.zst" }).Names.Count, "no list: nothing known to protect, the server still decides");
    }

    [TestMethod]
    public void DeleteConfirmText_ListsTheNamesTheTotalSizeAndTheProtectedNote()
    {
        var data = Data(E("Universe-20261028-090000.tar.zst", 100), E("Universe-20261027-090000.tar.zst", 1024 * 1024), E("Universe-20261026-090000.tar.zst", 1024 * 1024));
        var plan = BackupManagement.PlanDelete(data, data.Backups.Select(b => b.Name));

        var text = BackupManagement.DeleteConfirmText(plan);

        StringAssert.StartsWith(text, "Delete 2 backups (" + Fmt.Bytes(2 * 1024 * 1024) + ")");
        StringAssert.Contains(text, "  Universe-20261027-090000.tar.zst");
        StringAssert.Contains(text, "  Universe-20261026-090000.tar.zst");
        StringAssert.Contains(text, "Universe-20261028-090000.tar.zst  - newest backup, protected: kept");
        StringAssert.Contains(text, "cannot be undone");
    }

    [TestMethod]
    public void DeleteConfirmText_CutsALongListAfterTwelveNames()
    {
        var entries = Enumerable.Range(1, 20).Select(i => E($"Universe-202610{i:00}-090000.zip", 10)).ToArray();
        var data = Data(entries);
        var plan = BackupManagement.PlanDelete(data, entries.Select(e => e.Name));

        var text = BackupManagement.DeleteConfirmText(plan);

        Assert.AreEqual(BackupManagement.MaxListed, entries.Count(e => text.Contains(e.Name)));
        StringAssert.Contains(text, "... and 8 more");
        StringAssert.StartsWith(text, "Delete 20 backups");
    }

    [TestMethod]
    public void PruneConfirmText_ListsWhatGoesAndTheFreedSpace_AndNothingToPruneSaysSo()
    {
        var dry = new BackupPruneResultDto
        {
            DryRun = true,
            FreedBytes = 3 * 1024 * 1024,
            Deleted = { new PrunedFileDto { Name = "Universe-20260901-100000.zip", Bytes = 2 * 1024 * 1024, Reason = "legacy-zip-keep" }, new PrunedFileDto { Name = "Universe-20261001-090000.tar.zst", Bytes = 1024 * 1024, Reason = "size-cap" } },
            StillOverCap = true,
        };

        var text = BackupManagement.PruneConfirmText(dry, includeZips: true);
        StringAssert.Contains(text, "deletes 2 files and frees " + Fmt.Bytes(3 * 1024 * 1024));
        StringAssert.Contains(text, "Universe-20260901-100000.zip");
        StringAssert.Contains(text, "old .zip");
        StringAssert.Contains(text, "size cap");
        StringAssert.Contains(text, "old .zip backups");
        StringAssert.Contains(text, "stays above the cap");
        Assert.IsTrue(text.EndsWith("Delete them?"));

        Assert.IsFalse(BackupManagement.PruneConfirmText(dry, includeZips: false).Contains("also trims"));
        StringAssert.Contains(BackupManagement.NothingToPrune(new BackupPruneResultDto(), false), "Nothing to prune");
        StringAssert.Contains(BackupManagement.NothingToPrune(new BackupPruneResultDto { StillOverCap = true }, true), "above its cap");
    }

    [TestMethod]
    public void BackupsBody_ParsesTheNewFields_AndAnOldServerBodyHasNoPolicy()
    {
        var json = "{\"backups\":[{\"name\":\"Universe-20261028-090000.tar.zst\",\"bytes\":10,\"createdUtc\":\"2026-10-28T09:00:00Z\",\"kind\":\"tar.zst\"}],\"count\":1,\"totalBytes\":1234,\"capBytes\":2147483648,"
                   + "\"policy\":{\"keepNewest\":10,\"keepDailyDays\":14,\"keepScreenshotArchives\":10,\"legacyZipKeep\":2,\"maxTotalMegabytes\":2048}}";
        var dto = ApiJson.TryParse<BackupsDto>(json);
        Assert.AreEqual(1, dto.Count);
        Assert.AreEqual(1234, dto.TotalBytes);
        Assert.AreEqual(2147483648L, dto.CapBytes);
        Assert.AreEqual(2, dto.Policy.LegacyZipKeep);
        Assert.AreEqual(2048, dto.Policy.MaxTotalMegabytes);

        var old = ApiJson.TryParse<BackupsDto>("{\"backups\":[]}");
        Assert.IsNull(old.Policy);
        Assert.AreEqual(0, old.Count);
    }

    [TestMethod]
    public async Task Client_Delete_SendsDELETEWithTheEscapedName_AndMapsTheStatuses()
    {
        using var server = new ApiClientTests.FakeServer();
        using var client = new AdminApiClient(server.Port, Tokens());
        string method = null, path = null;
        server.Handler = (m, p) =>
        {
            method = m;
            path = p;
            return (200, "{\"name\":\"Universe-20261027-090000.tar.zst\",\"deleted\":[\"Universe-20261027-090000.tar.zst\",\"Universe-20261027-090000.tar.zst.sha256\"],\"freedBytes\":105}", 0);
        };

        var ok = await client.DeleteAsync<BackupDeleteResultDto>("backups/" + Uri.EscapeDataString("Universe-20261027-090000.tar.zst"), CancellationToken.None);

        Assert.IsTrue(ok.IsOk);
        Assert.AreEqual("DELETE", method);
        Assert.AreEqual("/api/v1/backups/Universe-20261027-090000.tar.zst", path);
        Assert.AreEqual("Bearer " + Token, server.LastAuth);
        Assert.AreEqual(string.Empty, server.LastBody, "no body");
        Assert.AreEqual(2, ok.Value.Deleted.Count);
        Assert.AreEqual(105, ok.Value.FreedBytes);

        server.Handler = (m, p) => (409, "{\"error\":\"this is the newest archive backup and is protected; add ?force=true to delete it anyway\"}", 0);
        var protectedNewest = await client.DeleteAsync<BackupDeleteResultDto>("backups/x", CancellationToken.None);
        Assert.AreEqual(ApiOutcome.Failed, protectedNewest.Outcome);
        Assert.AreEqual(409, protectedNewest.StatusCode);
        StringAssert.Contains(protectedNewest.Error, "protected");

        server.Handler = (m, p) => (404, "{\"error\":\"no such backup\"}", 0);
        var missing = await client.DeleteAsync<BackupDeleteResultDto>("backups/x", CancellationToken.None);
        Assert.AreEqual(ApiOutcome.Failed, missing.Outcome, "a missing file is a failure with the server's message, not 'update the server'");
        Assert.AreEqual("no such backup", missing.Error);

        server.Handler = (m, p) => (404, "{\"error\":\"not found\"}", 0);
        Assert.AreEqual(ApiOutcome.NotSupported, (await client.DeleteAsync<BackupDeleteResultDto>("backups/x", CancellationToken.None)).Outcome, "an older server without the call");

        server.Handler = (m, p) => (409, "{\"error\":\"a backup is running, try again when it has finished\"}", 0);
        StringAssert.Contains((await client.DeleteAsync<BackupDeleteResultDto>("backups/x", CancellationToken.None)).Error, "running");
    }

    [TestMethod]
    public async Task Client_Prune_PostsTheTwoFlags_AndParsesTheResult()
    {
        using var server = new ApiClientTests.FakeServer();
        using var client = new AdminApiClient(server.Port, Tokens());
        string method = null, path = null;
        server.Handler = (m, p) =>
        {
            method = m;
            path = p;
            return (200, "{\"deleted\":[{\"name\":\"Universe-20260901-100000.zip\",\"bytes\":7000000,\"reason\":\"legacy-zip-keep\"}],\"freedBytes\":7000000,\"dryRun\":true,\"totalBytesAfter\":100,\"capBytes\":0,\"stillOverCap\":false,\"failed\":[]}", 0);
        };

        var result = await client.PostAsync<BackupPruneResultDto>("backups/prune", new { dryRun = true, includeLegacyZips = true }, CancellationToken.None);

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual("POST", method);
        Assert.AreEqual("/api/v1/backups/prune", path);
        Assert.AreEqual("{\"dryRun\":true,\"includeLegacyZips\":true}", server.LastBody);
        Assert.IsTrue(result.Value.DryRun);
        Assert.AreEqual(1, result.Value.Deleted.Count);
        Assert.AreEqual("Universe-20260901-100000.zip", result.Value.Deleted[0].Name);
        Assert.AreEqual("legacy-zip-keep", result.Value.Deleted[0].Reason);
        Assert.AreEqual(7000000, result.Value.FreedBytes);
    }

    [TestMethod]
    public async Task Mock_ReportsPolicyCountAndCap_ProtectsTheNewest_AndPrunesInTwoSteps()
    {
        using var mock = new MockSession(null);
        var list = (await mock.GetBackupsAsync(CancellationToken.None)).Value;
        Assert.IsNotNull(list.Policy);
        Assert.AreEqual(list.Backups.Count, list.Count);
        Assert.AreEqual(2048L * 1024 * 1024, list.CapBytes);
        Assert.IsTrue(list.Backups.Any(b => b.Kind == "zip") && list.Backups.Any(b => b.Kind == "screenshots.tar"));
        StringAssert.Contains(BackupManagement.Summary(list), "of 2048 MB cap");

        var newest = BackupManagement.NewestArchive(list.Backups);
        var refused = await mock.DeleteBackupAsync(newest, CancellationToken.None);
        Assert.AreEqual(ApiOutcome.Failed, refused.Outcome);
        Assert.AreEqual(409, refused.StatusCode);

        var older = list.Backups.First(b => b.Kind == "tar.zst" && b.Name != newest).Name;
        Assert.IsTrue((await mock.DeleteBackupAsync(older, CancellationToken.None)).IsOk);
        Assert.IsFalse((await mock.GetBackupsAsync(CancellationToken.None)).Value.Backups.Any(b => b.Name == older));
        Assert.AreEqual(ApiOutcome.Failed, (await mock.DeleteBackupAsync(older, CancellationToken.None)).Outcome, "already gone");

        var before = (await mock.GetBackupsAsync(CancellationToken.None)).Value.Count;
        var dry = (await mock.PruneBackupsAsync(true, true, CancellationToken.None)).Value;
        Assert.IsTrue(dry.DryRun);
        Assert.IsTrue(dry.Deleted.Any(d => d.Reason == "legacy-zip-keep"));
        Assert.AreEqual(before, (await mock.GetBackupsAsync(CancellationToken.None)).Value.Count, "a dry run changes nothing");

        var real = (await mock.PruneBackupsAsync(false, true, CancellationToken.None)).Value;
        Assert.AreEqual(dry.Deleted.Count, real.Deleted.Count);
        Assert.AreEqual(before - real.Deleted.Count, (await mock.GetBackupsAsync(CancellationToken.None)).Value.Count);
        Assert.AreEqual(0, (await mock.PruneBackupsAsync(true, true, CancellationToken.None)).Value.Deleted.Count, "nothing left to prune");
    }
}
