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

/// <summary>The retention numbers of the server (GET backups, "policy"). 0 for MaxTotalMegabytes = no cap; 0 for LegacyZipKeep = old zips are not pruned automatically.</summary>
public sealed class BackupPolicyDto
{
    public int KeepNewest { get; set; }
    public int KeepDailyDays { get; set; }
    public int KeepScreenshotArchives { get; set; }
    public int LegacyZipKeep { get; set; }
    public int MaxTotalMegabytes { get; set; }
}

public sealed class BackupsDto
{
    private List<BackupEntryDto> _backups = new();
    public List<BackupEntryDto> Backups { get => _backups; set => _backups = value ?? new(); }
    public int Count { get; set; }
    /// <summary>Every file in the Backups folder (what the cap is measured against).</summary>
    public long TotalBytes { get; set; }
    public long CapBytes { get; set; }
    /// <summary>Null when the server is older than the retention controls.</summary>
    public BackupPolicyDto Policy { get; set; }
}

public sealed class BackupDeleteResultDto
{
    private List<string> _deleted = new();
    public string Name { get; set; }
    public List<string> Deleted { get => _deleted; set => _deleted = value ?? new(); }
    public long FreedBytes { get; set; }
}

public sealed class PrunedFileDto
{
    public string Name { get; set; }
    public long Bytes { get; set; }
    public string Reason { get; set; }
}

public sealed class BackupPruneResultDto
{
    private List<PrunedFileDto> _deleted = new();
    private List<string> _failed = new();
    public List<PrunedFileDto> Deleted { get => _deleted; set => _deleted = value ?? new(); }
    public long FreedBytes { get; set; }
    public bool DryRun { get; set; }
    public long TotalBytesAfter { get; set; }
    public long CapBytes { get; set; }
    public bool StillOverCap { get; set; }
    public List<string> Failed { get => _failed; set => _failed = value ?? new(); }
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

/// <summary>One row of the message-compression table: how well one message type compressed on the wire. A server without this table leaves the whole array out.</summary>
public sealed class MessageCompressionDto
{
    public string Type { get; set; }
    public long Messages { get; set; }
    public long RawBytes { get; set; }
    public long WireBytes { get; set; }
    public double Ratio { get; set; }
    public string Codec { get; set; }

    /// <summary>Average zstd level of the compressions counted in this row. 0 when unknown or when the server does not send it.</summary>
    public double AvgLevel { get; set; }

    /// <summary>Raw minus wire bytes as the server counts them. Null when the server is older than this field; see <see cref="EffectiveSavedBytes"/>.</summary>
    public long? SavedBytes { get; set; }

    /// <summary>The server's ratio, or raw / wire when it is missing. 0 when nothing was sent (wire size 0).</summary>
    [JsonIgnore]
    public double EffectiveRatio => Ratio > 0 ? Ratio : (WireBytes > 0 ? (double)RawBytes / WireBytes : 0);

    /// <summary>The server's saved bytes, or max(0, raw - wire) when it is missing. Never negative.</summary>
    [JsonIgnore]
    public long EffectiveSavedBytes => Math.Max(0, SavedBytes ?? RawBytes - WireBytes);
}

/// <summary>The <c>compression</c> block of <c>GET network</c>: how the server compresses right now. A server without it leaves the block out (null here).</summary>
public sealed class CompressionDto
{
    /// <summary>"Adaptive" or "Fixed".</summary>
    public string Mode { get; set; }
    public int ServerLevel { get; set; }
    public int EffectiveLevelNow { get; set; }
    public int ClientLevel { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public double AvgCompressMicrosPerKb { get; set; }
}

public sealed class TrafficWindowDto
{
    private List<SnapshotCompressionDto> _snapshots = new();
    private List<MessageCompressionDto> _messageCompression = new();
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double Seconds { get; set; }
    public TrafficDirectionDto Out { get; set; }
    public TrafficDirectionDto In { get; set; }
    public List<SnapshotCompressionDto> Snapshots { get => _snapshots; set => _snapshots = value ?? new(); }
    public List<MessageCompressionDto> MessageCompression { get => _messageCompression; set => _messageCompression = value ?? new(); }
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
    private List<MessageCompressionDto> _messageCompression = new();
    public DateTime? StartedUtc { get; set; }
    public double UptimeSeconds { get; set; }
    public int WindowsRecorded { get; set; }
    public TrafficTotalsDto Totals { get; set; }
    public TrafficWindowDto LastMinute { get; set; }
    public TrafficWindowDto LastTenMinutes { get; set; }
    public TrafficWindowDto SinceStart { get; set; }
    public List<SnapshotCompressionDto> SnapshotCompression { get => _snapshotCompression; set => _snapshotCompression = value ?? new(); }
    /// <summary>Totals since the server started, per message type. Empty when the server is older than this field.</summary>
    public List<MessageCompressionDto> MessageCompression { get => _messageCompression; set => _messageCompression = value ?? new(); }
    /// <summary>How the server compresses right now (mode, levels, cache). Null when the server is older than this field.</summary>
    public CompressionDto Compression { get; set; }
}

/// <summary>Answer to <c>POST clients/requestlogs</c>: <c>{"requested":true,"player":"&lt;name&gt;"}</c>.</summary>
public sealed class RequestLogsResultDto
{
    public bool? Requested { get; set; }
    public string Player { get; set; }
}

/// <summary>Body of <c>POST clients/requestlogs</c>. The note is left out of the JSON when it is empty.</summary>
public sealed class RequestLogsBody
{
    public const int MaxNoteLength = 120;

    public string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Note { get; init; }

    public static RequestLogsBody Create(string name, string note) => new() { Name = (name ?? string.Empty).Trim(), Note = CleanNote(note) };

    /// <summary>Trimmed, null when empty, cut to <see cref="MaxNoteLength"/> characters (never in the middle of a surrogate pair).</summary>
    public static string CleanNote(string note)
    {
        var text = (note ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxNoteLength)
        {
            var length = char.IsHighSurrogate(text[MaxNoteLength - 1]) ? MaxNoteLength - 1 : MaxNoteLength;
            text = text.Substring(0, length).TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }
}

/// <summary>The activity-log line for a "Request logs" call. The server's own message is used for 404 (not connected), 409 (other game build) and 429 (asked less than a minute ago).</summary>
public static class RequestLogsText
{
    public static (string Text, bool Failed) Describe(string player, ApiResult<RequestLogsResultDto> result)
    {
        if (result.IsOk && result.Value.Requested != false)
        {
            var who = string.IsNullOrWhiteSpace(result.Value.Player) ? player : result.Value.Player;
            return ($"Asked {who} to send their game logs.", false);
        }

        var reason = result.IsOk ? "the server did not accept the request" : (string.IsNullOrWhiteSpace(result.Error) ? "the request failed" : result.Error);
        return ("failed: " + reason, true);
    }
}

/// <summary>What the Network page shows in the "Message compression" table, kept apart from the controls so it can be tested.</summary>
public static class MessageCompressionView
{
    public const string EmptyText = "No compressed messages yet";

    /// <summary>
    /// The rows of the selected window. The since-start window (or no window at all) falls back to the server's top-level totals,
    /// which cover the same period. Other windows never borrow them: the totals would be wrong for a one-minute view.
    /// </summary>
    public static IReadOnlyList<MessageCompressionDto> Pick(NetworkDto network, TrafficWindowDto window)
    {
        if (window != null && window.MessageCompression.Count > 0) return window.MessageCompression;
        if (network != null && (window == null || ReferenceEquals(window, network.SinceStart))) return network.MessageCompression;
        return Array.Empty<MessageCompressionDto>();
    }

    /// <summary>Type, Messages, Raw, Sent (wire), Saved, Ratio, Codec, Level - sizes with the app's byte formatter.</summary>
    public static List<object[]> Rows(IEnumerable<MessageCompressionDto> items)
    {
        return items.Select(m => new object[]
        {
            string.IsNullOrEmpty(m.Type) ? "-" : m.Type,
            Fmt.Count(m.Messages),
            Fmt.Bytes(m.RawBytes),
            Fmt.Bytes(m.WireBytes),
            Fmt.Bytes(m.EffectiveSavedBytes),
            Fmt.Ratio(m.EffectiveRatio),
            string.IsNullOrEmpty(m.Codec) ? "-" : m.Codec,
            Level(m.AvgLevel),
        }).ToList();
    }

    /// <summary>The placeholder row of an empty table: the message in the first column, empty cells for the rest.</summary>
    public static object[] EmptyRow() => new object[] { EmptyText, "", "", "", "", "", "", "" };

    /// <summary>Average zstd level with one decimal; a dash when it is not known (0, negative or not a number).</summary>
    public static string Level(double value) => value > 0 && !double.IsInfinity(value) ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-";

    public const string NoInformationText = "Compression: no information from this server";
    public const string NoCacheLookupsText = "no cache lookups yet";

    /// <summary>
    /// The one line above the table, e.g. "Compression: Adaptive, level 9 now, saved 12.3 MB, cache hit rate 64%".
    /// Saved is the sum over <paramref name="rows"/> (the since-start rows). A server without the block gets <see cref="NoInformationText"/>.
    /// </summary>
    public static string Summary(CompressionDto block, IEnumerable<MessageCompressionDto> rows)
    {
        if (block == null) return NoInformationText;
        var parts = new List<string>();
        var mode = block.Mode?.Trim();
        if (!string.IsNullOrEmpty(mode)) parts.Add(mode);
        if (block.EffectiveLevelNow > 0)
            parts.Add(string.Equals(mode, "Fixed", StringComparison.OrdinalIgnoreCase) ? $"level {block.EffectiveLevelNow}" : $"level {block.EffectiveLevelNow} now");
        parts.Add("saved " + Fmt.Bytes(rows == null ? 0 : rows.Sum(m => m.EffectiveSavedBytes)));
        parts.Add(CacheText(block.CacheHits, block.CacheMisses));
        return "Compression: " + string.Join(", ", parts);
    }

    /// <summary>"cache hit rate 64%" (hits / (hits + misses), rounded to a whole percent), or "no cache lookups yet" when there were none.</summary>
    public static string CacheText(long hits, long misses)
    {
        hits = Math.Max(0, hits);
        misses = Math.Max(0, misses);
        var lookups = hits + misses;
        if (lookups == 0) return NoCacheLookupsText;
        return "cache hit rate " + Math.Round(100.0 * hits / lookups, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
