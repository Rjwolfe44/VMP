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

        private static readonly Regex ArchivePattern = new Regex(@"^Universe-(?<ts>\d{8}-\d{6})\.tar\.zst$", RegexOptions.CultureInvariant);
        private static readonly Regex ScreenshotPattern = new Regex(@"^Screenshots-(?<ts>\d{8}-\d{6})\.tar$", RegexOptions.CultureInvariant);

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
