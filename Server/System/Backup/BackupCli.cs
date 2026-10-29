using Server.Settings;
using Server.Settings.Definition;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Server.System.Backup
{
    /// <summary>
    /// The offline verbs of Server.exe, handled before anything else starts (no log, no network, nothing written to the settings):
    /// <c>Server.exe backup --out &lt;folder&gt; [--keep-newest N --keep-days D]</c> packs the Universe and Config folders next to the exe and then applies
    /// the retention of Config\vmp-server.json (defaults when it cannot be read; the two options override the keep counts),
    /// <c>Server.exe restore &lt;file&gt; --to &lt;folder&gt; [--overwrite]</c> extracts a .tar.zst or .zip,
    /// <c>Server.exe prune [--dir &lt;folder&gt;] [--dry-run] [--include-legacy-zips]</c> applies the same retention to a folder (default: Backups next to the exe)
    /// and prints the names it deleted (or would delete). Each prints one summary line (prune: plus one line per file) and the exit code is
    /// 0 on success, 1 on failure, 2 on bad arguments.
    /// </summary>
    public static class BackupCli
    {
        /// <returns>False when <paramref name="args"/> is not a backup/restore/prune verb (the server should start normally).</returns>
        public static bool TryRun(string[] args, TextWriter output, TextWriter error, string baseDirectory, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;

            var verb = args[0].ToLowerInvariant();
            if (verb != "backup" && verb != "restore" && verb != "prune") return false;

            try
            {
                var options = Parse(args);
                switch (verb)
                {
                    case "backup":
                        exitCode = RunBackup(options, output, error, baseDirectory);
                        break;
                    case "restore":
                        exitCode = RunRestore(options, output, error);
                        break;
                    default:
                        exitCode = RunPrune(options, output, error, baseDirectory);
                        break;
                }
            }
            catch (Exception e)
            {
                error.WriteLine(verb + " FAILED: " + e.Message);
                exitCode = 1;
            }

            return true;
        }

        private sealed class Options
        {
            public readonly List<string> Positional = new List<string>();
            public string Out;
            public string To;
            public string Dir;
            public bool Overwrite;
            public bool Screenshots;
            public bool DryRun;
            public bool IncludeLegacyZips;
            public int? KeepNewest;
            public int? KeepDays;
            public int Level;
        }

        private static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--out": options.Out = Next(args, ref i, arg); break;
                    case "--to": options.To = Next(args, ref i, arg); break;
                    case "--dir": options.Dir = Next(args, ref i, arg); break;
                    case "--overwrite": options.Overwrite = true; break;
                    case "--screenshots": options.Screenshots = true; break;
                    case "--dry-run": options.DryRun = true; break;
                    case "--include-legacy-zips": options.IncludeLegacyZips = true; break;
                    case "--keep-newest": options.KeepNewest = ParseInt(Next(args, ref i, arg), arg); break;
                    case "--keep-days": options.KeepDays = ParseInt(Next(args, ref i, arg), arg); break;
                    case "--level": options.Level = ParseInt(Next(args, ref i, arg), arg); break;
                    default:
                        if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unknown option " + arg);
                        options.Positional.Add(arg);
                        break;
                }
            }

            return options;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length) throw new ArgumentException(option + " needs a value");
            index++;
            return args[index];
        }

        private static int ParseInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
            {
                throw new ArgumentException(option + " needs a number, got '" + value + "'");
            }

            return number;
        }

        /// <summary>
        /// The retention numbers of Config\vmp-server.json (interval section) next to the exe, read without loading or writing anything else.
        /// Defaults when the file is missing or unreadable; the reason goes to <paramref name="error"/>.
        /// </summary>
        public static RetentionPolicy LoadPolicy(string baseDirectory, TextWriter error)
        {
            if (SettingsFile.TryReadSection<IntervalSettingsDefinition>(Path.Combine(baseDirectory, "Config"), "interval", out var interval, out var problem))
            {
                return RetentionPolicy.FromSettings(interval);
            }

            // No settings file yet is normal (fresh install); only a file that exists but cannot be read is worth a note.
            if (File.Exists(Path.Combine(baseDirectory, "Config", SettingsFile.FileName))) error?.WriteLine("note: retention settings not read (" + problem + "), using the defaults");
            return new RetentionPolicy();
        }

        private static int RunBackup(Options options, TextWriter output, TextWriter error, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(options.Out))
            {
                error.WriteLine("usage: Server.exe backup --out <folder> [--screenshots] [--keep-newest N --keep-days D]");
                return 2;
            }

            var outFolder = Path.GetFullPath(options.Out);
            var now = DateTime.UtcNow;
            var result = BackupWriter.Create(baseDirectory, outFolder, now, options.Level, options.Screenshots);

            var policy = LoadPolicy(baseDirectory, error);
            if (options.KeepNewest.HasValue) policy.KeepNewest = options.KeepNewest.Value;
            if (options.KeepDays.HasValue) policy.KeepDailyDays = options.KeepDays.Value;
            var retention = BackupRetention.Run(outFolder, now, policy, new[] { result.FileName }, false, false);

            var line = "backup ok: " + result.ArchivePath + " " + result.Summary() + " pruned=" + retention.Deleted.Count + " freed=" + retention.FreedBytes;
            if (result.Excluded.Count > 0) line += " excluded=[" + string.Join("; ", result.Excluded) + "]";
            if (result.Skipped.Count > 0) line += " skipped=[" + string.Join("; ", result.Skipped) + "]";
            output.WriteLine(line);
            return 0;
        }

        private static int RunPrune(Options options, TextWriter output, TextWriter error, string baseDirectory)
        {
            if (options.Positional.Count != 0)
            {
                error.WriteLine("usage: Server.exe prune [--dir <folder>] [--dry-run] [--include-legacy-zips]");
                return 2;
            }

            var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Dir) ? Path.Combine(baseDirectory, "Backups") : options.Dir);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("backup folder not found: " + folder);

            var policy = LoadPolicy(baseDirectory, error);
            var names = new List<string>();
            foreach (var file in BackupRetention.ListFolder(folder)) names.Add(file.Name);
            var newest = BackupRetention.NewestArchiveName(names);

            var result = BackupRetention.Run(folder, DateTime.UtcNow, policy, newest == null ? null : new[] { newest }, options.IncludeLegacyZips, options.DryRun);
            var cap = result.CapBytes > 0 ? (result.CapBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB" : "no cap";
            var line = (options.DryRun ? "prune dry-run: would delete " : "prune ok: deleted ") + result.Deleted.Count + " file(s), "
                       + (options.DryRun ? "would free " : "freed ") + result.FreedBytes + " bytes, folder " + result.TotalBytesAfter + " bytes after (cap " + cap + ") " + folder;
            if (result.StillOverCap) line += " STILL OVER CAP (the newest archive and at least " + RetentionPolicy.MinArchivesForCap + " archives are kept)";
            if (result.Failed.Count > 0) line += " could-not-delete=" + result.Failed.Count;
            output.WriteLine(line);
            foreach (var deleted in result.Deleted) output.WriteLine(deleted.Name);
            foreach (var failed in result.Failed) error.WriteLine("could not delete (in use): " + failed.Name);
            return result.Failed.Count > 0 && !options.DryRun ? 1 : 0;
        }

        private static int RunRestore(Options options, TextWriter output, TextWriter error)
        {
            if (options.Positional.Count != 1 || string.IsNullOrWhiteSpace(options.To))
            {
                error.WriteLine("usage: Server.exe restore <file.tar.zst|file.zip> --to <folder> [--overwrite]");
                return 2;
            }

            var archive = Path.GetFullPath(options.Positional[0]);
            if (!File.Exists(archive)) throw new FileNotFoundException("archive not found: " + archive);

            var target = Path.GetFullPath(options.To);
            var result = BackupReader.Restore(archive, target, options.Overwrite);
            output.WriteLine("restore ok: " + archive + " -> " + target + " " + result.Summary());
            return 0;
        }
    }
}
