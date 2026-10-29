using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

internal static class ZstdFixtures
{
    // GET network from a server with zstd message compression: avgLevel and savedBytes per row, plus the top-level compression block.
    public const string Network = "{\"startedUtc\":\"2026-10-29T03:00:00Z\",\"uptimeSeconds\":3600,\"windowsRecorded\":10,\"totals\":{\"sentBytes\":5000000,\"sentMessages\":900,\"receivedBytes\":2000000,\"receivedMessages\":700},"
        + "\"lastMinute\":{\"seconds\":60,\"snapshots\":[],\"messageCompression\":[{\"type\":\"KerbalInfo\",\"messages\":3,\"rawBytes\":9300,\"wireBytes\":3000,\"ratio\":3.1,\"codec\":\"zstd\",\"avgLevel\":9.0,\"savedBytes\":6300}]},"
        + "\"lastTenMinutes\":{\"seconds\":600,\"snapshots\":[],\"messageCompression\":[]},"
        + "\"sinceStart\":{\"seconds\":3600,\"snapshots\":[],\"messageCompression\":["
        + "{\"type\":\"VesselProtoMsgData\",\"messages\":410,\"rawBytes\":6150000,\"wireBytes\":980000,\"ratio\":6.28,\"codec\":\"zstd\",\"avgLevel\":9.0,\"savedBytes\":5170000},"
        + "{\"type\":\"ScenarioInfo\",\"messages\":12,\"rawBytes\":400000,\"wireBytes\":80000,\"ratio\":5.0,\"codec\":\"zstd\",\"avgLevel\":7.4,\"savedBytes\":320000}]},"
        + "\"snapshotCompression\":[],"
        + "\"messageCompression\":["
        + "{\"type\":\"VesselProtoMsgData\",\"messages\":410,\"rawBytes\":6150000,\"wireBytes\":980000,\"ratio\":6.28,\"codec\":\"zstd\",\"avgLevel\":9.0,\"savedBytes\":5170000},"
        + "{\"type\":\"ScenarioInfo\",\"messages\":12,\"rawBytes\":400000,\"wireBytes\":80000,\"ratio\":5.0,\"codec\":\"zstd\",\"avgLevel\":7.4,\"savedBytes\":320000}],"
        + "\"compression\":{\"mode\":\"Adaptive\",\"serverLevel\":9,\"effectiveLevelNow\":9,\"clientLevel\":5,\"cacheHits\":1840,\"cacheMisses\":1020,\"avgCompressMicrosPerKb\":96.5}}";

    // GET settings: the Compression section as the server describes it (trimmed to the five settings).
    public const string CompressionSection = """
    {"sections":[
     {"id":"compression","title":"Compression","settings":[
      {"key":"compression.Mode","label":"Mode","description":"How the server picks the zstd level.","type":"enum","value":"Adaptive","default":"Adaptive","min":null,"max":null,"maxLength":null,"options":["Adaptive","Fixed"],"restartRequired":false,"secret":false,"modified":false},
      {"key":"compression.ServerLevel","label":"Server Level","description":"Zstd level the server uses when Mode is Fixed.","type":"int","value":9,"default":9,"min":1,"max":19,"maxLength":null,"options":null,"restartRequired":false,"secret":false,"modified":false},
      {"key":"compression.ClientLevel","label":"Client Level","description":"Highest zstd level clients use for what they send (1-9).","type":"int","value":5,"default":5,"min":1,"max":9,"maxLength":null,"options":null,"restartRequired":false,"secret":false,"modified":false},
      {"key":"compression.MaxAdaptiveLevel","label":"Max Adaptive Level","description":"Highest level adaptive mode may use on the server (1-19).","type":"int","value":15,"default":15,"min":1,"max":19,"maxLength":null,"options":null,"restartRequired":false,"secret":false,"modified":false},
      {"key":"compression.MinSizeBytes","label":"Min Size Bytes","description":"Payloads smaller than this many bytes are sent without compression (0-65536).","type":"int","value":160,"default":160,"min":0,"max":65536,"maxLength":null,"options":null,"restartRequired":false,"secret":false,"modified":false}]}
    ]}
    """;
}

[TestClass]
public class ZstdCompressionDtoTests
{
    [TestMethod]
    public void TheNewRowFields_AreParsed_AtTopLevelAndInEveryWindow()
    {
        var n = ApiJson.TryParse<NetworkDto>(ZstdFixtures.Network);

        var vessel = n.MessageCompression[0];
        Assert.AreEqual("VesselProtoMsgData", vessel.Type);
        Assert.AreEqual("zstd", vessel.Codec);
        Assert.AreEqual(9.0, vessel.AvgLevel);
        Assert.AreEqual(5170000L, vessel.SavedBytes);
        Assert.AreEqual(5170000L, vessel.EffectiveSavedBytes);
        Assert.AreEqual(7.4, n.MessageCompression[1].AvgLevel);
        Assert.AreEqual(7.4, n.SinceStart.MessageCompression[1].AvgLevel);
        Assert.AreEqual(6300L, n.LastMinute.MessageCompression.Single().SavedBytes);
        Assert.AreEqual(9.0, n.LastMinute.MessageCompression.Single().AvgLevel);
    }

    [TestMethod]
    public void AnOlderServer_WithoutTheFields_GivesLevelZero_AndSavedFromRawMinusWire()
    {
        var n = ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network);

        var row = n.MessageCompression[0];
        Assert.AreEqual("quicklz", row.Codec);
        Assert.AreEqual(0.0, row.AvgLevel);
        Assert.IsNull(row.SavedBytes);
        Assert.AreEqual(1200000L, row.EffectiveSavedBytes);
        Assert.AreEqual(2000L, n.MessageCompression[1].EffectiveSavedBytes, "wire 0 and raw 2000");
        Assert.AreEqual(0L, new MessageCompressionDto().EffectiveSavedBytes);

        var small = ApiJson.TryParse<MessageCompressionDto>("{\"rawBytes\":100,\"wireBytes\":250}");
        Assert.AreEqual(0L, small.EffectiveSavedBytes, "wire larger than raw is never a negative saving");
    }

    [TestMethod]
    public void TheServersSavedBytes_WinOverTheComputation_AndNeverGoNegative()
    {
        Assert.AreEqual(7L, ApiJson.TryParse<MessageCompressionDto>("{\"rawBytes\":1000,\"wireBytes\":250,\"savedBytes\":7}").EffectiveSavedBytes);
        Assert.AreEqual(0L, ApiJson.TryParse<MessageCompressionDto>("{\"rawBytes\":1000,\"wireBytes\":250,\"savedBytes\":-5}").EffectiveSavedBytes);
        Assert.AreEqual(0L, ApiJson.TryParse<MessageCompressionDto>("{\"rawBytes\":1000,\"wireBytes\":250,\"savedBytes\":0}").EffectiveSavedBytes, "an explicit 0 is not 'absent'");
        Assert.AreEqual(8.0, ApiJson.TryParse<MessageCompressionDto>("{\"avgLevel\":\"8\"}").AvgLevel, "numbers as text are accepted like everywhere else");
    }

    [TestMethod]
    public void TheCompressionBlock_IsParsed()
    {
        var c = ApiJson.TryParse<NetworkDto>(ZstdFixtures.Network).Compression;

        Assert.IsNotNull(c);
        Assert.AreEqual("Adaptive", c.Mode);
        Assert.AreEqual(9, c.ServerLevel);
        Assert.AreEqual(9, c.EffectiveLevelNow);
        Assert.AreEqual(5, c.ClientLevel);
        Assert.AreEqual(1840L, c.CacheHits);
        Assert.AreEqual(1020L, c.CacheMisses);
        Assert.AreEqual(96.5, c.AvgCompressMicrosPerKb);
    }

    [TestMethod]
    public void WithoutTheBlock_TheNetworkDtoHasNullCompression()
    {
        Assert.IsNull(ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network).Compression);
        Assert.IsNull(ApiJson.TryParse<NetworkDto>(Fixtures.NetworkFresh).Compression);
        Assert.IsNull(ApiJson.TryParse<NetworkDto>(Fixtures.NetworkBusy).Compression);
        Assert.IsNull(ApiJson.TryParse<NetworkDto>("{\"compression\":null}").Compression);

        var partial = ApiJson.TryParse<NetworkDto>("{\"compression\":{\"mode\":\"Fixed\"}}").Compression;
        Assert.AreEqual("Fixed", partial.Mode);
        Assert.AreEqual(0, partial.EffectiveLevelNow);
        Assert.AreEqual(0L, partial.CacheHits);
    }

    [TestMethod]
    public void TheRows_ShowTheLevelWithOneDecimal_AndTheSavedBytes()
    {
        var n = ApiJson.TryParse<NetworkDto>(ZstdFixtures.Network);

        var rows = MessageCompressionView.Rows(n.MessageCompression);

        CollectionAssert.AreEqual(new object[] { "VesselProtoMsgData", "410", "5.9 MB", "957.0 KB", "4.9 MB", "6.28x", "zstd", "9.0" }, rows[0]);
        CollectionAssert.AreEqual(new object[] { "ScenarioInfo", "12", Fmt.Bytes(400000), Fmt.Bytes(80000), Fmt.Bytes(320000), "5.0x", "zstd", "7.4" }, rows[1]);
        Assert.AreEqual("8.0", MessageCompressionView.Level(7.96));
        Assert.AreEqual("10.5", MessageCompressionView.Level(10.5));
        Assert.AreEqual("-", MessageCompressionView.Level(0));
        Assert.AreEqual("-", MessageCompressionView.Level(-1));
        Assert.AreEqual("-", MessageCompressionView.Level(double.NaN));
        Assert.AreEqual("-", MessageCompressionView.Level(double.PositiveInfinity));
        Assert.AreEqual(8, MessageCompressionView.EmptyRow().Length);
        Assert.AreEqual(MessageCompressionView.EmptyText, MessageCompressionView.EmptyRow()[0]);
    }
}

[TestClass]
public class CompressionSummaryTests
{
    private static CompressionDto Block(string mode = "Adaptive", int level = 9, long hits = 1840, long misses = 1020) =>
        new() { Mode = mode, ServerLevel = 9, EffectiveLevelNow = level, ClientLevel = 5, CacheHits = hits, CacheMisses = misses, AvgCompressMicrosPerKb = 96.5 };

    private static MessageCompressionDto Row(long saved) => new() { Type = "T", RawBytes = saved + 1000, WireBytes = 1000, SavedBytes = saved };

    [TestMethod]
    public void Adaptive_ShowsTheLevelNow_TheSavedBytesAndTheHitRate()
    {
        var text = MessageCompressionView.Summary(Block(), new[] { Row(12_897_485) });

        Assert.AreEqual("Compression: Adaptive, level 9 now, saved 12.3 MB, cache hit rate 64%", text);
    }

    [TestMethod]
    public void Fixed_ShowsTheLevelWithoutNow()
    {
        Assert.AreEqual("Compression: Fixed, level 12, saved 0 B, cache hit rate 50%", MessageCompressionView.Summary(Block("Fixed", 12, 5, 5), Array.Empty<MessageCompressionDto>()));
        Assert.AreEqual("Compression: fixed, level 3, saved 1.0 KB, cache hit rate 100%", MessageCompressionView.Summary(Block("fixed", 3, 7, 0), new[] { Row(1024) }));
    }

    [TestMethod]
    public void NoCacheLookups_SaysSo_AndNeverDividesByZero()
    {
        Assert.AreEqual("Compression: Adaptive, level 9 now, saved 0 B, no cache lookups yet", MessageCompressionView.Summary(Block(hits: 0, misses: 0), null));
        Assert.AreEqual("no cache lookups yet", MessageCompressionView.CacheText(0, 0));
        Assert.AreEqual("no cache lookups yet", MessageCompressionView.CacheText(-3, -4), "negative counts are treated as none");
    }

    [DataTestMethod]
    [DataRow(1840L, 1020L, "cache hit rate 64%")]
    [DataRow(0L, 10L, "cache hit rate 0%")]
    [DataRow(10L, 0L, "cache hit rate 100%")]
    [DataRow(1L, 1L, "cache hit rate 50%")]
    [DataRow(1L, 7L, "cache hit rate 13%")]
    [DataRow(2L, 1L, "cache hit rate 67%")]
    [DataRow(long.MaxValue / 4, long.MaxValue / 4, "cache hit rate 50%")]
    public void TheHitRate_IsHitsOverLookups_RoundedToAWholePercent(long hits, long misses, string expected)
    {
        Assert.AreEqual(expected, MessageCompressionView.CacheText(hits, misses));
    }

    [TestMethod]
    public void ANullBlock_GivesThePlainNote()
    {
        Assert.AreEqual("Compression: no information from this server", MessageCompressionView.Summary(null, new[] { Row(5) }));
        Assert.AreEqual(MessageCompressionView.NoInformationText, MessageCompressionView.Summary(null, null));
        Assert.AreEqual(MessageCompressionView.NoInformationText, MessageCompressionView.Summary(ApiJson.TryParse<NetworkDto>(CompressionFixtures.Network).Compression, null));
    }

    [TestMethod]
    public void LargeByteCounts_UseTheAppsFormatter()
    {
        const long tb = 1024L * 1024 * 1024 * 1024;

        Assert.AreEqual("Compression: Adaptive, level 9 now, saved 3.5 TB, cache hit rate 64%", MessageCompressionView.Summary(Block(), new[] { Row(3 * tb + tb / 2) }));
        Assert.AreEqual("Compression: Adaptive, level 9 now, saved 6.0 TB, cache hit rate 64%", MessageCompressionView.Summary(Block(), new[] { Row(3 * tb), Row(3 * tb) }), "the rows are added up");
        Assert.AreEqual("Compression: Adaptive, level 9 now, saved 2.0 GB, cache hit rate 64%", MessageCompressionView.Summary(Block(), new[] { Row(2L * 1024 * 1024 * 1024) }));
    }

    [TestMethod]
    public void MissingPieces_AreLeftOutNotShownAsZero()
    {
        Assert.AreEqual("Compression: level 9 now, saved 0 B, no cache lookups yet", MessageCompressionView.Summary(new CompressionDto { EffectiveLevelNow = 9 }, null));
        Assert.AreEqual("Compression: Adaptive, saved 0 B, no cache lookups yet", MessageCompressionView.Summary(new CompressionDto { Mode = " Adaptive " }, null));
    }

    [TestMethod]
    public void SavedBytes_AreSummedOverTheSinceStartRows_IncludingOlderRowsWithoutTheField()
    {
        var n = ApiJson.TryParse<NetworkDto>(ZstdFixtures.Network);

        var text = MessageCompressionView.Summary(n.Compression, MessageCompressionView.Pick(n, n.SinceStart));

        Assert.AreEqual("Compression: Adaptive, level 9 now, saved " + Fmt.Bytes(5170000 + 320000) + ", cache hit rate 64%", text);

        var mixed = new[] { new MessageCompressionDto { RawBytes = 3000, WireBytes = 1000 }, new MessageCompressionDto { RawBytes = 9, WireBytes = 9, SavedBytes = 4096 } };
        Assert.AreEqual("Compression: Adaptive, level 9 now, saved 6.0 KB, cache hit rate 64%", MessageCompressionView.Summary(Block(), mixed));
    }
}

[TestClass]
public class ZstdMockAndSettingsTests
{
    [TestMethod]
    public async Task TheMockNetwork_ShowsZstd_WithLevelsAndSavedBytes_AndTheCompressionBlock()
    {
        using var mock = new MockSession(null);
        var n = (await mock.GetNetworkAsync(CancellationToken.None)).Value;
        var levels = new Dictionary<string, double> { ["VesselProtoMsgData"] = 9.0, ["ScenarioInfo"] = 7.4, ["CraftInfo"] = 9.0, ["ContractInfo"] = 8.2, ["KerbalInfo"] = 9.0 };

        foreach (var list in new[] { n.MessageCompression, n.LastMinute.MessageCompression, n.LastTenMinutes.MessageCompression, n.SinceStart.MessageCompression })
        {
            foreach (var m in list)
            {
                Assert.AreEqual("zstd", m.Codec, m.Type);
                Assert.AreEqual(levels[m.Type], m.AvgLevel, m.Type);
                Assert.AreEqual(m.RawBytes - m.WireBytes, m.SavedBytes, m.Type);
                Assert.IsTrue(m.SavedBytes > 0, m.Type);
            }
        }

        var c = n.Compression;
        Assert.IsNotNull(c);
        Assert.AreEqual("Adaptive", c.Mode);
        Assert.AreEqual(9, c.ServerLevel);
        Assert.AreEqual(9, c.EffectiveLevelNow);
        Assert.AreEqual(5, c.ClientLevel);
        Assert.AreEqual(1840L, c.CacheHits);
        Assert.AreEqual(1020L, c.CacheMisses);
        Assert.AreEqual(96.5, c.AvgCompressMicrosPerKb);

        var text = MessageCompressionView.Summary(c, MessageCompressionView.Pick(n, n.SinceStart));
        var saved = n.SinceStart.MessageCompression.Sum(m => m.RawBytes - m.WireBytes);
        Assert.AreEqual("Compression: Adaptive, level 9 now, saved " + Fmt.Bytes(saved) + ", cache hit rate 64%", text);
    }

    [TestMethod]
    public void TheRealSectionDescription_ParsesWithLimitsOptionsAndLiveFlags()
    {
        var schema = ApiJson.TryParse<SettingsSchemaDto>(ZstdFixtures.CompressionSection);

        var section = schema.Sections.Single();
        Assert.AreEqual("compression", section.Id);
        Assert.AreEqual("Compression", section.Title);
        CollectionAssert.AreEqual(new[] { "compression.Mode", "compression.ServerLevel", "compression.ClientLevel", "compression.MaxAdaptiveLevel", "compression.MinSizeBytes" }, section.Settings.Select(s => s.Key).ToArray());
        var all = section.Settings.ToDictionary(s => s.Key);
        Assert.AreEqual("enum", all["compression.Mode"].Type);
        CollectionAssert.AreEqual(new[] { "Adaptive", "Fixed" }, all["compression.Mode"].Options);
        Assert.AreEqual("Adaptive", all["compression.Mode"].DefaultValue());
        Assert.AreEqual(1.0, all["compression.ServerLevel"].Min);
        Assert.AreEqual(19.0, all["compression.ServerLevel"].Max);
        Assert.AreEqual(9.0, all["compression.ClientLevel"].Max);
        Assert.AreEqual(15L, all["compression.MaxAdaptiveLevel"].ServerValue());
        Assert.AreEqual(0.0, all["compression.MinSizeBytes"].Min);
        Assert.AreEqual(65536.0, all["compression.MinSizeBytes"].Max);
        Assert.AreEqual(160L, all["compression.MinSizeBytes"].DefaultValue());
        Assert.IsTrue(all.Values.All(s => !s.RestartRequired && !s.Secret), "all five apply immediately");

        var model = new SettingsEditModel();
        model.Load(schema);
        Assert.IsNotNull(model.Find("compression.Mode"));
        Assert.IsFalse(SettingsEditModel.IsReadOnly(model.Find("compression.ServerLevel")));
    }

    [TestMethod]
    public async Task TheMockSchema_HasTheCompressionSection_LikeTheServer()
    {
        using var mock = new MockSession(null);
        var schema = (await mock.GetSettingsAsync(CancellationToken.None)).Value;

        var section = schema.Sections.Single(s => s.Id == "compression");
        Assert.AreEqual("Compression", section.Title);
        Assert.AreEqual("adminApi", schema.Sections.Last().Id, "the read-only section stays last");
        var all = section.Settings.ToDictionary(s => s.Key);
        CollectionAssert.AreEqual(new[] { "compression.Mode", "compression.ServerLevel", "compression.ClientLevel", "compression.MaxAdaptiveLevel", "compression.MinSizeBytes" }, section.Settings.Select(s => s.Key).ToArray());
        Assert.AreEqual("Mode", all["compression.Mode"].Label);
        Assert.AreEqual("enum", all["compression.Mode"].Type);
        CollectionAssert.AreEqual(new[] { "Adaptive", "Fixed" }, all["compression.Mode"].Options);
        Assert.AreEqual("Adaptive", all["compression.Mode"].ServerValue());
        Assert.AreEqual((1.0, 19.0, 9L), (all["compression.ServerLevel"].Min.Value, all["compression.ServerLevel"].Max.Value, (long)all["compression.ServerLevel"].ServerValue()));
        Assert.AreEqual((1.0, 9.0, 5L), (all["compression.ClientLevel"].Min.Value, all["compression.ClientLevel"].Max.Value, (long)all["compression.ClientLevel"].ServerValue()));
        Assert.AreEqual((1.0, 19.0, 15L), (all["compression.MaxAdaptiveLevel"].Min.Value, all["compression.MaxAdaptiveLevel"].Max.Value, (long)all["compression.MaxAdaptiveLevel"].ServerValue()));
        Assert.AreEqual((0.0, 65536.0, 160L), (all["compression.MinSizeBytes"].Min.Value, all["compression.MinSizeBytes"].Max.Value, (long)all["compression.MinSizeBytes"].ServerValue()));
        Assert.IsTrue(all.Values.All(s => !s.RestartRequired && !s.Modified && s.Description.Length > 20));
        Assert.IsTrue(all["compression.ClientLevel"].Description.Contains("never above 3 for payloads over 4 MB"));
    }

    [TestMethod]
    public async Task TheMockServer_ValidatesTheCompressionSettings_AndAppliesThemLive()
    {
        using var mock = new MockSession(null);

        var bad = await mock.PutSettingsAsync(new Dictionary<string, object> { ["compression.ClientLevel"] = 10L, ["compression.ServerLevel"] = 0L, ["compression.Mode"] = "Turbo", ["compression.MinSizeBytes"] = 70000L }, CancellationToken.None);
        Assert.AreEqual(400, bad.StatusCode);
        Assert.AreEqual(4, new SettingsEditModel().ApplyServerErrors(bad.Body));

        var good = await mock.PutSettingsAsync(new Dictionary<string, object> { ["compression.Mode"] = "Fixed", ["compression.ServerLevel"] = 12L, ["compression.ClientLevel"] = 9L }, CancellationToken.None);
        Assert.IsTrue(good.IsOk);
        CollectionAssert.AreEquivalent(new[] { "compression.Mode", "compression.ServerLevel", "compression.ClientLevel" }, good.Value.Applied);
        Assert.AreEqual(0, good.Value.RestartRequired.Count, "compression settings need no restart");
        var after = (await mock.GetSettingsAsync(CancellationToken.None)).Value.Sections.Single(s => s.Id == "compression").Settings.ToDictionary(s => s.Key);
        Assert.AreEqual("Fixed", after["compression.Mode"].ServerValue());
        Assert.IsTrue(after["compression.Mode"].Modified);
        Assert.AreEqual(12L, after["compression.ServerLevel"].ServerValue());
        Assert.IsFalse(after["compression.MaxAdaptiveLevel"].Modified);
    }
}
