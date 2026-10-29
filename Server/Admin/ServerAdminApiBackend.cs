using LmpCommon;
using LmpCommon.Compression;
using LmpCommon.Stats;
using Server.Client;
using Server.Command.Command;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using Server.System;
using Server.System.Backup;
using Server.System.LogUpload;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Server.Admin
{
    /// <summary>Process CPU use as a percentage of the whole machine, from two readings of the process CPU time.</summary>
    public sealed class CpuMeter
    {
        private readonly object _lock = new object();
        private readonly int _cores;
        private TimeSpan _lastCpu;
        private DateTime _lastWallUtc;
        private double _lastPercent;

        public CpuMeter(int cores, TimeSpan startCpu, DateTime startWallUtc)
        {
            _cores = Math.Max(1, cores);
            _lastCpu = startCpu;
            _lastWallUtc = startWallUtc;
        }

        /// <summary>Readings less than a second apart return the previous figure.</summary>
        public double Sample(TimeSpan totalCpu, DateTime nowUtc)
        {
            lock (_lock)
            {
                var wall = (nowUtc - _lastWallUtc).TotalSeconds;
                if (wall < 1.0) return _lastPercent;

                var cpu = (totalCpu - _lastCpu).TotalSeconds;
                _lastPercent = Math.Round(Math.Max(0, cpu / (wall * _cores) * 100.0), 1);
                _lastCpu = totalCpu;
                _lastWallUtc = nowUtc;
                return _lastPercent;
            }
        }
    }

    /// <summary>The real server behind the admin API. All reads; the only writes are the same say / kick / backup the console already has.</summary>
    public sealed class ServerAdminApiBackend : IAdminApiBackend
    {
        private const int MaxReports = 100;
        private const int MaxBackupsListed = 500;
        private static readonly TimeSpan UniverseSizeCache = TimeSpan.FromSeconds(30);

        private readonly CpuMeter _cpu;
        private readonly object _sizeLock = new object();
        private long _universeBytes;
        private DateTime _universeMeasuredUtc = DateTime.MinValue;

        public ServerAdminApiBackend()
        {
            using (var process = Process.GetCurrentProcess())
            {
                _cpu = new CpuMeter(Environment.ProcessorCount, TimeSpan.Zero, process.StartTime.ToUniversalTime());
            }
        }

        public StatusInfo GetStatus()
        {
            var now = DateTime.UtcNow;
            long workingSet;
            TimeSpan cpuTime;
            using (var process = Process.GetCurrentProcess())
            {
                workingSet = process.WorkingSet64;
                cpuTime = process.TotalProcessorTime;
            }

            var uptime = ServerContext.ServerClock.Elapsed;
            var players = ClientRetriever.GetAuthenticatedClients().Select(c => new PlayerInfo
            {
                Name = AdminLogTail.Clean(c.PlayerName),
                Subspace = c.Subspace,
                ConnectedSinceUtc = c.ConnectedAtUtc,
                ConnectedSeconds = Math.Max(0, (now - c.ConnectedAtUtc).TotalSeconds),
            }).ToList();

            return new StatusInfo
            {
                Version = ProductVersion.Of(typeof(ServerAdminApiBackend).Assembly),
                StartedUtc = now - uptime,
                UptimeSeconds = Math.Round(uptime.TotalSeconds, 1),
                WorkingSetBytes = workingSet,
                CpuPercent = _cpu.Sample(cpuTime, now),
                PlayersOnline = players.Count,
                Players = players,
                VesselCount = VesselStoreSystem.CurrentVessels.Count,
                UniverseBytes = GetUniverseBytes(now),
                Settings = new SettingsHighlights
                {
                    ServerName = GeneralSettings.SettingsStore.ServerName,
                    GameMode = GeneralSettings.SettingsStore.GameMode.ToString(),
                    WarpMode = WarpSettings.SettingsStore.WarpMode.ToString(),
                    MaxPlayers = GeneralSettings.SettingsStore.MaxPlayers,
                    PasswordProtected = ServerContext.UsePassword,
                    Cheats = GeneralSettings.SettingsStore.Cheats,
                    GamePort = ConnectionSettings.SettingsStore.Port,
                },
                Machine = MachineProbe.Read(AppDomain.CurrentDomain.BaseDirectory),
            };
        }

        public NetworkInfo GetNetwork()
        {
            var network = AdminApiMapper.ToNetwork(VmpTrafficStats.GetReport(DateTime.UtcNow));
            var config = MessageCodec.Config;
            network.Compression = AdminApiMapper.ToCompression(config, MessageCodec.Policy.EffectiveLevelNow(CompressionSide.Server, config),
                VmpTrafficStats.CacheHits, VmpTrafficStats.CacheMisses, MessageCodec.Policy.AvgMicrosPerKb(CompressionSide.Server));
            return network;
        }

        public List<ClientInfo> GetClients()
        {
            var now = DateTime.UtcNow;
            var list = new List<ClientInfo>();
            foreach (var client in ClientRetriever.GetClients())
            {
                var info = new ClientInfo
                {
                    Name = AdminLogTail.Clean(client.PlayerName),
                    Authenticated = client.Authenticated,
                    Subspace = client.Subspace,
                    ConnectedSinceUtc = client.ConnectedAtUtc,
                    ConnectedSeconds = Math.Max(0, (now - client.ConnectedAtUtc).TotalSeconds),
                    BytesIn = client.BytesReceived,
                    BytesOut = client.BytesSent,
                    Build = client.ExactClientBuild == null ? null : AdminLogTail.Clean(client.ExactClientBuild),
                };

                try
                {
                    var connection = client.Connection;
                    if (connection != null)
                    {
                        info.WireBytesIn = connection.Statistics.ReceivedBytes;
                        info.WireBytesOut = connection.Statistics.SentBytes;
                        var rtt = connection.AverageRoundtripTime;
                        if (rtt > 0) info.LatencyMs = Math.Round(rtt * 1000.0, 1);
                    }
                }
                catch (Exception)
                {
                    // The connection can be torn down while we look at it; the app-level numbers above are still right.
                }

                list.Add(info);
            }

            return list;
        }

        public List<string> ReadLog(int lines)
        {
            return AdminLogTail.ReadTail(LunaLog.LogFilename, lines);
        }

        public ReportsInfo GetReports()
        {
            var info = new ReportsInfo();
            var folder = LogUploadSystem.ClientLogsDirectory;
            try
            {
                if (Directory.Exists(folder)) info.ZipCount = Directory.GetFiles(folder, "*.zip").Length;
            }
            catch (IOException)
            {
            }

            // Each line is one JSON object; the file only ever grows, so the newest entries are at the end.
            foreach (var line in AdminLogTail.ReadTail(Path.Combine(folder, LogUploadAssembler.IndexFileName), MaxReports, 2 * 1024 * 1024))
            {
                try
                {
                    using (var document = JsonDocument.Parse(line))
                    {
                        info.Entries.Add(document.RootElement.Clone());
                    }
                }
                catch (JsonException)
                {
                    // Skip a line that is cut off or not JSON.
                }
            }

            // Newest first.
            info.Entries.Reverse();
            info.Count = info.Entries.Count;
            return info;
        }

        public List<BackupFileEntry> ListBackups()
        {
            var list = new List<BackupFileEntry>();
            if (!Directory.Exists(BackupSystem.BackupsDirectory)) return list;

            foreach (var path in Directory.GetFiles(BackupSystem.BackupsDirectory))
            {
                var name = Path.GetFileName(path);
                var kind = BackupNaming.KindOf(name);
                if (kind == null) continue;

                var file = new FileInfo(path);
                list.Add(new BackupFileEntry { Name = name, Bytes = file.Length, CreatedUtc = file.CreationTimeUtc, Kind = kind });
            }

            return list.OrderByDescending(b => b.CreatedUtc).Take(MaxBackupsListed).ToList();
        }

        public async Task<BackupCreateInfo> CreateBackupAsync()
        {
            var outcome = await BackupSystem.CreateArchiveAsync().ConfigureAwait(false);
            if (outcome.AlreadyRunning) return null;
            if (outcome.Error != null)
            {
                LunaLog.Error($"[AdminApi] backup failed: {outcome.Error}");
                throw new AdminApiException(500, "backup failed: " + outcome.Error);
            }

            BackupSystem.LogOutcome(Task.FromResult(outcome));
            var result = outcome.Result;
            return new BackupCreateInfo
            {
                FileName = result.FileName,
                Files = result.Files,
                RawBytes = result.RawBytes,
                CompressedBytes = result.CompressedBytes,
                Ratio = Math.Round(result.Ratio, 2),
                Seconds = Math.Round(result.Seconds, 1),
                Level = result.Level,
                Verified = result.Verified,
                Sha256 = result.Sha256,
                Excluded = result.Excluded,
                Skipped = result.Skipped,
                Pruned = outcome.Pruned,
            };
        }

        public void Say(string text)
        {
            SayCommand.Broadcast(text);
        }

        public bool Kick(string name, string reason)
        {
            return KickCommand.KickPlayer(name, reason);
        }

        public LogRequestResult RequestLogs(string name, string note)
        {
            return LogUploadSystem.RequestLogs(name, note);
        }

        private long GetUniverseBytes(DateTime now)
        {
            lock (_sizeLock)
            {
                if (now - _universeMeasuredUtc < UniverseSizeCache) return _universeBytes;

                long total = 0;
                try
                {
                    if (Directory.Exists(ServerContext.UniverseDirectory))
                    {
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                        foreach (var path in Directory.EnumerateFiles(ServerContext.UniverseDirectory, "*", options))
                        {
                            try
                            {
                                total += new FileInfo(path).Length;
                            }
                            catch (IOException)
                            {
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }

                _universeBytes = total;
                _universeMeasuredUtc = now;
                return total;
            }
        }
    }
}
