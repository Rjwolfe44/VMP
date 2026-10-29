using LmpCommon.Compression;
using LmpCommon.Stats;
using Server.System.LogUpload;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Server.Admin
{
    public static class AdminApiJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
    }

    /// <summary>A problem the handler reports to the caller with this status and message (everything else becomes a plain 500).</summary>
    public sealed class AdminApiException : Exception
    {
        public int StatusCode { get; }

        public AdminApiException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>Everything the API needs from the running server. The production version is <see cref="ServerAdminApiBackend"/>; tests use a fake.</summary>
    public interface IAdminApiBackend
    {
        StatusInfo GetStatus();
        NetworkInfo GetNetwork();
        List<ClientInfo> GetClients();
        /// <summary>The last <paramref name="lines"/> lines of the current server log (already capped by the caller).</summary>
        List<string> ReadLog(int lines);
        ReportsInfo GetReports();
        List<BackupFileEntry> ListBackups();
        /// <summary>Creates an archive backup now; null when one is already running. Throws <see cref="AdminApiException"/> when it fails.</summary>
        Task<BackupCreateInfo> CreateBackupAsync();
        void Say(string text);
        /// <summary>False when no such player is connected.</summary>
        bool Kick(string name, string reason);
        /// <summary>
        /// Asks the player's game to send a diagnostics bundle. The default (for backends that do not support it) answers "not found";
        /// the real server and the tests' fakes override it.
        /// </summary>
        LogRequestResult RequestLogs(string name, string note) => LogRequestResult.NotFound(name);

        /// <summary>Count, size and retention policy of the Backups folder. The default (for fakes) derives the count and size from <see cref="ListBackups"/>.</summary>
        BackupFolderInfo GetBackupFolderInfo()
        {
            var list = ListBackups() ?? new List<BackupFileEntry>();
            var total = 0L;
            foreach (var entry in list) total += entry.Bytes;
            return new BackupFolderInfo { Count = list.Count, TotalBytes = total, CapBytes = 0, Policy = new BackupPolicyInfo() };
        }

        /// <summary>Deletes one backup file (the caller already checked the name pattern). The default answers "not found".</summary>
        BackupDeleteResult DeleteBackup(string fileName, bool force) => new BackupDeleteResult { Status = BackupDeleteStatus.NotFound, Name = fileName };

        /// <summary>Applies (or, with <paramref name="dryRun"/>, only lists) the retention rules now. The default does nothing.</summary>
        BackupPruneResult PruneBackups(bool dryRun, bool includeLegacyZips) => new BackupPruneResult { DryRun = dryRun };
    }

    /// <summary>The retention numbers in force (GET /api/v1/backups, "policy").</summary>
    public sealed class BackupPolicyInfo
    {
        public int KeepNewest { get; set; } = 10;
        public int KeepDailyDays { get; set; } = 14;
        public int KeepScreenshotArchives { get; set; } = 10;
        public int LegacyZipKeep { get; set; }
        /// <summary>0 = no cap.</summary>
        public int MaxTotalMegabytes { get; set; } = 2048;
    }

    public sealed class BackupFolderInfo
    {
        /// <summary>Recognized backup files (archives, old zips, screenshot archives).</summary>
        public int Count { get; set; }
        /// <summary>Every file in the Backups folder, sidecars and unknown files included.</summary>
        public long TotalBytes { get; set; }
        /// <summary>0 = no cap.</summary>
        public long CapBytes { get; set; }
        public BackupPolicyInfo Policy { get; set; } = new BackupPolicyInfo();
    }

    public enum BackupDeleteStatus
    {
        Deleted,
        NotFound,
        /// <summary>A backup is running.</summary>
        Running,
        /// <summary>The newest Universe archive; needs force.</summary>
        Protected,
        /// <summary>The file is open in another program.</summary>
        InUse,
    }

    public sealed class BackupDeleteResult
    {
        public BackupDeleteStatus Status { get; set; }
        public string Name { get; set; }
        /// <summary>Every file removed (the archive and its sidecar).</summary>
        public List<string> Files { get; set; } = new List<string>();
        public long FreedBytes { get; set; }
    }

    public sealed class PrunedFileInfo
    {
        public string Name { get; set; }
        /// <summary>Size of the file plus its sidecar.</summary>
        public long Bytes { get; set; }
        /// <summary>keep-rules, legacy-zip-keep or size-cap.</summary>
        public string Reason { get; set; }
    }

    public sealed class BackupPruneResult
    {
        /// <summary>A backup is running, so nothing was done.</summary>
        public bool Running { get; set; }
        public bool DryRun { get; set; }
        public List<PrunedFileInfo> Deleted { get; set; } = new List<PrunedFileInfo>();
        public long FreedBytes { get; set; }
        public long TotalBytesAfter { get; set; }
        public long CapBytes { get; set; }
        /// <summary>The folder is above the cap but nothing more may be deleted (newest archive, at least 3 archives).</summary>
        public bool StillOverCap { get; set; }
        /// <summary>Files that could not be deleted because they are in use.</summary>
        public List<string> Failed { get; set; } = new List<string>();
    }

    public sealed class SettingsHighlights
    {
        public string ServerName { get; set; }
        public string GameMode { get; set; }
        public string WarpMode { get; set; }
        public int MaxPlayers { get; set; }
        public bool PasswordProtected { get; set; }
        public bool Cheats { get; set; }
        public int GamePort { get; set; }
    }

    public sealed class PlayerInfo
    {
        public string Name { get; set; }
        public int Subspace { get; set; }
        public DateTime ConnectedSinceUtc { get; set; }
        public double ConnectedSeconds { get; set; }
    }

    public sealed class StatusInfo
    {
        public string Version { get; set; }
        public DateTime StartedUtc { get; set; }
        public double UptimeSeconds { get; set; }
        public long WorkingSetBytes { get; set; }
        public double CpuPercent { get; set; }
        public int PlayersOnline { get; set; }
        public List<PlayerInfo> Players { get; set; } = new List<PlayerInfo>();
        public int VesselCount { get; set; }
        public long UniverseBytes { get; set; }
        public SettingsHighlights Settings { get; set; }
        /// <summary>The PC the server runs on (RAM, OS uptime, free disk of the server drive). Individual figures are null when unreadable.</summary>
        public MachineInfo Machine { get; set; }
    }

    public sealed class ClientInfo
    {
        public string Name { get; set; }
        public bool Authenticated { get; set; }
        public int Subspace { get; set; }
        public DateTime ConnectedSinceUtc { get; set; }
        public double ConnectedSeconds { get; set; }
        /// <summary>Message bytes the server received from / sent to this client (serialized messages, before packet headers).</summary>
        public long BytesIn { get; set; }
        public long BytesOut { get; set; }
        /// <summary>Bytes on the socket including headers and resends, from the network library; null when unavailable.</summary>
        public long? WireBytesIn { get; set; }
        public long? WireBytesOut { get; set; }
        public string Build { get; set; }
        public double? LatencyMs { get; set; }
    }

    public sealed class LogInfo
    {
        public int Requested { get; set; }
        public int Count { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
    }

    public sealed class ReportsInfo
    {
        public int ZipCount { get; set; }
        public int Count { get; set; }
        public List<JsonElement> Entries { get; set; } = new List<JsonElement>();
    }

    public sealed class BackupFileEntry
    {
        public string Name { get; set; }
        public long Bytes { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Kind { get; set; }
    }

    public sealed class BackupCreateInfo
    {
        public string FileName { get; set; }
        public int Files { get; set; }
        public long RawBytes { get; set; }
        public long CompressedBytes { get; set; }
        public double Ratio { get; set; }
        public double Seconds { get; set; }
        public int Level { get; set; }
        public bool Verified { get; set; }
        public string Sha256 { get; set; }
        public List<string> Excluded { get; set; } = new List<string>();
        public List<string> Skipped { get; set; } = new List<string>();
        public List<string> Pruned { get; set; } = new List<string>();
    }

    public sealed class TrafficTypeInfo
    {
        public string Type { get; set; }
        public long Bytes { get; set; }
        public long Messages { get; set; }
        public double BytesPerSecond { get; set; }
    }

    public sealed class TrafficDirectionInfo
    {
        public long Bytes { get; set; }
        public long Messages { get; set; }
        public double BytesPerSecond { get; set; }
        public List<TrafficTypeInfo> Types { get; set; } = new List<TrafficTypeInfo>();
    }

    public sealed class EncodingCountsInfo
    {
        public long Raw { get; set; }
        public long QuickLz { get; set; }
        public long Zstd { get; set; }
        public long ZstdDictionary { get; set; }
    }

    public sealed class SnapshotCompressionInfo
    {
        public string Domain { get; set; }
        public long Snapshots { get; set; }
        public long RawBytes { get; set; }
        public long WireBytes { get; set; }
        public double Ratio { get; set; }
        public EncodingCountsInfo Encodings { get; set; }
    }

    /// <summary>Raw versus wire bytes of one message type whose payload is compressed by itself (QuickLZ for vessel, kerbal, scenario, contract and craft data).</summary>
    public sealed class MessageCompressionInfo
    {
        public string Type { get; set; }
        /// <summary>Compressions (messages built) plus decompressions (messages read) counted for the type.</summary>
        public long Messages { get; set; }
        public long RawBytes { get; set; }
        public long WireBytes { get; set; }
        public double Ratio { get; set; }
        public string Codec { get; set; }
        /// <summary>Average zstd level of the compressions counted (0 when none had a level, for example only decompressions).</summary>
        public double AvgLevel { get; set; }
        /// <summary>Bytes that did not have to be sent: rawBytes minus wireBytes, never negative.</summary>
        public long SavedBytes { get; set; }
    }

    /// <summary>The compression settings in force and how the compression has been doing (GET /api/v1/network, "compression").</summary>
    public sealed class CompressionInfo
    {
        /// <summary>Adaptive or Fixed.</summary>
        public string Mode { get; set; }
        public int ServerLevel { get; set; }
        /// <summary>The level a typical 32 KB payload gets right now (Fixed: ServerLevel; Adaptive: after the timing feedback).</summary>
        public int EffectiveLevelNow { get; set; }
        /// <summary>The highest level clients are told to use.</summary>
        public int ClientLevel { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        /// <summary>Smoothed microseconds the server needs to compress one KB (0 until something was compressed).</summary>
        public double AvgCompressMicrosPerKb { get; set; }
    }

    public sealed class TrafficWindowInfo
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public double Seconds { get; set; }
        public TrafficDirectionInfo Out { get; set; }
        public TrafficDirectionInfo In { get; set; }
        public List<SnapshotCompressionInfo> Snapshots { get; set; } = new List<SnapshotCompressionInfo>();
        public List<MessageCompressionInfo> MessageCompression { get; set; } = new List<MessageCompressionInfo>();
    }

    public sealed class TrafficTotalsInfo
    {
        public long SentBytes { get; set; }
        public long SentMessages { get; set; }
        public long ReceivedBytes { get; set; }
        public long ReceivedMessages { get; set; }
    }

    public sealed class NetworkInfo
    {
        public DateTime StartedUtc { get; set; }
        public double UptimeSeconds { get; set; }
        /// <summary>Closed one-minute windows in the ring (at most 10). lastMinute / lastTenMinutes are null until the first one closes.</summary>
        public int WindowsRecorded { get; set; }
        public TrafficTotalsInfo Totals { get; set; }
        public TrafficWindowInfo LastMinute { get; set; }
        public TrafficWindowInfo LastTenMinutes { get; set; }
        public TrafficWindowInfo SinceStart { get; set; }
        public List<SnapshotCompressionInfo> SnapshotCompression { get; set; } = new List<SnapshotCompressionInfo>();
        /// <summary>Since start: per message type, raw versus wire bytes of the payloads that are compressed one by one (empty until one was).</summary>
        public List<MessageCompressionInfo> MessageCompression { get; set; } = new List<MessageCompressionInfo>();
        /// <summary>The compression settings in force and cache figures; the VMP server manager shows them as one line.</summary>
        public CompressionInfo Compression { get; set; }
    }

    /// <summary>Turns <see cref="TrafficReport"/> (LmpCommon) into the JSON shapes above.</summary>
    public static class AdminApiMapper
    {
        public const int MaxTypesPerDirection = 25;

        public static NetworkInfo ToNetwork(TrafficReport report)
        {
            var since = report.SinceStart;
            return new NetworkInfo
            {
                StartedUtc = report.StartedUtc,
                UptimeSeconds = Math.Max(0, (report.NowUtc - report.StartedUtc).TotalSeconds),
                WindowsRecorded = report.WindowCount,
                Totals = new TrafficTotalsInfo
                {
                    SentBytes = since.SentBytes,
                    SentMessages = since.SentMessages,
                    ReceivedBytes = since.ReceivedBytes,
                    ReceivedMessages = since.ReceivedMessages,
                },
                LastMinute = ToWindow(report.LastMinute),
                LastTenMinutes = ToWindow(report.LastTenMinutes),
                SinceStart = ToWindow(since),
                SnapshotCompression = ToSnapshots(since.Snapshots),
                MessageCompression = ToMessageCompression(since.MessageCompression),
            };
        }

        public static TrafficWindowInfo ToWindow(TrafficWindow window)
        {
            if (window == null) return null;
            return new TrafficWindowInfo
            {
                StartUtc = window.StartUtc,
                EndUtc = window.EndUtc,
                Seconds = window.Seconds,
                Out = ToDirection(window.Sent, window.Seconds),
                In = ToDirection(window.Received, window.Seconds),
                Snapshots = ToSnapshots(window.Snapshots),
                MessageCompression = ToMessageCompression(window.MessageCompression),
            };
        }

        public static List<MessageCompressionInfo> ToMessageCompression(List<VmpTrafficStats.CompressionRow> rows)
        {
            var list = new List<MessageCompressionInfo>();
            if (rows == null) return list;
            foreach (var row in rows)
            {
                list.Add(new MessageCompressionInfo
                {
                    Type = row.Type,
                    Messages = row.Messages,
                    RawBytes = row.RawBytes,
                    WireBytes = row.WireBytes,
                    Ratio = Math.Round(row.Ratio, 2),
                    Codec = row.Codec,
                    AvgLevel = Math.Round(row.AvgLevel, 1),
                    SavedBytes = row.SavedBytes,
                });
            }

            return list;
        }

        public static CompressionInfo ToCompression(ICompressionConfig config, int effectiveLevelNow, long cacheHits, long cacheMisses, double avgMicrosPerKb)
        {
            return new CompressionInfo
            {
                Mode = config.Mode.ToString(),
                ServerLevel = config.ServerLevel,
                EffectiveLevelNow = effectiveLevelNow,
                ClientLevel = config.ClientLevel,
                CacheHits = cacheHits,
                CacheMisses = cacheMisses,
                AvgCompressMicrosPerKb = Math.Round(avgMicrosPerKb, 1),
            };
        }

        private static TrafficDirectionInfo ToDirection(List<VmpTrafficStats.Row> rows, double seconds)
        {
            var info = new TrafficDirectionInfo();
            foreach (var row in rows)
            {
                info.Bytes += row.Bytes;
                info.Messages += row.Messages;
                if (info.Types.Count < MaxTypesPerDirection)
                {
                    info.Types.Add(new TrafficTypeInfo { Type = row.Name, Bytes = row.Bytes, Messages = row.Messages, BytesPerSecond = Rate(row.Bytes, seconds) });
                }
            }

            info.BytesPerSecond = Rate(info.Bytes, seconds);
            return info;
        }

        private static List<SnapshotCompressionInfo> ToSnapshots(List<VmpTrafficStats.SnapshotRow> rows)
        {
            var list = new List<SnapshotCompressionInfo>();
            foreach (var row in rows)
            {
                list.Add(new SnapshotCompressionInfo
                {
                    Domain = row.Domain.ToString(),
                    Snapshots = row.Snapshots,
                    RawBytes = row.RawBytes,
                    WireBytes = row.WireBytes,
                    Ratio = Math.Round(row.Ratio, 2),
                    Encodings = new EncodingCountsInfo { Raw = row.Raw, QuickLz = row.QuickLz, Zstd = row.Zstd, ZstdDictionary = row.ZstdDictionary },
                });
            }

            return list;
        }

        private static double Rate(long bytes, double seconds)
        {
            return seconds > 0 ? Math.Round(bytes / seconds, 1) : 0;
        }
    }
}
