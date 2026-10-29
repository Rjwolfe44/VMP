using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Server.System.Backup
{
    public sealed class BackupResult
    {
        public string ArchivePath;
        public string FileName;
        public string ChecksumPath;
        public string Sha256;
        public int Files;
        public long RawBytes;
        public long CompressedBytes;
        public double Seconds;
        public int Level;
        public bool Verified;
        public List<string> Excluded = new List<string>();
        public List<string> Skipped = new List<string>();

        public double Ratio => CompressedBytes > 0 ? (double)RawBytes / CompressedBytes : 0;

        /// <summary>One line: files, raw MB, compressed MB, ratio, seconds.</summary>
        public string Summary()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Format(inv, "files={0} raw={1:0.00}MB compressed={2:0.00}MB ratio={3:0.0}x seconds={4:0.0} level={5} verified={6}",
                Files, RawBytes / 1048576.0, CompressedBytes / 1048576.0, Ratio, Seconds, Level, Verified ? "yes" : "no");
        }
    }

    /// <summary>
    /// Packs Universe and the Config xml settings into <c>Universe-yyyyMMdd-HHmmss.tar.zst</c>: a tar stream through zstd (level 19 with
    /// long-distance matching and a window sized to the data, level 15 above 32 MB), content checksum on, written to a .part file,
    /// verified by reading it back, hashed into a .sha256 sidecar and only then renamed. Files are opened with FileShare.ReadWrite so a
    /// running server is never blocked; each file is read into memory in one go so its tar header length always matches its data.
    /// </summary>
    public static class BackupWriter
    {
        public const int DefaultLevel = 19;
        public const int FastLevel = 15;
        // Level 19 took about 10 s for 22 MB on the dev PC (level 15: 1 s), so above 32 MB the faster level keeps a backup under ~15 s.
        public const long FastLevelAboveBytes = 32L * 1024 * 1024;
        private const int IoBuffer = 128 * 1024;

        /// <param name="baseDirectory">The folder that holds Universe and Config (the server folder).</param>
        /// <param name="level">0 = choose by size.</param>
        public static BackupResult Create(string baseDirectory, string outputFolder, DateTime nowUtc, int level = 0, bool includeScreenshots = false)
        {
            var clock = Stopwatch.StartNew();
            var plan = BackupSourceSelector.Build(baseDirectory, includeScreenshots);
            if (level <= 0) level = plan.TotalBytes > FastLevelAboveBytes ? FastLevel : DefaultLevel;

            Directory.CreateDirectory(outputFolder);
            var name = UniqueName(outputFolder, nowUtc);
            var finalPath = Path.Combine(outputFolder, name);
            var partPath = finalPath + BackupNaming.PartExtension;

            var result = new BackupResult { FileName = name, ArchivePath = finalPath, Level = level };
            result.Excluded.AddRange(plan.Excluded);

            try
            {
                using (var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, IoBuffer, FileOptions.SequentialScan))
                {
                    using (var zstd = new CompressionStream(file, level, IoBuffer, true))
                    {
                        Configure(zstd, plan.TotalBytes);
                        using (var tar = new TarWriter(zstd, TarEntryFormat.Gnu, true))
                        {
                            foreach (var entry in plan.Entries)
                            {
                                byte[] buffer;
                                int read;
                                var rented = entry.Content == null;
                                if (rented)
                                {
                                    if (!TryReadFile(entry.FullPath, out buffer, out read, out var problem))
                                    {
                                        result.Skipped.Add(entry.ArchiveName + " (" + problem + ")");
                                        continue;
                                    }
                                }
                                else
                                {
                                    buffer = entry.Content;
                                    read = buffer.Length;
                                }

                                try
                                {
                                    var tarEntry = new GnuTarEntry(TarEntryType.RegularFile, entry.ArchiveName)
                                    {
                                        ModificationTime = new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteUtc, DateTimeKind.Utc)),
                                        DataStream = new MemoryStream(buffer, 0, read, false),
                                    };
                                    tar.WriteEntry(tarEntry);
                                }
                                finally
                                {
                                    if (rented) ArrayPool<byte>.Shared.Return(buffer);
                                }

                                result.Files++;
                                result.RawBytes += read;
                            }
                        }
                    }

                    file.Flush(true);
                }

                result.CompressedBytes = new FileInfo(partPath).Length;

                // Read it back before it gets its real name: every entry must be there and the data sizes must add up.
                var scan = BackupReader.ScanTarZst(partPath);
                if (scan.Entries != result.Files || scan.Bytes != result.RawBytes)
                {
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                        "verification failed: packed {0} files / {1} bytes, read back {2} files / {3} bytes",
                        result.Files, result.RawBytes, scan.Entries, scan.Bytes));
                }

                result.Verified = true;

                string hash;
                using (var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBuffer, FileOptions.SequentialScan))
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }

                File.Move(partPath, finalPath);
                result.Sha256 = hash;
                result.ChecksumPath = finalPath + BackupNaming.ChecksumExtension;
                File.WriteAllText(result.ChecksumPath, hash + "  " + name + "\n");
            }
            catch
            {
                TryDelete(partPath);
                throw;
            }

            result.Seconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        private static void Configure(CompressionStream stream, long totalBytes)
        {
            // Window big enough to cover the whole data set (2^20 .. 2^27) so repeats across files are found, without paying for more.
            var windowLog = 20;
            while (windowLog < 27 && (1L << windowLog) < totalBytes) windowLog++;

            stream.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            stream.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            stream.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        }

        private static string UniqueName(string folder, DateTime nowUtc)
        {
            var time = nowUtc;
            for (var i = 0; i < 300; i++)
            {
                var name = BackupNaming.MakeName(time);
                var path = Path.Combine(folder, name);
                if (!File.Exists(path) && !File.Exists(path + BackupNaming.PartExtension)) return name;
                time = time.AddSeconds(1);
            }

            throw new IOException("Could not find a free backup file name in " + folder);
        }

        internal static bool TryReadFile(string path, out byte[] buffer, out int read, out string problem)
        {
            buffer = null;
            read = 0;
            problem = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                    {
                        var length = stream.Length;
                        if (length > int.MaxValue - 1024)
                        {
                            problem = "larger than 2 GB";
                            return false;
                        }

                        var rented = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, length));
                        var total = 0;
                        while (total < length)
                        {
                            var n = stream.Read(rented, total, (int)length - total);
                            if (n == 0) break;
                            total += n;
                        }

                        buffer = rented;
                        read = total;
                        return true;
                    }
                }
                catch (FileNotFoundException)
                {
                    problem = "vanished";
                    return false;
                }
                catch (DirectoryNotFoundException)
                {
                    problem = "vanished";
                    return false;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    problem = e.Message;
                    if (attempt < 2) Thread.Sleep(100);
                }
            }

            return false;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
