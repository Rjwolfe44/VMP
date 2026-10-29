using LmpCommon.Compression;
using LmpCommon.PersistentSync;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Measures QuickLZ and zstd (levels 1, 3, 5, 9, 15, 19) on real vessel, kerbal and scenario files. Skipped unless the folders are named
    /// in environment variables, so no game data lives in the repository:
    /// VMP_BENCH_VESSELS, VMP_BENCH_KERBALS, VMP_BENCH_SCENARIOS (folders with the files); VMP_BENCH_OUT (optional file for the table).
    /// Run: dotnet test ServerTest --filter CompressionBenchmark
    /// </summary>
    [TestClass]
    public class CompressionBenchmark
    {
        private static readonly int[] Levels = { 1, 3, 5, 9, 12, 13, 15, 19 };

        private static List<byte[]> Load(string variable)
        {
            var folder = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            return Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllBytes).Where(b => b.Length > 0).ToList();
        }

        private sealed class Result
        {
            public long CompressedBytes;
            public double CompressMsPerMb;
            public double DecompressMsPerMb;
            public double AvgCompressMsPerFile;
        }

        private static Result Measure(List<byte[]> files, Func<byte[], byte[]> compress, Func<byte[], byte[]> decompress)
        {
            var total = files.Sum(f => (long)f.Length);
            var reps = (int)Math.Max(1, Math.Min(2000, 3L * 1024 * 1024 / Math.Max(1, total)));
            var compressed = files.Select(compress).ToList(); // warm-up and the sizes
            foreach (var pair in files.Zip(compressed, (raw, packed) => new { raw, packed }))
            {
                if (!pair.raw.SequenceEqual(decompress(pair.packed))) throw new InvalidOperationException("round trip failed");
            }

            var watch = Stopwatch.StartNew();
            for (var r = 0; r < reps; r++)
            {
                foreach (var file in files) compress(file);
            }

            var compressMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (var r = 0; r < reps; r++)
            {
                foreach (var packed in compressed) decompress(packed);
            }

            var decompressMs = watch.Elapsed.TotalMilliseconds;
            var megabytes = total * (double)reps / (1024 * 1024);
            return new Result
            {
                CompressedBytes = compressed.Sum(c => (long)c.Length),
                CompressMsPerMb = compressMs / megabytes,
                DecompressMsPerMb = decompressMs / megabytes,
                AvgCompressMsPerFile = compressMs / (files.Count * (double)reps),
            };
        }

        private static byte[] Zstd(byte[] raw, int level)
        {
            byte[] output;
            int length;
            if (!ZstdBackend.TryCompressAtLevel(raw, raw.Length, level, out output, out length)) throw new InvalidOperationException("zstd failed");
            return output.Take(length).ToArray();
        }

        private static byte[] UnZstd(byte[] frame)
        {
            byte[] raw;
            string error;
            if (!ZstdBackend.TryDecompressFrame(frame, frame.Length, CompressionLimits.MaxDecodedBytes, out raw, out error)) throw new InvalidOperationException(error);
            return raw;
        }

        private static void Row(StringBuilder table, string kind, string codec, long raw, Result r)
        {
            table.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0,-9} | {1,-9} | {2,7:0.00}x | {3,9:0.0} | {4,9:0.0} | {5,10:0.00} |",
                kind, codec, (double)raw / Math.Max(1, r.CompressedBytes), r.CompressMsPerMb, r.DecompressMsPerMb, r.AvgCompressMsPerFile));
        }

        [TestMethod]
        public void Benchmark_RealUniverseFiles()
        {
            var sets = new[] { Tuple.Create("Vessels", Load("VMP_BENCH_VESSELS")), Tuple.Create("Kerbals", Load("VMP_BENCH_KERBALS")), Tuple.Create("Scenarios", Load("VMP_BENCH_SCENARIOS")) }
                .Where(s => s.Item2 != null && s.Item2.Count > 0).ToList();
            if (sets.Count == 0) Assert.Inconclusive("Set VMP_BENCH_VESSELS, VMP_BENCH_KERBALS and VMP_BENCH_SCENARIOS to run the benchmark.");
            Assert.IsTrue(ZstdBackend.CanCompress, ZstdBackend.Error);

            var table = new StringBuilder();
            table.AppendLine("| set       | codec     |   ratio | comp ms/MB | decomp ms/MB | comp ms/file |");
            table.AppendLine("|-----------|-----------|---------|-----------|-----------|------------|");
            foreach (var set in sets)
            {
                var files = set.Item2;
                var raw = files.Sum(f => (long)f.Length);
                table.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} : {1} files, {2:0.00} MB raw, largest {3:0} KB, average {4:0.0} KB |", set.Item1, files.Count, raw / 1048576.0,
                    files.Max(f => f.Length) / 1024.0, raw / 1024.0 / files.Count));

                Row(table, set.Item1, "quicklz", raw, Measure(files, f =>
                {
                    byte[] output;
                    int written;
                    MessageCodec.TryQuickLzCompress(f, f.Length, out output, out written);
                    return output.Take(written).ToArray();
                }, f =>
                {
                    var copy = (byte[])f.Clone();
                    int length;
                    MessageCodec.Decompress(ref copy, f.Length, out length, null);
                    return copy.Take(length).ToArray();
                }));

                foreach (var level in Levels)
                {
                    var slow = level >= 15 && raw > 2 * 1024 * 1024;
                    Row(table, set.Item1, "zstd " + level, raw, Measure(slow ? files.OrderByDescending(f => f.Length).Take(Math.Max(3, files.Count / 10)).ToList() : files, f => Zstd(f, level), UnZstd));
                }
            }

            // What one big vessel costs at each level (the largest file alone).
            var vessels = sets.FirstOrDefault(s => s.Item1 == "Vessels");
            if (vessels != null)
            {
                var biggest = vessels.Item2.OrderByDescending(f => f.Length).First();
                table.AppendLine();
                table.AppendLine(string.Format(CultureInfo.InvariantCulture, "largest vessel: {0:0} KB", biggest.Length / 1024.0));
                foreach (var level in Levels)
                {
                    var r = Measure(new List<byte[]> { biggest }, f => Zstd(f, level), UnZstd);
                    table.AppendLine(string.Format(CultureInfo.InvariantCulture, "  zstd {0,2}: {1,6:0} KB ({2,5:0.00}x) in {3,7:0.00} ms, decode {4,6:0.00} ms", level, r.CompressedBytes / 1024.0,
                        (double)biggest.Length / r.CompressedBytes, r.AvgCompressMsPerFile, r.DecompressMsPerMb * biggest.Length / 1048576.0));
                }
            }

            var text = table.ToString();
            Console.WriteLine(text);
            var output = Environment.GetEnvironmentVariable("VMP_BENCH_OUT");
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, text);
        }
    }
}
