using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmpControl.Core;

/// <summary>Reads the admin API's JSON. Unknown fields are ignored and missing ones keep their defaults, because the API gains fields over time.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Null when the text is empty, not JSON, or has a field of a clashing type.</summary>
    public static T TryParse<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ErrorMessageOf(string json)
    {
        return TryParse<ErrorDto>(json)?.Error;
    }
}

public sealed class ErrorDto { public string Error { get; set; } }

public sealed class OkDto { public bool Ok { get; set; } }

public sealed class SettingsHighlightsDto
{
    public string ServerName { get; set; }
    public string GameMode { get; set; }
    public string WarpMode { get; set; }
    public int MaxPlayers { get; set; }
    public bool PasswordProtected { get; set; }
    public bool Cheats { get; set; }
    public int GamePort { get; set; }
}

public sealed class PlayerDto
{
    public string Name { get; set; }
    public int Subspace { get; set; }
    public DateTime? ConnectedSinceUtc { get; set; }
    public double ConnectedSeconds { get; set; }
}

public sealed class MachineDto
{
    public long? RamTotalBytes { get; set; }
    public long? RamFreeBytes { get; set; }
    public double? OsUptimeSeconds { get; set; }
    public long? DiskFreeBytes { get; set; }
    public long? DiskTotalBytes { get; set; }
    public string DiskRoot { get; set; }
}

public sealed class StatusDto
{
    private List<PlayerDto> _players = new();

    public string Version { get; set; }
    public DateTime? StartedUtc { get; set; }
    public double UptimeSeconds { get; set; }
    public long WorkingSetBytes { get; set; }
    public double CpuPercent { get; set; }
    public int PlayersOnline { get; set; }
    public List<PlayerDto> Players { get => _players; set => _players = value ?? new(); }
    public int VesselCount { get; set; }
    public long UniverseBytes { get; set; }
    public SettingsHighlightsDto Settings { get; set; }
    public MachineDto Machine { get; set; }

    /// <summary>The larger of the reported count and the list length (either may be missing on an older server).</summary>
    public int PlayerCount => Math.Max(PlayersOnline, Players.Count);
}

public sealed class ClientDto
{
    public string Name { get; set; }
    public bool Authenticated { get; set; }
    public int Subspace { get; set; }
    public DateTime? ConnectedSinceUtc { get; set; }
    public double ConnectedSeconds { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long? WireBytesIn { get; set; }
    public long? WireBytesOut { get; set; }
    public string Build { get; set; }
    public double? LatencyMs { get; set; }
}

public sealed class ClientsDto
{
    private List<ClientDto> _clients = new();
    public List<ClientDto> Clients { get => _clients; set => _clients = value ?? new(); }
}

public sealed class LogDto
{
    private List<string> _lines = new();
    public int Requested { get; set; }
    public int Count { get; set; }
    public List<string> Lines { get => _lines; set => _lines = value ?? new(); }
}

public sealed class ReportEntryDto
{
    public string File { get; set; }
    public string Player { get; set; }
    public string Utc { get; set; }
    public long Bytes { get; set; }
    public string Reason { get; set; }
}

public sealed class ReportsDto
{
    private List<ReportEntryDto> _entries = new();
    public int ZipCount { get; set; }
    public int Count { get; set; }
    public List<ReportEntryDto> Entries { get => _entries; set => _entries = value ?? new(); }
}

public sealed class BackupEntryDto
{
    public string Name { get; set; }
    public long Bytes { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public string Kind { get; set; }
}

public sealed class BackupsDto
{
    private List<BackupEntryDto> _backups = new();
    public List<BackupEntryDto> Backups { get => _backups; set => _backups = value ?? new(); }
}

public sealed class BackupResultDto
{
    private List<string> _excluded = new(), _skipped = new(), _pruned = new();
    public string FileName { get; set; }
    public int Files { get; set; }
    public long RawBytes { get; set; }
    public long CompressedBytes { get; set; }
    public double Ratio { get; set; }
    public double Seconds { get; set; }
    public int Level { get; set; }
    public bool Verified { get; set; }
    public string Sha256 { get; set; }
    public List<string> Excluded { get => _excluded; set => _excluded = value ?? new(); }
    public List<string> Skipped { get => _skipped; set => _skipped = value ?? new(); }
    public List<string> Pruned { get => _pruned; set => _pruned = value ?? new(); }
}

public sealed class TrafficTypeDto
{
    public string Type { get; set; }
    public long Bytes { get; set; }
    public long Messages { get; set; }
    public double BytesPerSecond { get; set; }
}

public sealed class TrafficDirectionDto
{
    private List<TrafficTypeDto> _types = new();
    public long Bytes { get; set; }
    public long Messages { get; set; }
    public double BytesPerSecond { get; set; }
    public List<TrafficTypeDto> Types { get => _types; set => _types = value ?? new(); }
}

public sealed class EncodingCountsDto
{
    public long Raw { get; set; }
    public long QuickLz { get; set; }
    public long Zstd { get; set; }
    public long ZstdDictionary { get; set; }

    /// <summary>Only the encodings that were used, e.g. "zstd 40, raw 2".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (ZstdDictionary > 0) parts.Add($"zstd-dict {ZstdDictionary}");
        if (Zstd > 0) parts.Add($"zstd {Zstd}");
        if (QuickLz > 0) parts.Add($"quicklz {QuickLz}");
        if (Raw > 0) parts.Add($"raw {Raw}");
        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }
}

public sealed class SnapshotCompressionDto
{
    public string Domain { get; set; }
    public long Snapshots { get; set; }
    public long RawBytes { get; set; }
    public long WireBytes { get; set; }
    public double Ratio { get; set; }
    public EncodingCountsDto Encodings { get; set; }
}

public sealed class TrafficWindowDto
{
    private List<SnapshotCompressionDto> _snapshots = new();
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double Seconds { get; set; }
    public TrafficDirectionDto Out { get; set; }
    public TrafficDirectionDto In { get; set; }
    public List<SnapshotCompressionDto> Snapshots { get => _snapshots; set => _snapshots = value ?? new(); }
}

public sealed class TrafficTotalsDto
{
    public long SentBytes { get; set; }
    public long SentMessages { get; set; }
    public long ReceivedBytes { get; set; }
    public long ReceivedMessages { get; set; }
}

public sealed class NetworkDto
{
    private List<SnapshotCompressionDto> _snapshotCompression = new();
    public DateTime? StartedUtc { get; set; }
    public double UptimeSeconds { get; set; }
    public int WindowsRecorded { get; set; }
    public TrafficTotalsDto Totals { get; set; }
    public TrafficWindowDto LastMinute { get; set; }
    public TrafficWindowDto LastTenMinutes { get; set; }
    public TrafficWindowDto SinceStart { get; set; }
    public List<SnapshotCompressionDto> SnapshotCompression { get => _snapshotCompression; set => _snapshotCompression = value ?? new(); }
}
