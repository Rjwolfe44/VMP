using Server.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using ZstdSharp;

namespace Server.System.Backup
{
    /// <summary>An archive entry that would write outside the target folder or create a link.</summary>
    public sealed class UnsafeBackupEntryException : IOException
    {
        public UnsafeBackupEntryException(string message) : base(message)
        {
        }
    }

    public sealed class ScanResult
    {
        public int Entries;
        public long Bytes;
    }

    public sealed class VerifyResult
    {
        public bool Ok;
        public string Message = string.Empty;
        public int Entries;
        public long Bytes;
    }

    public sealed class RestoreResult
    {
        public int Files;
        public long RawBytes;
        public long ArchiveBytes;
        public double Seconds;
        /// <summary>Entries that were not written on purpose (a live credentials file is never overwritten).</summary>
        public List<string> Skipped = new List<string>();

        public string Summary()
        {
            var inv = CultureInfo.InvariantCulture;
            var ratio = ArchiveBytes > 0 ? (double)RawBytes / ArchiveBytes : 0;
            var line = string.Format(inv, "files={0} raw={1:0.00}MB compressed={2:0.00}MB ratio={3:0.0}x seconds={4:0.0}",
                Files, RawBytes / 1048576.0, ArchiveBytes / 1048576.0, ratio, Seconds);
            return Skipped.Count > 0 ? line + " skipped=[" + string.Join("; ", Skipped) + "]" : line;
        }
    }

    /// <summary>
    /// Verifies and restores .tar.zst backups and the old Compress-Archive .zip ones. Every entry name is checked before anything is
    /// written: absolute paths, drive letters, "..", unsafe file names and link entries (symlink/hardlink) are refused, and the final
    /// path must stay inside the target folder (no existing link in between either).
    /// </summary>
    public static class BackupReader
    {
        private const int IoBuffer = 128 * 1024;
        private const long MaxRestoreBytes = 64L * 1024 * 1024 * 1024;

        /// <summary>Reads a whole .tar.zst without keeping anything, counting entries and data bytes. Throws when the file is damaged.</summary>
        public static ScanResult ScanTarZst(string path)
        {
            var result = new ScanResult();
            var buffer = new byte[IoBuffer];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IoBuffer, FileOptions.SequentialScan))
            using (var zstd = new DecompressionStream(file))
            using (var tar = new TarReader(zstd, true))
            {
                TarEntry entry;
                while ((entry = tar.GetNextEntry(false)) != null)
                {
                    result.Entries++;
                    if (entry.DataStream == null) continue;
                    int n;
                    while ((n = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0) result.Bytes += n;
                }

                // The tar reader stops at its end marker; read on to the end of the zstd frame so its content checksum is checked too.
                while (zstd.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }

            return result;
        }

        /// <summary>Same as <see cref="ScanTarZst"/> for an uncompressed tar (the screenshot archives).</summary>
        public static ScanResult ScanPlainTar(string path)
        {
            var result = new ScanResult();
            var buffer = new byte[IoBuffer];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IoBuffer, FileOptions.SequentialScan))
            using (var tar = new TarReader(file, true))
            {
                TarEntry entry;
                while ((entry = tar.GetNextEntry(false)) != null)
                {
                    result.Entries++;
                    if (entry.DataStream == null) continue;
                    int n;
                    while ((n = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0) result.Bytes += n;
                }
            }

            return result;
        }

        public static VerifyResult Verify(string archivePath)
        {
            var result = new VerifyResult();
            try
            {
                var kind = BackupNaming.KindOf(archivePath);
                if (kind == "zip")
                {
                    using (var zip = ZipFile.OpenRead(archivePath))
                    {
                        var buffer = new byte[IoBuffer];
                        foreach (var entry in zip.Entries)
                        {
                            result.Entries++;
                            if (IsDirectoryName(entry.FullName)) continue;
                            using (var stream = entry.Open())
                            {
                                int n;
                                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) result.Bytes += n;
                            }
                        }
                    }
                }
                else if (kind == "screenshots.tar")
                {
                    var plain = ScanPlainTar(archivePath);
                    result.Entries = plain.Entries;
                    result.Bytes = plain.Bytes;
                }
                else if (kind == "tar.zst")
                {
                    var sidecar = archivePath + BackupNaming.ChecksumExtension;
                    if (File.Exists(sidecar))
                    {
                        var expected = ReadChecksum(sidecar);
                        string actual;
                        using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IoBuffer, FileOptions.SequentialScan))
                        {
                            actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                        }

                        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Message = "sha256 does not match " + Path.GetFileName(sidecar);
                            return result;
                        }
                    }

                    var scan = ScanTarZst(archivePath);
                    result.Entries = scan.Entries;
                    result.Bytes = scan.Bytes;
                }
                else
                {
                    result.Message = "unknown archive type (expected .tar.zst or .zip)";
                    return result;
                }

                result.Ok = true;
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Message = e.GetType().Name + ": " + e.Message;
            }

            return result;
        }

        /// <summary>Extracts into <paramref name="targetDirectory"/> (created if needed). Existing files are an error unless <paramref name="overwrite"/>.</summary>
        public static RestoreResult Restore(string archivePath, string targetDirectory, bool overwrite = false)
        {
            var clock = Stopwatch.StartNew();
            var kind = BackupNaming.KindOf(archivePath);
            if (kind == null) throw new InvalidDataException("unknown archive type (expected .tar.zst or .zip): " + archivePath);

            var check = Verify(archivePath);
            if (!check.Ok) throw new InvalidDataException("archive failed verification: " + check.Message);

            var root = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(root);
            var result = new RestoreResult { ArchiveBytes = new FileInfo(archivePath).Length };

            if (kind == "zip") RestoreZip(archivePath, root, overwrite, result);
            else if (kind == "screenshots.tar") RestorePlainTar(archivePath, root, overwrite, result);
            else RestoreTarZst(archivePath, root, overwrite, result);

            result.Seconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        private static void RestoreTarZst(string archivePath, string root, bool overwrite, RestoreResult result)
        {
            using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IoBuffer, FileOptions.SequentialScan))
            using (var zstd = new DecompressionStream(file))
            using (var tar = new TarReader(zstd, true))
            {
                ExtractEntries(tar, root, overwrite, result);
            }
        }

        private static void RestorePlainTar(string archivePath, string root, bool overwrite, RestoreResult result)
        {
            using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IoBuffer, FileOptions.SequentialScan))
            using (var tar = new TarReader(file, true))
            {
                ExtractEntries(tar, root, overwrite, result);
            }
        }

        private static void ExtractEntries(TarReader tar, string root, bool overwrite, RestoreResult result)
        {
            {
                TarEntry entry;
                while ((entry = tar.GetNextEntry(false)) != null)
                {
                    switch (entry.EntryType)
                    {
                        case TarEntryType.RegularFile:
                        case TarEntryType.V7RegularFile:
                        case TarEntryType.ContiguousFile:
                            WriteFile(root, entry.Name, entry.DataStream, overwrite, result);
                            break;
                        case TarEntryType.Directory:
                            Directory.CreateDirectory(ResolveSafePath(root, entry.Name));
                            break;
                        case TarEntryType.GlobalExtendedAttributes:
                            break;
                        case TarEntryType.SymbolicLink:
                        case TarEntryType.HardLink:
                            throw new UnsafeBackupEntryException("link entries are not allowed: " + entry.Name);
                        default:
                            throw new UnsafeBackupEntryException("unsupported entry type " + entry.EntryType + ": " + entry.Name);
                    }
                }
            }
        }

        private static void RestoreZip(string archivePath, string root, bool overwrite, RestoreResult result)
        {
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                // Check every name first so a hostile entry late in the file cannot leave a half restored folder behind.
                foreach (var entry in zip.Entries)
                {
                    if (IsZipLink(entry)) throw new UnsafeBackupEntryException("link entries are not allowed: " + entry.FullName);
                    ResolveSafePath(root, entry.FullName);
                }

                foreach (var entry in zip.Entries)
                {
                    if (IsDirectoryName(entry.FullName))
                    {
                        Directory.CreateDirectory(ResolveSafePath(root, entry.FullName));
                        continue;
                    }

                    using (var stream = entry.Open())
                    {
                        WriteFile(root, entry.FullName, stream, overwrite, result);
                    }
                }
            }
        }

        private static void WriteFile(string root, string entryName, Stream data, bool overwrite, RestoreResult result)
        {
            var destination = ResolveSafePath(root, entryName);
            if (IsProtectedLiveFile(root, destination) && File.Exists(destination))
            {
                result.Skipped.Add(Path.GetRelativePath(root, destination).Replace('\\', '/') + " (live credentials file, not overwritten)");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, IoBuffer))
            {
                if (data != null)
                {
                    var buffer = new byte[IoBuffer];
                    int n;
                    while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        result.RawBytes += n;
                        if (result.RawBytes > MaxRestoreBytes) throw new InvalidDataException("archive expands to more than 64 GB");
                        output.Write(buffer, 0, n);
                    }
                }
            }

            result.Files++;
        }

        /// <summary>Maps an archive entry name to a full path inside <paramref name="targetRoot"/>, or throws <see cref="UnsafeBackupEntryException"/>.</summary>
        public static string ResolveSafePath(string targetRoot, string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) throw new UnsafeBackupEntryException("empty entry name");

            var name = entryName.Replace('\\', '/');
            if (name.IndexOf('\0') >= 0) throw new UnsafeBackupEntryException("entry name contains a NUL: " + Printable(entryName));
            if (name.StartsWith("/", StringComparison.Ordinal) || (name.Length >= 2 && name[1] == ':'))
            {
                throw new UnsafeBackupEntryException("absolute path in archive: " + Printable(entryName));
            }

            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var clean = new List<string>();
            foreach (var segment in segments)
            {
                if (segment == ".") continue;
                if (segment == "..") throw new UnsafeBackupEntryException("path traversal in archive: " + Printable(entryName));
                if (!StoragePath.IsSafeName(segment)) throw new UnsafeBackupEntryException("unsafe file name in archive: " + Printable(entryName));
                clean.Add(segment);
            }

            if (clean.Count == 0) throw new UnsafeBackupEntryException("empty entry name: " + Printable(entryName));

            var rootFull = Path.GetFullPath(targetRoot);
            var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootFull, string.Join(Path.DirectorySeparatorChar.ToString(), clean)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(rootWithSeparator, comparison)) throw new UnsafeBackupEntryException("entry escapes the target folder: " + Printable(entryName));

            // A pre-existing link inside the target could still redirect the write elsewhere.
            var current = rootFull;
            for (var i = 0; i < clean.Count - 1; i++)
            {
                current = Path.Combine(current, clean[i]);
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnsafeBackupEntryException("target contains a link at " + clean[i]);
                }
            }

            return full;
        }

        /// <summary>Restoring never replaces a live Config/GeneralSettings.xml or Config/admin-token.txt, even with overwrite (old zips carry the real GeneralSettings.xml).</summary>
        private static bool IsProtectedLiveFile(string root, string destination)
        {
            var relative = Path.GetRelativePath(root, destination).Replace('\\', '/');
            return string.Equals(relative, "Config/GeneralSettings.xml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "Config/admin-token.txt", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectoryName(string fullName)
        {
            return fullName.EndsWith("/", StringComparison.Ordinal) || fullName.EndsWith("\\", StringComparison.Ordinal);
        }

        private static bool IsZipLink(ZipArchiveEntry entry)
        {
            // Unix mode lives in the top 16 bits of the external attributes; 0xA000 is a symlink.
            return ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
        }

        private static string ReadChecksum(string sidecar)
        {
            var text = File.ReadAllText(sidecar).Trim();
            var space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        private static string Printable(string name)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i])) chars[i] = '?';
            }

            var text = new string(chars);
            return text.Length > 120 ? text.Substring(0, 120) + "..." : text;
        }
    }
}
