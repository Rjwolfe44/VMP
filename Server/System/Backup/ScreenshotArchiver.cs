using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Server.System.Backup
{
    public sealed class ScreenshotArchiveResult
    {
        public string FileName;
        public string ArchivePath;
        public int Files;
        public long RawBytes;
        public long ArchiveBytes;
        public double Seconds;
        /// <summary>Only files written after this moment were packed; null for the first archive (everything).</summary>
        public DateTime? SinceUtc;
        public List<string> Skipped = new List<string>();
    }

    /// <summary>
    /// Screenshots are append-only PNGs that no compressor shrinks, so they stay out of the main archive. Once a day the ones that are
    /// new since the previous archive go into <c>Screenshots-yyyyMMdd-HHmmss.tar</c> (plain tar). "Since" is the time in the name of the
    /// newest Screenshots-*.tar, which is when that archive started scanning, so a file written while it ran is picked up by the next
    /// one (at worst twice, never missed). The first run takes everything. Only names matching that exact pattern are ever pruned.
    /// </summary>
    public static class ScreenshotArchiver
    {
        public static DateTime? NewestArchiveTime(string folder)
        {
            DateTime? newest = null;
            if (!Directory.Exists(folder)) return null;
            foreach (var path in Directory.GetFiles(folder))
            {
                if (BackupNaming.TryParseScreenshotArchive(Path.GetFileName(path), out var time) && (newest == null || time > newest.Value)) newest = time;
            }

            return newest;
        }

        public static bool IsDue(string folder, DateTime nowUtc, TimeSpan interval)
        {
            var newest = NewestArchiveTime(folder);
            return newest == null || nowUtc - newest.Value >= interval;
        }

        /// <summary>Packs the screenshots written since the previous archive; null when there is nothing new.</summary>
        public static ScreenshotArchiveResult CreateIfNew(string baseDirectory, string outputFolder, DateTime nowUtc)
        {
            var clock = Stopwatch.StartNew();
            var folder = BackupSourceSelector.ScreenshotsPath(baseDirectory);
            if (!Directory.Exists(folder)) return null;

            var since = NewestArchiveTime(outputFolder);
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            var fresh = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(folder, "*", options))
            {
                var info = new FileInfo(path);
                if (since == null || info.LastWriteTimeUtc > since.Value) fresh.Add(info);
            }

            if (fresh.Count == 0) return null;
            fresh.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

            Directory.CreateDirectory(outputFolder);
            var name = BackupNaming.MakeScreenshotName(nowUtc);
            var finalPath = Path.Combine(outputFolder, name);
            var partPath = finalPath + BackupNaming.PartExtension;
            if (File.Exists(finalPath)) return null;

            var result = new ScreenshotArchiveResult { FileName = name, ArchivePath = finalPath, SinceUtc = since };
            try
            {
                using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                using (var tar = new TarWriter(file, TarEntryFormat.Gnu, true))
                {
                    foreach (var info in fresh)
                    {
                        byte[] buffer;
                        int read;
                        string problem;
                        if (!BackupWriter.TryReadFile(info.FullName, out buffer, out read, out problem))
                        {
                            result.Skipped.Add(info.Name + " (" + problem + ")");
                            continue;
                        }

                        try
                        {
                            var relative = Path.GetRelativePath(baseDirectory, info.FullName).Replace('\\', '/');
                            tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, relative)
                            {
                                ModificationTime = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
                                DataStream = new MemoryStream(buffer, 0, read, false),
                            });
                        }
                        finally
                        {
                            global::System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                        }

                        result.Files++;
                        result.RawBytes += read;
                    }
                }

                var scan = BackupReader.ScanPlainTar(partPath);
                if (scan.Entries != result.Files || scan.Bytes != result.RawBytes)
                {
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "screenshot archive verification failed: packed {0} files / {1} bytes, read back {2} / {3}",
                        result.Files, result.RawBytes, scan.Entries, scan.Bytes));
                }

                if (result.Files == 0)
                {
                    File.Delete(partPath);
                    return null;
                }

                result.ArchiveBytes = new FileInfo(partPath).Length;
                File.Move(partPath, finalPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(partPath)) File.Delete(partPath);
                }
                catch (IOException)
                {
                }

                throw;
            }

            result.Seconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>The newest <paramref name="keepNewest"/> screenshot archives stay (and <paramref name="neverDelete"/>); everything else that matches the exact name pattern goes.</summary>
        public static List<string> SelectDeletions(IEnumerable<string> fileNames, int keepNewest, string neverDelete)
        {
            var archives = new List<KeyValuePair<DateTime, string>>();
            foreach (var name in fileNames)
            {
                if (BackupNaming.TryParseScreenshotArchive(name, out var time)) archives.Add(new KeyValuePair<DateTime, string>(time, name));
            }

            archives.Sort((a, b) =>
            {
                var byTime = b.Key.CompareTo(a.Key);
                return byTime != 0 ? byTime : string.CompareOrdinal(b.Value, a.Value);
            });

            return archives.Skip(Math.Max(0, keepNewest))
                .Select(a => a.Value)
                .Where(n => !string.Equals(n, neverDelete, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static List<string> ApplyRetention(string folder, int keepNewest, string neverDelete)
        {
            var deleted = new List<string>();
            if (!Directory.Exists(folder)) return deleted;

            foreach (var name in SelectDeletions(Directory.GetFiles(folder).Select(Path.GetFileName), keepNewest, neverDelete))
            {
                try
                {
                    File.Delete(Path.Combine(folder, name));
                    deleted.Add(name);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return deleted;
        }
    }
}
