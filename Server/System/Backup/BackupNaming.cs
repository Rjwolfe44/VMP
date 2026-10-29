using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Server.System.Backup
{
    /// <summary>
    /// File names of backups. The main archive keeps the prefix Universe-yyyyMMdd-HHmmss (UTC) like the old Compress-Archive zips, so
    /// scripts that list Backups\Universe-* still see the new archives. New screenshots go into Screenshots-yyyyMMdd-HHmmss.tar.
    /// </summary>
    public static class BackupNaming
    {
        public const string Prefix = "Universe-";
        public const string TarZstExtension = ".tar.zst";
        public const string ZipExtension = ".zip";
        public const string ChecksumExtension = ".sha256";
        public const string PartExtension = ".part";
        public const string ScreenshotPrefix = "Screenshots-";
        public const string ScreenshotExtension = ".tar";
        public const string TimestampFormat = "yyyyMMdd-HHmmss";

        // \A and \z (not ^ and $: $ would also match before a trailing newline) and [0-9] (not \d: that matches other scripts' digits); case-sensitive.
        private static readonly Regex ArchivePattern = new Regex(@"\AUniverse-(?<ts>[0-9]{8}-[0-9]{6})\.tar\.zst\z", RegexOptions.CultureInvariant);
        private static readonly Regex ZipPattern = new Regex(@"\AUniverse-(?<ts>[0-9]{8}-[0-9]{6})\.zip\z", RegexOptions.CultureInvariant);
        private static readonly Regex ScreenshotPattern = new Regex(@"\AScreenshots-(?<ts>[0-9]{8}-[0-9]{6})\.tar\z", RegexOptions.CultureInvariant);

        /// <summary>The three kinds of files in the Backups folder that the server manages (and may delete).</summary>
        public enum FileKind
        {
            /// <summary>Universe-yyyyMMdd-HHmmss.tar.zst</summary>
            TarZst,
            /// <summary>Universe-yyyyMMdd-HHmmss.zip, made by the old PowerShell script.</summary>
            LegacyZip,
            /// <summary>Screenshots-yyyyMMdd-HHmmss.tar</summary>
            Screenshots,
        }

        public static string MakeName(DateTime utc)
        {
            return Prefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + TarZstExtension;
        }

        public static string MakeScreenshotName(DateTime utc)
        {
            return ScreenshotPrefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ScreenshotExtension;
        }

        /// <summary>True for names produced by <see cref="MakeName"/> (exactly; sidecars, .part files and zips do not match).</summary>
        public static bool TryParseArchive(string fileName, out DateTime utc)
        {
            return TryParse(ArchivePattern, fileName, out utc);
        }

        /// <summary>True for names produced by <see cref="MakeScreenshotName"/> (exactly).</summary>
        public static bool TryParseScreenshotArchive(string fileName, out DateTime utc)
        {
            return TryParse(ScreenshotPattern, fileName, out utc);
        }

        /// <summary>
        /// Strict check for the delete and prune code: true only for exactly Universe-yyyyMMdd-HHmmss.tar.zst, Universe-yyyyMMdd-HHmmss.zip,
        /// Screenshots-yyyyMMdd-HHmmss.tar (valid date and time, case-sensitive, no path characters) or the same tar.zst / zip name plus
        /// ".sha256" (<paramref name="isSidecar"/>). Everything else, including .part files, is not a managed backup file.
        /// </summary>
        public static bool TryClassify(string fileName, out FileKind kind, out DateTime utc, out bool isSidecar)
        {
            kind = FileKind.TarZst;
            utc = default(DateTime);
            isSidecar = false;
            if (string.IsNullOrEmpty(fileName) || fileName.Length > 64) return false;

            var baseName = fileName;
            if (fileName.EndsWith(ChecksumExtension, StringComparison.Ordinal))
            {
                isSidecar = true;
                baseName = fileName.Substring(0, fileName.Length - ChecksumExtension.Length);
            }

            if (TryParse(ArchivePattern, baseName, out utc))
            {
                kind = FileKind.TarZst;
                return true;
            }

            if (TryParse(ZipPattern, baseName, out utc))
            {
                kind = FileKind.LegacyZip;
                return true;
            }

            if (!isSidecar && TryParse(ScreenshotPattern, baseName, out utc))
            {
                kind = FileKind.Screenshots;
                return true;
            }

            isSidecar = false;
            return false;
        }

        /// <summary>True for exactly the legacy zip name Universe-yyyyMMdd-HHmmss.zip.</summary>
        public static bool TryParseLegacyZip(string fileName, out DateTime utc)
        {
            return TryParse(ZipPattern, fileName, out utc);
        }

        private static bool TryParse(Regex pattern, string fileName, out DateTime utc)
        {
            utc = default(DateTime);
            if (string.IsNullOrEmpty(fileName)) return false;
            var match = pattern.Match(fileName);
            if (!match.Success) return false;
            return DateTime.TryParseExact(match.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
        }

        /// <summary>"tar.zst", "zip", "screenshots.tar" or null.</summary>
        public static string KindOf(string fileNameOrPath)
        {
            if (string.IsNullOrEmpty(fileNameOrPath)) return null;
            if (TryParseScreenshotArchive(Path.GetFileName(fileNameOrPath), out _)) return "screenshots.tar";
            if (fileNameOrPath.EndsWith(TarZstExtension, StringComparison.OrdinalIgnoreCase)) return "tar.zst";
            if (fileNameOrPath.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase)) return "zip";
            return null;
        }
    }
}
