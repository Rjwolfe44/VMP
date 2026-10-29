using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

        // ---- The full engine: keep rules + legacy zips + size cap -------------------------------------------------------------------------

        /// <summary>Held while files are deleted, so a prune and the retention after a backup never run at the same time.</summary>
        public static readonly object Gate = new object();

        public sealed class FolderFile
        {
            public string Name;
            public long Bytes;
        }

        public sealed class PlannedDeletion
        {
            public string Name;
            /// <summary>Size of the file plus its .sha256 sidecar.</summary>
            public long Bytes;
            public string Sidecar;
            public BackupNaming.FileKind Kind;
            /// <summary>"keep-rules", "legacy-zip-keep" or "size-cap".</summary>
            public string Reason;
        }

        public sealed class RetentionResult
        {
            public List<PlannedDeletion> Deleted = new List<PlannedDeletion>();
            public List<PlannedDeletion> Failed = new List<PlannedDeletion>();
            public bool DryRun;
            public long TotalBytesBefore;
            public long TotalBytesAfter;
            public long CapBytes;

            public long FreedBytes
            {
                get
                {
                    long sum = 0;
                    foreach (var d in Deleted) sum += d.Bytes;
                    return sum;
                }
            }

            /// <summary>True when the folder is still above the cap because nothing more may be deleted (newest archive, minimum of 3).</summary>
            public bool StillOverCap => CapBytes > 0 && TotalBytesAfter > CapBytes;
        }

        private sealed class Item
        {
            public string Name;
            public DateTime Time;
            public BackupNaming.FileKind Kind;
        }

        private static int NewestFirst(Item a, Item b)
        {
            var byTime = b.Time.CompareTo(a.Time);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.Name, a.Name);
        }

        /// <summary>The name of the newest Universe-*.tar.zst among <paramref name="names"/>, or null.</summary>
        public static string NewestArchiveName(IEnumerable<string> names)
        {
            Item newest = null;
            foreach (var name in names)
            {
                if (!BackupNaming.TryParseArchive(name, out var time)) continue;
                var item = new Item { Name = name, Time = time };
                if (newest == null || NewestFirst(item, newest) < 0) newest = item;
            }

            return newest?.Name;
        }

        /// <summary>
        /// Pure: what the rules would delete from this listing. (1) Universe-*.tar.zst by newest N plus one per day, (2) Screenshots-*.tar by newest N,
        /// (3) Universe-*.zip by the legacy keep count (only when it is above 0, or when <paramref name="includeLegacyZips"/> asks for it, which uses
        /// <see cref="RetentionPolicy.ExplicitLegacyZipKeep"/> if the setting is 0), (4) while the sum of every file in the listing is above the cap,
        /// the oldest eligible files go: zips first, then the oldest Universe archives, then the oldest screenshot archives; the newest Universe
        /// archive is never taken and at least <see cref="RetentionPolicy.MinArchivesForCap"/> Universe archives stay. Names in
        /// <paramref name="neverDelete"/> are never taken. A .sha256 sidecar goes with its archive. Files that do not match the exact name
        /// patterns (including orphan sidecars and .part files) are never touched, but count towards the total.
        /// </summary>
        public static RetentionResult Plan(IEnumerable<FolderFile> files, DateTime nowUtc, RetentionPolicy policy, IEnumerable<string> neverDelete = null, bool includeLegacyZips = false)
        {
            policy = policy ?? new RetentionPolicy();
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            long total = 0;
            foreach (var file in files)
            {
                if (file == null || string.IsNullOrEmpty(file.Name)) continue;
                sizes[file.Name] = file.Bytes;
                total += file.Bytes;
            }

            var tars = new List<Item>();
            var zips = new List<Item>();
            var shots = new List<Item>();
            foreach (var name in sizes.Keys)
            {
                if (!BackupNaming.TryClassify(name, out var kind, out var time, out var isSidecar) || isSidecar) continue;
                var item = new Item { Name = name, Time = time, Kind = kind };
                if (kind == BackupNaming.FileKind.TarZst) tars.Add(item);
                else if (kind == BackupNaming.FileKind.LegacyZip) zips.Add(item);
                else shots.Add(item);
            }

            tars.Sort(NewestFirst);
            zips.Sort(NewestFirst);
            shots.Sort(NewestFirst);

            var never = new HashSet<string>(neverDelete ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var result = new RetentionResult { DryRun = true, TotalBytesBefore = total, CapBytes = Math.Max(0, policy.MaxTotalBytes) };
            var marked = new HashSet<string>(StringComparer.Ordinal);
            var remaining = total;

            void Mark(Item item, string reason)
            {
                if (never.Contains(item.Name) || !marked.Add(item.Name)) return;
                var deletion = new PlannedDeletion { Name = item.Name, Kind = item.Kind, Reason = reason, Bytes = sizes[item.Name] };
                if (item.Kind != BackupNaming.FileKind.Screenshots && sizes.TryGetValue(item.Name + BackupNaming.ChecksumExtension, out var sidecarBytes))
                {
                    deletion.Sidecar = item.Name + BackupNaming.ChecksumExtension;
                    deletion.Bytes += sidecarBytes;
                }

                remaining -= deletion.Bytes;
                result.Deleted.Add(deletion);
            }

            var byName = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var item in tars.Concat(zips).Concat(shots)) byName[item.Name] = item;

            foreach (var name in SelectDeletions(tars.Select(t => t.Name), nowUtc, policy.KeepNewest, policy.KeepDailyDays, null)) Mark(byName[name], "keep-rules");
            foreach (var name in ScreenshotArchiver.SelectDeletions(shots.Select(s => s.Name), policy.KeepScreenshotArchives, null)) Mark(byName[name], "keep-rules");

            var zipKeep = policy.LegacyZipKeep > 0 ? policy.LegacyZipKeep : includeLegacyZips ? RetentionPolicy.ExplicitLegacyZipKeep : 0;
            if (zipKeep > 0)
            {
                for (var i = zipKeep; i < zips.Count; i++) Mark(zips[i], "legacy-zip-keep");
            }

            if (result.CapBytes > 0 && remaining > result.CapBytes)
            {
                // Oldest first inside each group; the groups go in this order. Lists are sorted newest first, so walk them backwards.
                for (var i = zips.Count - 1; i >= 0 && remaining > result.CapBytes; i--)
                {
                    Mark(zips[i], "size-cap");
                }

                var newestArchive = tars.Count > 0 ? tars[0].Name : null;
                var archivesLeft = tars.Count(t => !marked.Contains(t.Name));
                for (var i = tars.Count - 1; i >= 0 && remaining > result.CapBytes; i--)
                {
                    var item = tars[i];
                    if (marked.Contains(item.Name) || item.Name == newestArchive || never.Contains(item.Name)) continue;
                    if (archivesLeft <= RetentionPolicy.MinArchivesForCap) break;
                    Mark(item, "size-cap");
                    archivesLeft--;
                }

                for (var i = shots.Count - 1; i >= 0 && remaining > result.CapBytes; i--)
                {
                    Mark(shots[i], "size-cap");
                }
            }

            result.TotalBytesAfter = remaining;
            return result;
        }

        /// <summary>Every regular file in the folder with its size (empty when the folder does not exist).</summary>
        public static List<FolderFile> ListFolder(string folder)
        {
            var list = new List<FolderFile>();
            if (!Directory.Exists(folder)) return list;
            foreach (var info in new DirectoryInfo(folder).GetFiles())
            {
                list.Add(new FolderFile { Name = info.Name, Bytes = info.Length });
            }

            return list;
        }

        /// <summary>
        /// Plans and (unless <paramref name="dryRun"/>) deletes. A file that is in use stays and is reported in <see cref="RetentionResult.Failed"/>;
        /// the next run tries again. The freed bytes are those of the deletions that worked.
        /// </summary>
        public static RetentionResult Run(string folder, DateTime nowUtc, RetentionPolicy policy, IEnumerable<string> neverDelete, bool includeLegacyZips, bool dryRun)
        {
            lock (Gate)
            {
                var plan = Plan(ListFolder(folder), nowUtc, policy, neverDelete, includeLegacyZips);
                plan.DryRun = dryRun;
                if (dryRun) return plan;

                var done = new List<PlannedDeletion>();
                long freed = 0;
                foreach (var deletion in plan.Deleted)
                {
                    try
                    {
                        File.Delete(Path.Combine(folder, deletion.Name));
                        if (deletion.Sidecar != null) File.Delete(Path.Combine(folder, deletion.Sidecar));
                        done.Add(deletion);
                        freed += deletion.Bytes;
                    }
                    catch (IOException)
                    {
                        plan.Failed.Add(deletion);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        plan.Failed.Add(deletion);
                    }
                }

                plan.Deleted = done;
                plan.TotalBytesAfter = plan.TotalBytesBefore - freed;
                return plan;
            }
        }
    }
}
