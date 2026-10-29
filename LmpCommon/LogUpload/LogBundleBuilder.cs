using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LmpCommon.LogUpload
{
    /// <summary>One file to put into the log bundle.</summary>
    public sealed class LogBundleSource
    {
        /// <summary>Name inside the zip (see <see cref="LogZipWriter.IsValidEntryName"/>).</summary>
        public string EntryName;

        /// <summary>File on disk.</summary>
        public string Path;

        /// <summary>Keep at most this many bytes: the end of the file, starting at a line boundary.</summary>
        public long MaxBytes = long.MaxValue;
    }

    public sealed class LogBundleOptions
    {
        public string OutputPath;

        /// <summary>Text for info.txt; the builder appends the list of files it packed.</summary>
        public string InfoText = string.Empty;

        public List<LogBundleSource> Sources = new List<LogBundleSource>();

        /// <summary>Account names to hide in paths (see <see cref="LogRedactor"/>).</summary>
        public IEnumerable<string> RedactNames = Enumerable.Empty<string>();

        /// <summary>Raw deflate compressor factory; null stores entries uncompressed.</summary>
        public Func<Stream, Stream> DeflateFactory;

        public DateTime Timestamp = DateTime.Now;
    }

    public sealed class LogBundleResult
    {
        public long ZipBytes;
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>
    /// Builds the "send logs to host" zip. Text logs are streamed line by line through <see cref="LogRedactor"/> into
    /// the zip, so a 150 MB KSP.log never sits in memory. Pure file IO: no Unity or KSP calls, safe on a worker thread.
    /// </summary>
    public static class LogBundleBuilder
    {
        public const string InfoEntryName = "info.txt";

        private static readonly string[] AllowedExtraExtensions = { ".log", ".txt", ".jsonl", ".json", ".csv" };
        private static readonly string[] ForbiddenNameParts = { "password", "secret", "token", "credential", "private" };

        /// <summary>
        /// True for files that must never leave the PC: VMP settings files (they hold server passwords) and anything
        /// whose name suggests a secret. Checked again for every source, not only when collecting folders.
        /// </summary>
        public static bool IsForbiddenFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            var name = System.IO.Path.GetFileName(fileName).ToLowerInvariant();
            if (name.StartsWith("settings", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".xml", StringComparison.Ordinal) || name.EndsWith(".cfg", StringComparison.Ordinal)) return true;
            return ForbiddenNameParts.Any(p => name.IndexOf(p, StringComparison.Ordinal) >= 0);
        }

        /// <summary>Makes a single zip path segment out of an arbitrary file name.</summary>
        public static string SanitizeEntryPart(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? string.Empty)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }

            var result = sb.ToString();
            while (result.Contains("..")) result = result.Replace("..", "_");
            result = result.Trim('.');
            if (result.Length > 80) result = result.Substring(result.Length - 80);
            return result.Length == 0 ? "file" : result;
        }

        /// <summary>
        /// Lists the newest log-like files directly inside <paramref name="folder"/> (allow-listed extensions only,
        /// never settings or secret-looking files).
        /// </summary>
        public static List<LogBundleSource> CollectFolder(string folder, string entryPrefix, int maxFiles, long maxBytesPerFile)
        {
            var result = new List<LogBundleSource>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return result;

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(folder).GetFiles();
            }
            catch (Exception)
            {
                return result;
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files
                .Where(f => AllowedExtraExtensions.Contains(f.Extension.ToLowerInvariant()) && !IsForbiddenFileName(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(0, maxFiles)))
            {
                var part = SanitizeEntryPart(file.Name);
                var candidate = part;
                for (var i = 2; !used.Add(candidate); i++)
                {
                    candidate = $"{i}_{part}";
                }

                result.Add(new LogBundleSource { EntryName = $"{entryPrefix}/{candidate}", Path = file.FullName, MaxBytes = maxBytesPerFile });
            }

            return result;
        }

        public static LogBundleResult Build(LogBundleOptions options, CancellationToken token, Action<long, long> progress)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.OutputPath)) throw new ArgumentException("Output path required", nameof(options));

            var redactor = new LogRedactor(options.RedactNames);
            var result = new LogBundleResult();
            var plannedTotal = options.Sources.Sum(s => PlannedBytes(s));
            long processedBefore = 0;

            var outputDirectory = System.IO.Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            using (var output = new FileStream(options.OutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024))
            {
                var zip = new LogZipWriter(output, options.DeflateFactory);
                foreach (var source in options.Sources)
                {
                    token.ThrowIfCancellationRequested();
                    var label = source.EntryName;
                    if (!LogZipWriter.IsValidEntryName(source.EntryName) || source.EntryName.Equals(InfoEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Lines.Add($"{label}: skipped (bad entry name)");
                        continue;
                    }

                    if (IsForbiddenFileName(source.Path))
                    {
                        result.Lines.Add($"{label}: skipped (never sent: may contain a password)");
                        continue;
                    }

                    if (!File.Exists(source.Path))
                    {
                        result.Lines.Add($"{label}: not found");
                        continue;
                    }

                    var planned = PlannedBytes(source);
                    FileStream file;
                    try
                    {
                        // KSP and Unity keep writing their logs while we read them.
                        file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        result.Lines.Add($"{label}: could not be opened ({e.GetType().Name})");
                        continue;
                    }

                    using (file)
                    {
                        var baseProcessed = processedBefore;
                        long originalLength = 0, skipped = 0;
                        // A read error part-way through an entry propagates: the caller reports it instead of uploading
                        // a half-written archive.
                        zip.AddEntry(source.EntryName, SafeLastWrite(source.Path), entryStream =>
                        {
                            CopyRedacted(file, source.MaxBytes, entryStream, redactor, token, out originalLength, out skipped,
                                done => progress?.Invoke(baseProcessed + done, plannedTotal));
                        });

                        result.Lines.Add(skipped > 0
                            ? $"{label}: last {FormatBytes(originalLength - skipped)} of {FormatBytes(originalLength)} (first {FormatBytes(skipped)} left out)"
                            : $"{label}: {FormatBytes(originalLength)}");
                    }

                    processedBefore += planned;
                    progress?.Invoke(processedBefore, plannedTotal);
                }

                var info = new StringBuilder();
                foreach (var line in (options.InfoText ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
                {
                    info.Append(redactor.RedactLine(line)).Append('\n');
                }

                info.Append('\n').Append("Files in this bundle:\n");
                foreach (var line in result.Lines)
                {
                    info.Append("  ").Append(redactor.RedactLine(line)).Append('\n');
                }

                info.Append(redactor.HasNames
                    ? "Your Windows account name in paths was replaced with " + LogRedactor.Placeholder + ".\n"
                    : "No account name was available to hide.\n");
                info.Append("VMP settings files (server passwords) are never included.\n");

                var infoBytes = new UTF8Encoding(false).GetBytes(info.ToString());
                zip.AddEntry(InfoEntryName, options.Timestamp, s => s.Write(infoBytes, 0, infoBytes.Length));
                zip.Finish();
                result.ZipBytes = output.Length;
            }

            return result;
        }

        private static long PlannedBytes(LogBundleSource source)
        {
            try
            {
                var info = new FileInfo(source.Path);
                return info.Exists ? Math.Min(info.Length, source.MaxBytes) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try
            {
                return File.GetLastWriteTime(path);
            }
            catch (Exception)
            {
                return DateTime.Now;
            }
        }

        /// <summary>
        /// Streams the (tail of the) file into <paramref name="destination"/> line by line, redacting each line. Stops at
        /// the length the file had when we started, since the game may still be appending to it.
        /// </summary>
        private static void CopyRedacted(FileStream file, long maxBytes, Stream destination, LogRedactor redactor, CancellationToken token,
            out long originalLength, out long skippedBytes, Action<long> progress)
        {
            originalLength = file.Length;
            skippedBytes = 0;
            if (maxBytes >= 0 && originalLength > maxBytes)
            {
                file.Position = originalLength - maxBytes;
                SkipToNextLine(file, originalLength);
                skippedBytes = file.Position;
            }

            var bounded = new BoundedReadStream(file, originalLength - file.Position);
            var utf8 = new UTF8Encoding(false);
            using (var reader = new StreamReader(bounded, utf8, true, 64 * 1024))
            using (var writer = new StreamWriter(destination, utf8, 64 * 1024, true))
            {
                writer.NewLine = "\n";
                if (skippedBytes > 0)
                {
                    writer.WriteLine($"[VMP log upload: the first {skippedBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes of this file were left out to keep the upload small]");
                }

                string line;
                var lines = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    writer.WriteLine(redactor.RedactLine(line));
                    if (++lines % 4096 == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        progress?.Invoke(bounded.BytesRead);
                    }
                }
            }
        }

        private static void SkipToNextLine(FileStream file, long limit)
        {
            var start = file.Position;
            var buffer = new byte[64 * 1024];
            while (file.Position < limit)
            {
                var toRead = (int)Math.Min(buffer.Length, limit - file.Position);
                var read = file.Read(buffer, 0, toRead);
                if (read <= 0) break;
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                if (newline >= 0)
                {
                    file.Position = file.Position - read + newline + 1;
                    return;
                }
            }

            // One enormous line: keep it from where we landed.
            file.Position = start;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        }

        /// <summary>Read-only view that stops after a fixed number of bytes and counts what it handed out.</summary>
        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream _inner;
            private long _remaining;
            public long BytesRead { get; private set; }

            public BoundedReadStream(Stream inner, long length)
            {
                _inner = inner;
                _remaining = Math.Max(0, length);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
                if (read <= 0) return 0;
                _remaining -= read;
                BytesRead += read;
                return read;
            }

            protected override void Dispose(bool disposing) { /* the caller owns the file */ }
        }
    }
}
