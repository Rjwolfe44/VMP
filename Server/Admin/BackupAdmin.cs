using Server.System.Backup;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Admin
{
    /// <summary>
    /// What the admin API does with the Backups folder: list, size and policy, delete one file, prune. It takes the folder, the policy, the "backup is
    /// running" flag and the clock as arguments so tests can run it on a temporary folder. Only files whose names match the exact backup name patterns
    /// (<see cref="BackupNaming.TryClassify"/>) are ever deleted.
    /// </summary>
    public sealed class BackupAdmin
    {
        public const int MaxBackupsListed = 500;

        private readonly string _folder;
        private readonly Func<RetentionPolicy> _policy;
        private readonly Func<bool> _isRunning;
        private readonly Func<DateTime> _utcNow;
        private readonly Action<string> _log;

        public BackupAdmin(string folder, Func<RetentionPolicy> policy, Func<bool> isRunning, Func<DateTime> utcNow = null, Action<string> log = null)
        {
            _folder = folder;
            _policy = policy ?? (() => new RetentionPolicy());
            _isRunning = isRunning ?? (() => false);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _log = log ?? (_ => { });
        }

        public List<BackupFileEntry> List()
        {
            var list = new List<BackupFileEntry>();
            if (!Directory.Exists(_folder)) return list;

            foreach (var path in Directory.GetFiles(_folder))
            {
                var name = Path.GetFileName(path);
                var kind = BackupNaming.KindOf(name);
                if (kind == null) continue;

                var file = new FileInfo(path);
                list.Add(new BackupFileEntry { Name = name, Bytes = file.Length, CreatedUtc = file.CreationTimeUtc, Kind = kind });
            }

            return list.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Name, StringComparer.Ordinal).Take(MaxBackupsListed).ToList();
        }

        public BackupFolderInfo GetFolderInfo()
        {
            var policy = _policy();
            var files = BackupRetention.ListFolder(_folder);
            return new BackupFolderInfo
            {
                Count = files.Count(f => BackupNaming.KindOf(f.Name) != null),
                TotalBytes = files.Sum(f => f.Bytes),
                CapBytes = policy.MaxTotalBytes,
                Policy = new BackupPolicyInfo
                {
                    KeepNewest = policy.KeepNewest,
                    KeepDailyDays = policy.KeepDailyDays,
                    KeepScreenshotArchives = policy.KeepScreenshotArchives,
                    LegacyZipKeep = policy.LegacyZipKeep,
                    MaxTotalMegabytes = policy.MaxTotalMegabytes,
                },
            };
        }

        /// <summary>
        /// Deletes one backup file and its .sha256 sidecar. The newest Universe archive (and its sidecar) is protected unless
        /// <paramref name="force"/> is set. Names that are not exactly a backup file name are answered as not found.
        /// </summary>
        public BackupDeleteResult Delete(string name, bool force)
        {
            var result = new BackupDeleteResult { Name = name };
            if (!BackupNaming.TryClassify(name, out var kind, out _, out var isSidecar))
            {
                result.Status = BackupDeleteStatus.NotFound;
                return result;
            }

            if (_isRunning())
            {
                result.Status = BackupDeleteStatus.Running;
                return result;
            }

            lock (BackupRetention.Gate)
            {
                var path = Path.Combine(_folder, name);
                if (!File.Exists(path))
                {
                    result.Status = BackupDeleteStatus.NotFound;
                    return result;
                }

                var archiveName = isSidecar ? name.Substring(0, name.Length - BackupNaming.ChecksumExtension.Length) : name;
                if (kind == BackupNaming.FileKind.TarZst && !force)
                {
                    var newest = BackupRetention.NewestArchiveName(BackupRetention.ListFolder(_folder).Select(f => f.Name));
                    if (string.Equals(newest, archiveName, StringComparison.Ordinal))
                    {
                        result.Status = BackupDeleteStatus.Protected;
                        return result;
                    }
                }

                try
                {
                    var bytes = new FileInfo(path).Length;
                    File.Delete(path);
                    result.Files.Add(name);
                    result.FreedBytes = bytes;

                    if (!isSidecar && kind != BackupNaming.FileKind.Screenshots)
                    {
                        var sidecar = path + BackupNaming.ChecksumExtension;
                        if (File.Exists(sidecar))
                        {
                            var sidecarBytes = new FileInfo(sidecar).Length;
                            File.Delete(sidecar);
                            result.Files.Add(name + BackupNaming.ChecksumExtension);
                            result.FreedBytes += sidecarBytes;
                        }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    result.Status = result.Files.Count == 0 ? BackupDeleteStatus.InUse : BackupDeleteStatus.Deleted;
                    if (result.Status == BackupDeleteStatus.InUse) return result;
                }
            }

            result.Status = BackupDeleteStatus.Deleted;
            _log($"[Backup] deleted {name} ({result.FreedBytes} bytes freed) through the admin API{(force ? " (forced)" : string.Empty)}");
            return result;
        }

        /// <summary>
        /// Applies the retention policy now (or, for a dry run, only reports what it would delete). The newest Universe archive is never
        /// taken. <paramref name="includeLegacyZips"/> keeps the newest 3 zips when BackupLegacyZipKeep is 0.
        /// </summary>
        public BackupPruneResult Prune(bool dryRun, bool includeLegacyZips)
        {
            if (_isRunning()) return new BackupPruneResult { Running = true, DryRun = dryRun };

            var names = BackupRetention.ListFolder(_folder).Select(f => f.Name).ToList();
            var newest = BackupRetention.NewestArchiveName(names);
            var run = BackupRetention.Run(_folder, _utcNow(), _policy(), newest == null ? null : new[] { newest }, includeLegacyZips, dryRun);

            var result = new BackupPruneResult
            {
                DryRun = dryRun,
                FreedBytes = run.FreedBytes,
                TotalBytesAfter = run.TotalBytesAfter,
                CapBytes = run.CapBytes,
                StillOverCap = run.StillOverCap,
                Failed = run.Failed.Select(f => f.Name).ToList(),
            };
            foreach (var deleted in run.Deleted) result.Deleted.Add(new PrunedFileInfo { Name = deleted.Name, Bytes = deleted.Bytes, Reason = deleted.Reason });

            if (!dryRun) _log($"[Backup] prune through the admin API: deleted {result.Deleted.Count} file(s), {result.FreedBytes} bytes freed{(includeLegacyZips ? " (old zips included)" : string.Empty)}");
            return result;
        }
    }
}
