using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.System.Backup
{
    public sealed class BackupEntry
    {
        public string FullPath;
        public string ArchiveName;
        public long Length;
        public DateTime LastWriteUtc;
        /// <summary>When set, these bytes are packed instead of the file (the sanitized copy of a settings file).</summary>
        public byte[] Content;
    }

    public sealed class BackupPlan
    {
        public readonly List<BackupEntry> Entries = new List<BackupEntry>();
        /// <summary>Human readable list of what was left out on purpose, with the reason.</summary>
        public readonly List<string> Excluded = new List<string>();
        public long TotalBytes;
        /// <summary>Files under Universe\Screenshots, whether or not they are part of this plan.</summary>
        public int ScreenshotFiles;
        public long ScreenshotBytes;
    }

    /// <summary>
    /// Decides what goes into the main archive: the Universe folder plus the Config\*.xml settings.
    /// Left out on purpose: Universe\Screenshots (already compressed PNGs; new ones go into a daily Screenshots-*.tar, or pass
    /// includeScreenshots), Config\admin-token.txt, and the original of every Config xml that holds a credential. Those xml files
    /// (GeneralSettings.xml always, any other one with a non-empty Password/Token/Secret/ApiKey element) are packed as
    /// Config\&lt;name&gt;.sanitized with those values blanked. Links (reparse points) are never followed.
    /// </summary>
    public static class BackupSourceSelector
    {
        public const string UniverseFolder = "Universe";
        public const string ConfigFolder = "Config";
        public const string ScreenshotsFolder = "Screenshots";
        public const string AdminTokenFileName = "admin-token.txt";
        public const string GeneralSettingsFileName = "GeneralSettings.xml";
        public const string SanitizedSuffix = ".sanitized";
        private const int MaxConfigBytes = 1024 * 1024;

        private static readonly Regex SecretElement = new Regex(
            @"<(?<tag>[A-Za-z0-9_]*(?:Password|Passwd|Token|Secret|ApiKey)[A-Za-z0-9_]*)>\s*[^<\s][^<]*</\k<tag>>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string ScreenshotsPath(string baseDirectory)
        {
            return Path.Combine(baseDirectory, UniverseFolder, ScreenshotsFolder);
        }

        /// <summary>Blanks the value of every Password/Token/Secret/ApiKey element. This is the one rule that decides what counts as a credential.</summary>
        public static string Sanitize(string xml, out List<string> blankedTags)
        {
            var tags = new List<string>();
            var clean = SecretElement.Replace(xml, match =>
            {
                var tag = match.Groups["tag"].Value;
                tags.Add(tag);
                return "<" + tag + "></" + tag + ">";
            });
            blankedTags = tags;
            return clean;
        }

        public static BackupPlan Build(string baseDirectory, bool includeScreenshots = false)
        {
            var plan = new BackupPlan();
            var universe = Path.Combine(baseDirectory, UniverseFolder);
            if (!Directory.Exists(universe)) throw new DirectoryNotFoundException("Universe folder not found: " + universe);

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
            };
            var screenshotPrefix = ScreenshotsFolder + Path.DirectorySeparatorChar;
            foreach (var path in Directory.EnumerateFiles(universe, "*", options))
            {
                if (Path.GetRelativePath(universe, path).StartsWith(screenshotPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    plan.ScreenshotFiles++;
                    try
                    {
                        plan.ScreenshotBytes += new FileInfo(path).Length;
                    }
                    catch (IOException)
                    {
                    }

                    if (!includeScreenshots) continue;
                }

                AddFile(plan, baseDirectory, path);
            }

            if (!includeScreenshots && plan.ScreenshotFiles > 0)
            {
                plan.Excluded.Add("Universe/Screenshots (" + plan.ScreenshotFiles + " files, " + (plan.ScreenshotBytes / 1048576.0).ToString("0.00", global::System.Globalization.CultureInfo.InvariantCulture)
                    + " MB; new ones go into a daily Screenshots-*.tar, or pass --screenshots)");
            }

            var config = Path.Combine(baseDirectory, ConfigFolder);
            if (Directory.Exists(config))
            {
                var configOptions = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                foreach (var path in Directory.EnumerateFiles(config, "*.xml", configOptions))
                {
                    AddConfigFile(plan, baseDirectory, path);
                }

                if (File.Exists(Path.Combine(config, AdminTokenFileName)))
                {
                    plan.Excluded.Add("Config/" + AdminTokenFileName + " (admin API token)");
                }
            }

            // Similar files next to each other compress much better (all vessel files, then all xml, ...).
            plan.Entries.Sort((a, b) =>
            {
                var byExtension = string.Compare(Path.GetExtension(a.ArchiveName), Path.GetExtension(b.ArchiveName), StringComparison.OrdinalIgnoreCase);
                return byExtension != 0 ? byExtension : string.CompareOrdinal(a.ArchiveName, b.ArchiveName);
            });
            return plan;
        }

        private static void AddConfigFile(BackupPlan plan, string baseDirectory, string path)
        {
            var name = Path.GetFileName(path);
            var general = string.Equals(name, GeneralSettingsFileName, StringComparison.OrdinalIgnoreCase);

            string text;
            if (!TryReadText(path, out text))
            {
                plan.Excluded.Add("Config/" + name + " (could not be read, or is over 1 MB, so it cannot be checked for credentials)");
                return;
            }

            var clean = Sanitize(text, out var blanked);
            if (!general && blanked.Count == 0)
            {
                AddFile(plan, baseDirectory, path);
                return;
            }

            if (SecretElement.IsMatch(clean))
            {
                plan.Excluded.Add("Config/" + name + " (credentials could not be blanked)");
                return;
            }

            var bytes = new UTF8Encoding(false).GetBytes(clean);
            plan.Entries.Add(new BackupEntry
            {
                FullPath = path,
                ArchiveName = "Config/" + name + SanitizedSuffix,
                Length = bytes.Length,
                LastWriteUtc = File.GetLastWriteTimeUtc(path),
                Content = bytes,
            });
            plan.TotalBytes += bytes.Length;
            plan.Excluded.Add("Config/" + name + " (original left out; Config/" + name + SanitizedSuffix + " has "
                + (blanked.Count > 0 ? string.Join(", ", blanked.Distinct()) : "no credentials") + " blanked)");
        }

        private static void AddFile(BackupPlan plan, string baseDirectory, string path)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) return;
            }
            catch (IOException)
            {
                return;
            }

            var relative = Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
            plan.Entries.Add(new BackupEntry { FullPath = path, ArchiveName = relative, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
            plan.TotalBytes += info.Length;
        }

        private static bool TryReadText(string path, out string text)
        {
            text = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxConfigBytes) return false;
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        text = reader.ReadToEnd();
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
