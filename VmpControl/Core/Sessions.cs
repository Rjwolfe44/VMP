namespace VmpControl.Core;

/// <summary>Everything the window needs from one server connection. <see cref="LiveSession"/> talks to a real server, <see cref="MockSession"/> returns sample data.</summary>
public interface IServerSession : IDisposable
{
    Profile Profile { get; }
    bool IsMock { get; }
    ActivityLog Log { get; }
    Task<PollSnapshot> PollAsync(bool windowHidden, CancellationToken ct);
    Task<ApiResult<NetworkDto>> GetNetworkAsync(CancellationToken ct);
    Task<ApiResult<LogDto>> GetLogAsync(int lines, CancellationToken ct);
    Task<ApiResult<ReportsDto>> GetReportsAsync(CancellationToken ct);
    Task<ApiResult<BackupsDto>> GetBackupsAsync(CancellationToken ct);
    Task<ApiResult<BackupResultDto>> CreateBackupAsync(CancellationToken ct);
    /// <summary>Deletes one backup file (DELETE backups/name); the server refuses the newest archive (409) and unknown names (400).</summary>
    Task<ApiResult<BackupDeleteResultDto>> DeleteBackupAsync(string name, CancellationToken ct);
    /// <summary>Applies the retention settings now (POST backups/prune); with dryRun it only reports what it would delete.</summary>
    Task<ApiResult<BackupPruneResultDto>> PruneBackupsAsync(bool dryRun, bool includeLegacyZips, CancellationToken ct);
    Task<ApiResult<OkDto>> SayAsync(string text, CancellationToken ct);
    Task<ApiResult<OkDto>> KickAsync(string name, string reason, CancellationToken ct);
    /// <summary>Asks a connected player's game to send its logs to the server (<c>POST clients/requestlogs</c>). The note is optional, at most 120 characters.</summary>
    Task<ApiResult<RequestLogsResultDto>> RequestLogsAsync(string name, string note, CancellationToken ct);
    Task<ApiResult<SettingsSchemaDto>> GetSettingsAsync(CancellationToken ct);
    /// <summary>One request with only the changed keys; the server validates everything and applies all or nothing.</summary>
    Task<ApiResult<SettingsSaveResultDto>> PutSettingsAsync(IReadOnlyDictionary<string, object> changes, CancellationToken ct);
    /// <summary>Runs the control script (Start, Stop, Restart, Backup, Status). The caller has already asked the user.</summary>
    Task<ProcResult> RunControlAsync(string action, CancellationToken ct);
    Task<ProcResult> RestartPcAsync(CancellationToken ct);
    Task<ProcResult> AbortRestartPcAsync(CancellationToken ct);
}

public sealed class LiveSession : IServerSession
{
    private readonly TokenSource _tokens;
    private readonly AdminApiClient _api;
    private readonly TunnelManager _tunnel;
    private ScriptStatus _script;
    private bool _lastApiOk;

    public Profile Profile { get; }
    public bool IsMock => false;
    public ActivityLog Log { get; } = new();

    public LiveSession(Profile profile)
    {
        var problem = profile.Validate();
        if (problem != null) throw new ArgumentException(problem);
        Profile = profile;
        _tokens = profile.IsRemote ? TokenSource.ForSsh(profile.SshAlias, profile.RemoteServerRoot) : TokenSource.ForFile(profile.ServerFolder);
        _tokens.Acquired += Log.SetSecret;
        _api = new AdminApiClient(profile.LocalApiPort, _tokens);
        if (profile.IsRemote)
        {
            _tunnel = new TunnelManager(profile.SshAlias, profile.LocalTunnelPort, profile.RemoteApiPort);
            _tunnel.Start();
        }
    }

    private string ControlScript => Profile.IsRemote ? Profile.ControlScriptPath : Path.Combine(Profile.ServerFolder, "Manage-VmpServer.ps1");

    /// <summary>
    /// Arguments for the control script. Host mode adds <c>-ServerRoot &lt;server folder&gt;</c>, so Manage-VmpServer.ps1 acts on the folder of the profile
    /// and not on its own default location. Remote mode adds <c>-Yes</c> (this app has already asked about players) and no server root.
    /// </summary>
    internal static string[] ControlArgs(Profile profile, string script, string action)
    {
        return profile.IsRemote
            ? SshCommands.ScriptArgs(script, action, yes: true)
            : SshCommands.ScriptArgs(script, action, yes: false, serverRoot: profile.ServerFolder);
    }

    public async Task<PollSnapshot> PollAsync(bool windowHidden, CancellationToken ct)
    {
        var snapshot = new PollSnapshot { ProfileName = Profile.Name, TunnelStatus = _tunnel?.Status };
        try
        {
            var status = await _api.GetAsync<StatusDto>("status", ct);
            if (status.IsOk)
            {
                var clients = await _api.GetAsync<ClientsDto>("clients", ct);
                snapshot.Api = ApiAvailability.Ok;
                snapshot.Status = status.Value;
                snapshot.Clients = clients.Value?.Clients ?? new List<ClientDto>();
                _lastApiOk = true;
                return snapshot;
            }

            snapshot.Api = status.Outcome switch
            {
                ApiOutcome.NotSupported => ApiAvailability.NotSupported,
                ApiOutcome.Unauthorized => ApiAvailability.Unauthorized,
                _ => ApiAvailability.Unreachable,
            };
            snapshot.ApiDetail = status.Error;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            snapshot.Api = ApiAvailability.Unreachable;
            snapshot.ApiDetail = e.Message;
        }

        // The API is not answering: ask the control script, right away when it has just stopped answering, otherwise now and then.
        var age = _script == null ? TimeSpan.MaxValue : DateTime.UtcNow - _script.AtUtc;
        if (_lastApiOk || age > TimeSpan.FromSeconds(windowHidden ? 60 : 20))
        {
            _script = await ReadScriptStatusAsync(ct);
        }

        _lastApiOk = false;
        snapshot.Script = _script;
        snapshot.TunnelStatus = _tunnel?.Status;
        return snapshot;
    }

    private async Task<ScriptStatus> ReadScriptStatusAsync(CancellationToken ct)
    {
        var path = ControlScript;
        if (!File.Exists(path)) return new ScriptStatus { Text = "control script not found: " + path };
        var result = await ProcessRunner.RunAsync(ProcessRunner.PowerShellPath(), ControlArgs(Profile, path, "Status"), TimeSpan.FromSeconds(45), ct);
        if (!result.Ok) return new ScriptStatus { Text = result.TimedOut ? "the control script timed out" : (result.StartError ?? result.LastErrorLine() + " " + result.Output).Trim() };
        return ControlOutputParser.ParseStatus(result.Output);
    }

    public Task<ApiResult<NetworkDto>> GetNetworkAsync(CancellationToken ct) => _api.GetAsync<NetworkDto>("network", ct, 5);
    public Task<ApiResult<LogDto>> GetLogAsync(int lines, CancellationToken ct) => _api.GetAsync<LogDto>("log?lines=" + Math.Clamp(lines, 1, 500), ct, 5);
    public Task<ApiResult<ReportsDto>> GetReportsAsync(CancellationToken ct) => _api.GetAsync<ReportsDto>("reports", ct, 5);
    public Task<ApiResult<BackupsDto>> GetBackupsAsync(CancellationToken ct) => _api.GetAsync<BackupsDto>("backups", ct, 5);
    public Task<ApiResult<BackupResultDto>> CreateBackupAsync(CancellationToken ct) => _api.PostAsync<BackupResultDto>("backups", null, ct, 900);
    public Task<ApiResult<BackupDeleteResultDto>> DeleteBackupAsync(string name, CancellationToken ct) => _api.DeleteAsync<BackupDeleteResultDto>("backups/" + Uri.EscapeDataString(name ?? string.Empty), ct, 30);
    public Task<ApiResult<BackupPruneResultDto>> PruneBackupsAsync(bool dryRun, bool includeLegacyZips, CancellationToken ct) => _api.PostAsync<BackupPruneResultDto>("backups/prune", new { dryRun, includeLegacyZips }, ct, 120);
    public Task<ApiResult<OkDto>> SayAsync(string text, CancellationToken ct) => _api.PostAsync<OkDto>("say", new { text }, ct, 10);
    public Task<ApiResult<OkDto>> KickAsync(string name, string reason, CancellationToken ct) => _api.PostAsync<OkDto>("kick", new { name, reason }, ct, 10);
    public Task<ApiResult<RequestLogsResultDto>> RequestLogsAsync(string name, string note, CancellationToken ct)
        => _api.PostAsync<RequestLogsResultDto>("clients/requestlogs", RequestLogsBody.Create(name, note), ct, 10);
    public Task<ApiResult<SettingsSchemaDto>> GetSettingsAsync(CancellationToken ct) => _api.GetAsync<SettingsSchemaDto>("settings", ct, 8);
    public Task<ApiResult<SettingsSaveResultDto>> PutSettingsAsync(IReadOnlyDictionary<string, object> changes, CancellationToken ct)
        => _api.PutAsync<SettingsSaveResultDto>("settings", new { changes }, ct, 15);

    public async Task<ProcResult> RunControlAsync(string action, CancellationToken ct)
    {
        var path = ControlScript;
        if (!File.Exists(path)) return new ProcResult(-1, "", "", false, "control script not found: " + path);
        var timeout = action switch
        {
            "Status" => TimeSpan.FromSeconds(60),
            "Backup" => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(3),
        };
        // The remote script asks "players are online, continue?" by itself unless it gets -Yes; this app has already asked.
        var result = await ProcessRunner.RunAsync(ProcessRunner.PowerShellPath(), ControlArgs(Profile, path, action), timeout, ct);
        _script = null;
        return result;
    }

    public Task<ProcResult> RestartPcAsync(CancellationToken ct) => RemoteOnly(SshCommands.ShutdownRestartArgs, ct);

    public Task<ProcResult> AbortRestartPcAsync(CancellationToken ct) => RemoteOnly(SshCommands.ShutdownAbortArgs, ct);

    private Task<ProcResult> RemoteOnly(Func<string, string[]> args, CancellationToken ct)
    {
        if (!Profile.IsRemote) return Task.FromResult(new ProcResult(-1, "", "", false, "restarting the PC is only available in remote mode"));
        return ProcessRunner.RunAsync(ProcessRunner.SshPath(), args(Profile.SshAlias), TimeSpan.FromSeconds(30), ct);
    }

    public void Dispose()
    {
        _tunnel?.Dispose();
        _api.Dispose();
    }
}

/// <summary>Sample data with a few moving numbers, for --mock, screenshots and trying the layout without a server.</summary>
public sealed class MockSession : IServerSession
{
    private readonly DateTime _start = DateTime.UtcNow.AddHours(-3).AddMinutes(-12);
    private readonly Random _random = new(7);
    private readonly List<BackupEntryDto> _backups;

    public Profile Profile { get; }
    public bool IsMock => true;
    public ActivityLog Log { get; } = new();

    public MockSession(Profile profile)
    {
        Profile = profile ?? Profile_Default();
        var now = DateTime.UtcNow;
        _backups = new List<BackupEntryDto>();
        for (var i = 0; i < 12; i++)
        {
            var time = now.AddHours(-3 - 12 * i);
            _backups.Add(new BackupEntryDto { Name = "Universe-" + time.ToString("yyyyMMdd-HHmmss") + ".tar.zst", Bytes = 6_420_113 - i * 31_000, CreatedUtc = time, Kind = "tar.zst" });
        }

        for (var i = 0; i < 6; i++)
        {
            var time = now.AddDays(-20 - i);
            _backups.Add(new BackupEntryDto { Name = "Universe-" + time.ToString("yyyyMMdd-HHmmss") + ".zip", Bytes = 7_100_000 + i * 410_000, CreatedUtc = time, Kind = "zip" });
        }

        for (var i = 0; i < 2; i++)
        {
            var time = now.AddDays(-1 - 3 * i);
            _backups.Add(new BackupEntryDto { Name = "Screenshots-" + time.ToString("yyyyMMdd-HHmmss") + ".tar", Bytes = 48_300_000 - i * 9_000_000, CreatedUtc = time, Kind = "screenshots.tar" });
        }
    }

    private static Profile Profile_Default() => new() { Name = "Mock server" };

    private double Wobble(double baseValue, double spread) => baseValue + (_random.NextDouble() - 0.5) * 2 * spread;

    public Task<PollSnapshot> PollAsync(bool windowHidden, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var players = new List<PlayerDto>
        {
            new() { Name = "Vlad", Subspace = 3, ConnectedSeconds = 8040, ConnectedSinceUtc = now.AddSeconds(-8040) },
            new() { Name = "Jebediah", Subspace = 3, ConnectedSeconds = 2810, ConnectedSinceUtc = now.AddSeconds(-2810) },
            new() { Name = "Valentina", Subspace = 5, ConnectedSeconds = 745, ConnectedSinceUtc = now.AddSeconds(-745) },
        };
        var status = new StatusDto
        {
            Version = "0.2.0-train.20261028.1",
            StartedUtc = _start,
            UptimeSeconds = (now - _start).TotalSeconds,
            WorkingSetBytes = (long)Wobble(640_000_000, 6_000_000),
            CpuPercent = Math.Round(Wobble(7.5, 2), 1),
            PlayersOnline = players.Count,
            Players = players,
            VesselCount = 148,
            UniverseBytes = 38_412_980,
            Settings = new SettingsHighlightsDto { ServerName = "VMP Train Server", GameMode = "Career", WarpMode = "Subspace", MaxPlayers = 20, GamePort = 8800, Cheats = false },
            Machine = new MachineDto
            {
                RamTotalBytes = 17_071_734_784,
                RamFreeBytes = 5_480_000_000,
                OsUptimeSeconds = 4 * 86400 + 7 * 3600 + 1234,
                DiskFreeBytes = 227_000_000_000,
                DiskTotalBytes = 999_000_000_000,
                DiskRoot = @"C:\",
            },
        };
        var snapshot = new PollSnapshot
        {
            ProfileName = Profile.Name,
            Api = ApiAvailability.Ok,
            Status = status,
            Clients = players.Select((p, i) => new ClientDto
            {
                Name = p.Name,
                Authenticated = true,
                Subspace = p.Subspace,
                ConnectedSeconds = p.ConnectedSeconds,
                ConnectedSinceUtc = p.ConnectedSinceUtc,
                BytesIn = 41_200_000 - i * 9_000_000,
                BytesOut = 118_700_000 - i * 31_000_000,
                WireBytesIn = 44_900_000 - i * 9_500_000,
                WireBytesOut = 131_000_000 - i * 33_000_000,
                Build = i == 2 ? "0.2.0-train.20261026.4" : "0.2.0-train.20261028.1",
                LatencyMs = Math.Round(Wobble(38 + i * 21, 4), 1),
            }).ToList(),
        };
        return Task.FromResult(snapshot);
    }

    private static readonly (string Name, long Out, long In, long OutMsgs, long InMsgs)[] Types =
    {
        ("VesselUpdateMsg", 9_100_000, 5_900_000, 22_400, 15_100),
        ("VesselProtoMsg", 6_800_000, 3_100_000, 310, 140),
        ("VesselsReplyMsg", 4_200_000, 0, 96, 0),
        ("ScenarioDataMsg", 1_450_000, 220_000, 64, 11),
        ("WarpChangeSubspaceMsg", 310_000, 88_000, 880, 240),
        ("PlayerStatusMsg", 150_000, 41_000, 1_200, 330),
        ("FlagSyncMsg", 96_000, 12_000, 40, 6),
        ("ChatMsg", 22_000, 6_000, 120, 31),
        ("HeartbeatMsg", 9_800, 9_800, 700, 700),
    };

    private TrafficWindowDto Window(double seconds)
    {
        double scale = seconds / 600.0;
        var window = new TrafficWindowDto { StartUtc = DateTime.UtcNow.AddSeconds(-seconds), EndUtc = DateTime.UtcNow, Seconds = seconds, Out = new(), In = new() };
        foreach (var (name, o, i, om, im) in Types)
        {
            var wobble = Wobble(1, 0.04);
            if (o > 0) window.Out.Types.Add(new TrafficTypeDto { Type = name, Bytes = (long)(o * scale * wobble), Messages = (long)(om * scale), BytesPerSecond = Math.Round(o * scale * wobble / seconds, 1) });
            if (i > 0) window.In.Types.Add(new TrafficTypeDto { Type = name, Bytes = (long)(i * scale * wobble), Messages = (long)(im * scale), BytesPerSecond = Math.Round(i * scale * wobble / seconds, 1) });
        }

        foreach (var (dir, rows) in new[] { (window.Out, window.Out.Types), (window.In, window.In.Types) })
        {
            dir.Bytes = rows.Sum(t => t.Bytes);
            dir.Messages = rows.Sum(t => t.Messages);
            dir.BytesPerSecond = Math.Round(dir.Bytes / seconds, 1);
        }

        window.Snapshots = new List<SnapshotCompressionDto>
        {
            new() { Domain = "Vessel", Snapshots = (long)(310 * scale), RawBytes = (long)(21_400_000 * scale), WireBytes = (long)(6_800_000 * scale), Ratio = 3.15, Encodings = new EncodingCountsDto { ZstdDictionary = (long)(280 * scale), Zstd = (long)(24 * scale), Raw = (long)(6 * scale) } },
            new() { Domain = "Scenario", Snapshots = (long)(64 * scale), RawBytes = (long)(5_900_000 * scale), WireBytes = (long)(1_450_000 * scale), Ratio = 4.07, Encodings = new EncodingCountsDto { Zstd = (long)(64 * scale) } },
            new() { Domain = "Kerbal", Snapshots = (long)(18 * scale), RawBytes = (long)(210_000 * scale), WireBytes = (long)(96_000 * scale), Ratio = 2.19, Encodings = new EncodingCountsDto { QuickLz = (long)(12 * scale), Raw = (long)(6 * scale) } },
        };

        MessageCompressionDto Compressed(string type, double messages, double raw, double ratio, double level)
        {
            var rawBytes = (long)(raw * scale);
            var wireBytes = (long)(raw * scale / ratio);
            return new MessageCompressionDto
            {
                Type = type, Messages = (long)(messages * scale), RawBytes = rawBytes, WireBytes = wireBytes, Ratio = wireBytes > 0 ? Math.Round((double)rawBytes / wireBytes, 2) : 0,
                Codec = "zstd", AvgLevel = level, SavedBytes = Math.Max(0, rawBytes - wireBytes),
            };
        }

        window.MessageCompression = new List<MessageCompressionDto>
        {
            Compressed("VesselProtoMsgData", 310, 21_400_000, 5.2, 9.0),
            Compressed("ScenarioInfo", 64, 5_900_000, 6.8, 7.4),
            Compressed("CraftInfo", 27, 2_650_000, 3.9, 9.0),
            Compressed("ContractInfo", 42, 1_320_000, 4.4, 8.2),
            Compressed("KerbalInfo", 18, 210_000, 3.1, 9.0),
        };
        return window;
    }

    public Task<ApiResult<NetworkDto>> GetNetworkAsync(CancellationToken ct)
    {
        var since = Window(3 * 3600 + 12 * 60);
        var network = new NetworkDto
        {
            StartedUtc = _start,
            UptimeSeconds = (DateTime.UtcNow - _start).TotalSeconds,
            WindowsRecorded = 10,
            Totals = new TrafficTotalsDto { SentBytes = since.Out.Bytes * 19, SentMessages = since.Out.Messages * 19, ReceivedBytes = since.In.Bytes * 19, ReceivedMessages = since.In.Messages * 19 },
            LastMinute = Window(60),
            LastTenMinutes = Window(600),
            SinceStart = since,
        };
        network.SnapshotCompression = network.SinceStart.Snapshots;
        network.MessageCompression = network.SinceStart.MessageCompression;
        network.Compression = new CompressionDto { Mode = "Adaptive", ServerLevel = 9, EffectiveLevelNow = 9, ClientLevel = 5, CacheHits = 1840, CacheMisses = 1020, AvgCompressMicrosPerKb = 96.5 };
        return Task.FromResult(new ApiResult<NetworkDto> { Outcome = ApiOutcome.Ok, Value = network });
    }

    public async Task<ApiResult<LogDto>> GetLogAsync(int lines, CancellationToken ct)
    {
        var log = new LogDto { Requested = lines };
        var now = DateTime.Now;
        var samples = new[]
        {
            "[VMP]: Client Vlad (3f9a0c...) handshake successfully, version 0.2.0-train.20261028.1",
            "[VMP]: Online Players: 3, connected: 3",
            "[Debug]: Performing a GarbageCollection",
            "[VMP]: Vessel 4c1f2a91 saved (Duna Relay 2)",
            "[Warning]: Vessel 91bb7c20 update arrived for subspace 5 while player is in subspace 3",
            "[VMP]: Valentina sent 3 vessel proto messages",
            "[Error]: Failed to send a message to Valentina: connection reset by peer",
            "[VMP]: Backup written: Universe-20261028-090000.tar.zst (6.1 MB, 3.2x)",
            "[VMP]: Jebediah changed to subspace 3",
        };
        for (var i = 0; i < Math.Min(lines, 60); i++)
        {
            log.Lines.Add($"[{now.AddSeconds(-(60 - i) * 7):HH:mm:ss}]{samples[(i * 5 + i / 3) % samples.Length]}");
        }

        log.Count = log.Lines.Count;
        await Task.Delay(30, ct);
        return new ApiResult<LogDto> { Outcome = ApiOutcome.Ok, Value = log };
    }

    public Task<ApiResult<ReportsDto>> GetReportsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var reports = new ReportsDto
        {
            ZipCount = 3,
            Count = 3,
            Entries =
            {
                new ReportEntryDto { File = "Jebediah_20261028-101500Z-stall.zip", Player = "Jebediah", Utc = now.AddMinutes(-95).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Bytes = 2_480_113, Reason = "stall" },
                new ReportEntryDto { File = "Valentina_20261028-091200Z-errors.zip", Player = "Valentina", Utc = now.AddHours(-3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Bytes = 1_204_550, Reason = "errors" },
                new ReportEntryDto { File = "Vlad_20261027-221030Z.zip", Player = "Vlad", Utc = now.AddHours(-14).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Bytes = 5_902_210, Reason = "manual" },
            },
        };
        return Task.FromResult(new ApiResult<ReportsDto> { Outcome = ApiOutcome.Ok, Value = reports });
    }

    public Task<ApiResult<BackupsDto>> GetBackupsAsync(CancellationToken ct)
        => Task.FromResult(new ApiResult<BackupsDto>
        {
            Outcome = ApiOutcome.Ok,
            Value = new BackupsDto
            {
                Backups = _backups.ToList(),
                Count = _backups.Count,
                TotalBytes = _backups.Sum(b => b.Bytes) + 4_200,
                CapBytes = 2048L * 1024 * 1024,
                Policy = new BackupPolicyDto { KeepNewest = 10, KeepDailyDays = 14, KeepScreenshotArchives = 10, LegacyZipKeep = 0, MaxTotalMegabytes = 2048 },
            },
        });

    public Task<ApiResult<BackupDeleteResultDto>> DeleteBackupAsync(string name, CancellationToken ct)
    {
        var entry = _backups.FirstOrDefault(b => b.Name == name);
        if (entry == null) return Task.FromResult(ApiResult<BackupDeleteResultDto>.Fail(ApiOutcome.Failed, "no such backup", 404));
        if (name == BackupManagement.NewestArchive(_backups)) return Task.FromResult(ApiResult<BackupDeleteResultDto>.Fail(ApiOutcome.Failed, "this is the newest archive backup and is protected", 409));
        _backups.Remove(entry);
        return Task.FromResult(new ApiResult<BackupDeleteResultDto> { Outcome = ApiOutcome.Ok, Value = new BackupDeleteResultDto { Name = name, Deleted = { name }, FreedBytes = entry.Bytes } });
    }

    public Task<ApiResult<BackupPruneResultDto>> PruneBackupsAsync(bool dryRun, bool includeLegacyZips, CancellationToken ct)
    {
        // Sample rules: archives beyond the newest 10 go; with the zip option all but the newest 3 zips go.
        var result = new BackupPruneResultDto { DryRun = dryRun, CapBytes = 2048L * 1024 * 1024 };
        foreach (var b in _backups.Where(b => b.Kind == "tar.zst").OrderByDescending(b => b.Name, StringComparer.Ordinal).Skip(10)) result.Deleted.Add(new PrunedFileDto { Name = b.Name, Bytes = b.Bytes, Reason = "keep-rules" });
        if (includeLegacyZips)
        {
            foreach (var b in _backups.Where(b => b.Kind == "zip").OrderByDescending(b => b.Name, StringComparer.Ordinal).Skip(3)) result.Deleted.Add(new PrunedFileDto { Name = b.Name, Bytes = b.Bytes, Reason = "legacy-zip-keep" });
        }

        result.FreedBytes = result.Deleted.Sum(d => d.Bytes);
        if (!dryRun) _backups.RemoveAll(b => result.Deleted.Any(d => d.Name == b.Name));
        result.TotalBytesAfter = _backups.Sum(b => b.Bytes) + 4_200 - (dryRun ? result.FreedBytes : 0);
        return Task.FromResult(new ApiResult<BackupPruneResultDto> { Outcome = ApiOutcome.Ok, Value = result });
    }

    public async Task<ApiResult<BackupResultDto>> CreateBackupAsync(CancellationToken ct)
    {
        await Task.Delay(600, ct);
        var name = $"Universe-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.zst";
        _backups.Insert(0, new BackupEntryDto { Name = name, Bytes = 6_455_000, CreatedUtc = DateTime.UtcNow, Kind = "tar.zst" });
        return new ApiResult<BackupResultDto>
        {
            Outcome = ApiOutcome.Ok,
            Value = new BackupResultDto
            {
                FileName = name, Files = 1_912, RawBytes = 38_412_980, CompressedBytes = 6_455_000, Ratio = 5.95, Seconds = 1.8, Level = 9, Verified = true,
                Excluded = { "Backups", "Logs", "ClientLogs", "Config/admin-token.txt" },
            },
        };
    }

    public Task<ApiResult<OkDto>> SayAsync(string text, CancellationToken ct)
        => Task.FromResult(new ApiResult<OkDto> { Outcome = ApiOutcome.Ok, Value = new OkDto { Ok = true } });

    public Task<ApiResult<OkDto>> KickAsync(string name, string reason, CancellationToken ct)
        => Task.FromResult(new ApiResult<OkDto> { Outcome = ApiOutcome.Ok, Value = new OkDto { Ok = true } });

    private readonly Dictionary<string, DateTime> _logRequests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simulates the server's one-request-per-minute limit for each player, so the 429 message can be seen in --mock.</summary>
    public Task<ApiResult<RequestLogsResultDto>> RequestLogsAsync(string name, string note, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_logRequests.TryGetValue(name ?? "", out var last) && now - last < TimeSpan.FromSeconds(60))
        {
            return Task.FromResult(ApiResult<RequestLogsResultDto>.Fail(ApiOutcome.Failed, "logs were already requested from this player less than 60 seconds ago", 429));
        }

        _logRequests[name ?? ""] = now;
        return Task.FromResult(new ApiResult<RequestLogsResultDto> { Outcome = ApiOutcome.Ok, Value = new RequestLogsResultDto { Requested = true, Player = name } });
    }

    private readonly SettingsSchemaDto _settings = MockSettings.Create();

    public async Task<ApiResult<SettingsSchemaDto>> GetSettingsAsync(CancellationToken ct)
    {
        await Task.Delay(40, ct);
        return new ApiResult<SettingsSchemaDto> { Outcome = ApiOutcome.Ok, Value = MockSettings.Clone(_settings) };
    }

    public async Task<ApiResult<SettingsSaveResultDto>> PutSettingsAsync(IReadOnlyDictionary<string, object> changes, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        return MockSettings.Apply(_settings, changes);
    }

    public async Task<ProcResult> RunControlAsync(string action, CancellationToken ct)
    {
        await Task.Delay(400, ct);
        return new ProcResult(0, "mock: " + action + " done", "", false, null);
    }

    public Task<ProcResult> RestartPcAsync(CancellationToken ct) => Task.FromResult(new ProcResult(0, "mock: restart scheduled", "", false, null));
    public Task<ProcResult> AbortRestartPcAsync(CancellationToken ct) => Task.FromResult(new ProcResult(0, "mock: restart aborted", "", false, null));

    public void Dispose()
    {
    }
}

/// <summary>Polls the session on the thread that started it (so events arrive on the UI thread): every 2 s while the window is up, every 10 s while it is hidden.</summary>
public sealed class Poller
{
    private readonly IServerSession _session;
    private readonly Func<bool> _windowActive;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _wake;

    public PollSnapshot Latest { get; private set; }
    public event Action<PollSnapshot> Updated;

    public Poller(IServerSession session, Func<bool> windowActive)
    {
        _session = session;
        _windowActive = windowActive;
    }

    public CancellationToken Token => _stop.Token;

    public void Start() => _ = RunAsync();

    public void Stop() => _stop.Cancel();

    /// <summary>Polls again now instead of waiting (after an action, or when the window is shown).</summary>
    public void RefreshNow() => _wake?.TrySetResult();

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var hidden = !_windowActive();
            try
            {
                var snapshot = await _session.PollAsync(hidden, ct);
                Latest = snapshot;
                Updated?.Invoke(snapshot);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                var snapshot = new PollSnapshot { Api = ApiAvailability.Unreachable, ApiDetail = e.Message, ProfileName = _session.Profile.Name };
                Latest = snapshot;
                Updated?.Invoke(snapshot);
            }

            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Task.WhenAny(Task.Delay(hidden ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2), ct), _wake.Task);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
