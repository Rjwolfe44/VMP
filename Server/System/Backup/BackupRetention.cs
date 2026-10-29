using System;
using System.Collections.Generic;
using System.IO;

namespace Server.System.Backup
{
    /// <summary>
    /// Keeps the newest N archives plus the newest one of each of the last D days, deletes the rest. Only files named exactly like
    /// Universe-yyyyMMdd-HHmmss.tar.zst are ever considered (and their .sha256 sidecar goes with them); old zips, other files,
    /// .part files and orphan sidecars are never touched, and the archive that was just created is never deleted.
    /// </summary>
    public static class BackupRetention
    {
        public static List<string> SelectDeletions(IEnumerable<string> fileNames, DateTime nowUtc, int keepNewest, int keepDailyDays, string neverDelete)
        {
            var backups = new List<KeyValuePair<DateTime, string>>();
            foreach (var name in fileNames)
            {
                if (BackupNaming.TryParseArchive(name, out var timestamp)) backups.Add(new KeyValuePair<DateTime, string>(timestamp, name));
            }

            // Newest first; the name breaks ties so the result is deterministic.
            backups.Sort((a, b) =>
            {
                var byTime = b.Key.CompareTo(a.Key);
                return byTime != 0 ? byTime : string.CompareOrdinal(b.Value, a.Value);
            });

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(neverDelete)) keep.Add(neverDelete);

            for (var i = 0; i < backups.Count && i < Math.Max(0, keepNewest); i++) keep.Add(backups[i].Value);

            if (keepDailyDays > 0)
            {
                var oldestDay = nowUtc.Date.AddDays(-(keepDailyDays - 1));
                var seenDays = new HashSet<DateTime>();
                foreach (var backup in backups)
                {
                    var day = backup.Key.Date;
                    if (day < oldestDay) continue;
                    if (seenDays.Add(day)) keep.Add(backup.Value);
                }
            }

            var delete = new List<string>();
            foreach (var backup in backups)
            {
                if (!keep.Contains(backup.Value)) delete.Add(backup.Value);
            }

            return delete;
        }

        /// <summary>Applies the rules to a folder and returns the archive names that were deleted.</summary>
        public static List<string> Apply(string folder, DateTime nowUtc, int keepNewest, int keepDailyDays, string neverDelete)
        {
            var deleted = new List<string>();
            if (!Directory.Exists(folder)) return deleted;

            var names = new List<string>();
            foreach (var path in Directory.GetFiles(folder)) names.Add(Path.GetFileName(path));

            foreach (var name in SelectDeletions(names, nowUtc, keepNewest, keepDailyDays, neverDelete))
            {
                try
                {
                    File.Delete(Path.Combine(folder, name));
                    var sidecar = Path.Combine(folder, name + BackupNaming.ChecksumExtension);
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                    deleted.Add(name);
                }
                catch (IOException)
                {
                    // In use right now; the next run tries again.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return deleted;
        }
    }
}
