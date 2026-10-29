using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Server.System.Backup
{
    /// <summary>
    /// The offline verbs of Server.exe, handled before anything else starts (no settings, no log, no network):
    /// <c>Server.exe backup --out &lt;folder&gt; [--keep-newest N --keep-days D]</c> packs the Universe and Config folders next to the exe,
    /// <c>Server.exe restore &lt;file&gt; --to &lt;folder&gt; [--overwrite]</c> extracts a .tar.zst or .zip. Each prints one summary line
    /// and the exit code is 0 on success, 1 on failure, 2 on bad arguments.
    /// </summary>
    public static class BackupCli
    {
        /// <returns>False when <paramref name="args"/> is not a backup/restore verb (the server should start normally).</returns>
        public static bool TryRun(string[] args, TextWriter output, TextWriter error, string baseDirectory, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;

            var verb = args[0].ToLowerInvariant();
            if (verb != "backup" && verb != "restore") return false;

            try
            {
                var options = Parse(args);
                exitCode = verb == "backup" ? RunBackup(options, output, error, baseDirectory) : RunRestore(options, output, error);
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
            public bool Overwrite;
            public bool Screenshots;
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
                    case "--overwrite": options.Overwrite = true; break;
                    case "--screenshots": options.Screenshots = true; break;
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

            var pruned = 0;
            if (options.KeepNewest.HasValue || options.KeepDays.HasValue)
            {
                pruned = BackupRetention.Apply(outFolder, now, options.KeepNewest ?? 10, options.KeepDays ?? 14, result.FileName).Count;
            }

            var line = "backup ok: " + result.ArchivePath + " " + result.Summary() + " pruned=" + pruned;
            if (result.Excluded.Count > 0) line += " excluded=[" + string.Join("; ", result.Excluded) + "]";
            if (result.Skipped.Count > 0) line += " skipped=[" + string.Join("; ", result.Skipped) + "]";
            output.WriteLine(line);
            return 0;
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
