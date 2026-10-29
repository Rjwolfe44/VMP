using LmpCommon.LogUpload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace LmpCommonTest
{
    [TestClass]
    public class LogUploadBundleTests
    {
        private static readonly Func<Stream, Stream> Deflate = s => new DeflateStream(s, CompressionMode.Compress, true);

        private string _dir;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vmp-logbundle-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private static Dictionary<string, string> ReadZipText(string path)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                return archive.Entries.ToDictionary(e => e.FullName, e =>
                {
                    using (var reader = new StreamReader(e.Open(), Encoding.UTF8))
                        return reader.ReadToEnd();
                });
            }
        }

        #region Zip writer

        [TestMethod]
        public void Crc32MatchesTheStandardCheckValue()
        {
            Assert.AreEqual(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        }

        [TestMethod]
        public void DeflatedAndStoredZipsAreReadableAndStartWithTheMagicTheServerChecks()
        {
            var binary = new byte[200_000];
            new Random(3).NextBytes(binary);
            var text = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"[LOG 12:00:{i % 60:00}] line {i}"));

            foreach (var factory in new[] { Deflate, null })
            {
                var path = Path.Combine(_dir, factory == null ? "stored.zip" : "deflated.zip");
                using (var output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                {
                    var zip = new LogZipWriter(output, factory);
                    zip.AddEntry("KSP.log", new DateTime(2026, 10, 24, 12, 30, 10), s => { var b = Encoding.UTF8.GetBytes(text); s.Write(b, 0, b.Length); });
                    zip.AddEntry("VladMultiplayer/Traces/blob.bin", DateTime.Now, s => s.Write(binary, 0, binary.Length));
                    zip.AddEntry("empty.txt", DateTime.Now, s => { });
                    zip.Finish();
                }

                var bytes = File.ReadAllBytes(path);
                CollectionAssert.AreEqual(new byte[] { (byte)'P', (byte)'K', 3, 4 }, bytes.Take(4).ToArray());
                // CRC patched into the first local header.
                Assert.AreEqual(Crc32.Compute(Encoding.UTF8.GetBytes(text)), BitConverter.ToUInt32(bytes, 14));

                using (var archive = ZipFile.OpenRead(path))
                {
                    CollectionAssert.AreEqual(new[] { "KSP.log", "VladMultiplayer/Traces/blob.bin", "empty.txt" }, archive.Entries.Select(e => e.FullName).ToArray());
                    using (var reader = new StreamReader(archive.GetEntry("KSP.log").Open()))
                        Assert.AreEqual(text, reader.ReadToEnd());
                    using (var ms = new MemoryStream())
                    {
                        archive.GetEntry("VladMultiplayer/Traces/blob.bin").Open().CopyTo(ms);
                        CollectionAssert.AreEqual(binary, ms.ToArray());
                    }

                    Assert.AreEqual(0, archive.GetEntry("empty.txt").Length);
                    Assert.AreEqual(new DateTime(2026, 10, 24, 12, 30, 10), archive.GetEntry("KSP.log").LastWriteTime.DateTime);
                }

                if (factory != null)
                    Assert.IsTrue(bytes.Length < text.Length + binary.Length, "deflate actually compressed the text");
            }
        }

        [DataTestMethod]
        [DataRow("../evil.txt")]
        [DataRow("/abs.txt")]
        [DataRow("a\\b.txt")]
        [DataRow("C:x.txt")]
        [DataRow("a//b.txt")]
        [DataRow("a/../b.txt")]
        [DataRow("dir/")]
        [DataRow("")]
        [DataRow("name with space.txt")]
        public void ZipWriterRejectsUnsafeEntryNames(string name)
        {
            Assert.IsFalse(LogZipWriter.IsValidEntryName(name));
            using (var ms = new MemoryStream())
            {
                var zip = new LogZipWriter(ms, null);
                Assert.ThrowsException<ArgumentException>(() => zip.AddEntry(name, DateTime.Now, s => { }));
            }
        }

        [TestMethod]
        public void ZipWriterRejectsDuplicateNames()
        {
            using (var ms = new MemoryStream())
            {
                var zip = new LogZipWriter(ms, null);
                zip.AddEntry("info.txt", DateTime.Now, s => { });
                Assert.ThrowsException<ArgumentException>(() => zip.AddEntry("INFO.txt", DateTime.Now, s => { }));
            }
        }

        #endregion

        #region Redaction

        [DataTestMethod]
        [DataRow(@"Loading C:\Users\Alex\Games\KSP\GameData\x.dll", @"Loading C:\Users\<user>\Games\KSP\GameData\x.dll")]
        [DataRow(@"path C:/Users/alex/AppData/LocalLow/Squad", @"path C:/Users/<user>/AppData/LocalLow/Squad")]
        [DataRow(@"{""path"":""C:\\Users\\Alex\\KSP""}", @"{""path"":""C:\\Users\\<user>\\KSP""}")]
        [DataRow(@"/home/alex/.config/unity3d/Player.log", @"/home/<user>/.config/unity3d/Player.log")]
        [DataRow(@"/Users/Alex/Library/Logs", @"/Users/<user>/Library/Logs")]
        [DataRow(@"C:\Users\Alex", @"C:\Users\<user>")]
        [DataRow(@"two C:\Users\Alex\a and D:\Users\ALEX\b", @"two C:\Users\<user>\a and D:\Users\<user>\b")]
        [DataRow(@"C:\Users\Alex2\KSP", @"C:\Users\Alex2\KSP")]
        [DataRow(@"C:\Users\Public\KSP", @"C:\Users\Public\KSP")]
        [DataRow(@"Alex joined the game", @"Alex joined the game")]
        [DataRow(@"C:\Users\Sam Doe\Desktop\VMP Server", @"C:\Users\<user>\Desktop\VMP Server")]
        [DataRow("", "")]
        public void RedactorHidesTheAccountNameInProfilePaths(string input, string expected)
        {
            var redactor = new LogRedactor(new[] { "Alex", "Sam Doe", null, "" });
            Assert.AreEqual(expected, redactor.RedactLine(input));
        }

        [TestMethod]
        public void RedactorWithoutNamesLeavesTextAlone()
        {
            var redactor = new LogRedactor(new string[0]);
            Assert.IsFalse(redactor.HasNames);
            Assert.AreEqual(@"C:\Users\Alex\x", redactor.RedactLine(@"C:\Users\Alex\x"));
        }

        [TestMethod]
        public void RedactorTreatsRegexCharactersInNamesLiterally()
        {
            var redactor = new LogRedactor(new[] { "a.b(c)" });
            Assert.AreEqual(@"C:\Users\<user>\x", redactor.RedactLine(@"C:\Users\a.b(c)\x"));
            Assert.AreEqual(@"C:\Users\aXb(c)\x", redactor.RedactLine(@"C:\Users\aXb(c)\x"));
        }

        #endregion

        #region Bundle builder

        [TestMethod]
        public void BundleContainsTheLogsRedactedAndNeverTheSettingsFiles()
        {
            var kspLog = Path.Combine(_dir, "KSP.log");
            File.WriteAllText(kspLog, "[LOG] KSP root C:\\Users\\Alex\\KSP\n[LOG] all good\n");
            var playerLog = Path.Combine(_dir, "Player.log");
            File.WriteAllText(playerLog, "Mono path[0] = 'C:/Users/Alex/KSP/KSP_x64_Data/Managed'\n");

            var vmp = Path.Combine(_dir, "GameData", "VladMultiplayer");
            var traces = Path.Combine(vmp, "Traces");
            var data = Path.Combine(vmp, "Data");
            Directory.CreateDirectory(traces);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(traces, "sync-20261024-120000.jsonl"), "{\"file\":\"C:\\\\Users\\\\Alex\\\\x\"}\n");
            File.WriteAllText(Path.Combine(traces, "settings.xml"), "<Password>hunter2</Password>");
            File.WriteAllText(Path.Combine(traces, "settings_bkp.xml"), "<Password>hunter2</Password>");
            File.WriteAllText(Path.Combine(traces, "settings-copy.txt"), "Password=hunter2");
            File.WriteAllText(Path.Combine(traces, "api-token.txt"), "token");
            File.WriteAllText(Path.Combine(traces, "picture.png"), "png");
            File.WriteAllText(Path.Combine(data, "settings.xml"), "<Password>hunter2</Password>");

            var sources = new List<LogBundleSource>
            {
                new LogBundleSource { EntryName = "KSP.log", Path = kspLog },
                new LogBundleSource { EntryName = "Player.log", Path = playerLog },
                // Even if a caller passed the settings file explicitly, it must be skipped.
                new LogBundleSource { EntryName = "settings.xml", Path = Path.Combine(data, "settings.xml") },
                new LogBundleSource { EntryName = "missing.log", Path = Path.Combine(_dir, "nope.log") },
            };
            sources.AddRange(LogBundleBuilder.CollectFolder(traces, "VladMultiplayer/Traces", 20, 1024 * 1024));
            sources.AddRange(LogBundleBuilder.CollectFolder(Path.Combine(vmp, "logs"), "VladMultiplayer/logs", 20, 1024 * 1024));

            var zipPath = Path.Combine(_dir, "out", "bundle.zip");
            var result = LogBundleBuilder.Build(new LogBundleOptions
            {
                OutputPath = zipPath,
                InfoText = "VMP log upload\nPlayer: Jeb\nKSP folder: C:\\Users\\Alex\\KSP\n",
                Sources = sources,
                RedactNames = new[] { "Alex" },
                DeflateFactory = Deflate,
            }, CancellationToken.None, null);

            Assert.AreEqual(new FileInfo(zipPath).Length, result.ZipBytes);
            var entries = ReadZipText(zipPath);
            CollectionAssert.AreEquivalent(new[] { "KSP.log", "Player.log", "VladMultiplayer/Traces/sync-20261024-120000.jsonl", "info.txt" }, entries.Keys.ToArray());
            Assert.AreEqual("[LOG] KSP root C:\\Users\\<user>\\KSP\n[LOG] all good\n", entries["KSP.log"]);
            Assert.AreEqual("Mono path[0] = 'C:/Users/<user>/KSP/KSP_x64_Data/Managed'\n", entries["Player.log"]);
            Assert.AreEqual("{\"file\":\"C:\\\\Users\\\\<user>\\\\x\"}\n", entries["VladMultiplayer/Traces/sync-20261024-120000.jsonl"]);

            var info = entries["info.txt"];
            StringAssert.Contains(info, "Player: Jeb");
            StringAssert.Contains(info, "KSP folder: C:\\Users\\<user>\\KSP");
            StringAssert.Contains(info, "settings.xml: skipped");
            StringAssert.Contains(info, "missing.log: not found");
            Assert.IsFalse(entries.Values.Any(v => v.Contains("hunter2") || v.Contains("Alex")), "no password and no account name anywhere");
        }

        [TestMethod]
        public void LargeLogKeepsOnlyTheTailFromALineBoundary()
        {
            var kspLog = Path.Combine(_dir, "KSP.log");
            var lines = Enumerable.Range(0, 20000).Select(i => $"[LOG 12:00:00.000] line number {i:00000} with some padding text").ToList();
            File.WriteAllText(kspLog, string.Join("\n", lines) + "\n");
            var original = new FileInfo(kspLog).Length;
            const long keep = 100_000;

            var zipPath = Path.Combine(_dir, "bundle.zip");
            LogBundleBuilder.Build(new LogBundleOptions
            {
                OutputPath = zipPath,
                Sources = new List<LogBundleSource> { new LogBundleSource { EntryName = "KSP.log", Path = kspLog, MaxBytes = keep } },
                DeflateFactory = Deflate,
            }, CancellationToken.None, null);

            var text = ReadZipText(zipPath)["KSP.log"];
            var outLines = text.TrimEnd('\n').Split('\n');
            StringAssert.StartsWith(outLines[0], "[VMP log upload: the first ");
            Assert.IsTrue(lines.Contains(outLines[1]), "the first kept line is a whole line: " + outLines[1]);
            Assert.AreEqual(lines.Last(), outLines.Last());
            var keptBytes = outLines.Skip(1).Sum(l => l.Length + 1);
            Assert.IsTrue(keptBytes <= keep && keptBytes > keep - 200, $"kept {keptBytes} of {original}");

            StringAssert.Contains(ReadZipText(zipPath)["info.txt"], "left out");
        }

        [TestMethod]
        public void ReadsALogThatTheGameStillHasOpenForWriting()
        {
            var kspLog = Path.Combine(_dir, "KSP.log");
            using (var writer = new FileStream(kspLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var first = Encoding.UTF8.GetBytes("[LOG] still running\n");
                writer.Write(first, 0, first.Length);
                writer.Flush();

                var zipPath = Path.Combine(_dir, "bundle.zip");
                LogBundleBuilder.Build(new LogBundleOptions
                {
                    OutputPath = zipPath,
                    Sources = new List<LogBundleSource> { new LogBundleSource { EntryName = "KSP.log", Path = kspLog } },
                    DeflateFactory = Deflate,
                }, CancellationToken.None, null);

                Assert.AreEqual("[LOG] still running\n", ReadZipText(zipPath)["KSP.log"]);
            }
        }

        [TestMethod]
        public void CollectFolderTakesOnlyTheNewestAllowedFiles()
        {
            var folder = Path.Combine(_dir, "Traces");
            Directory.CreateDirectory(folder);
            for (var i = 0; i < 6; i++)
            {
                var path = Path.Combine(folder, $"trace{i}.jsonl");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
            }

            File.WriteAllText(Path.Combine(folder, "weird name (1).log"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "weird name (1).log"), DateTime.UtcNow.AddMinutes(-10));
            File.WriteAllText(Path.Combine(folder, "settings.xml"), "x");
            File.WriteAllText(Path.Combine(folder, "server.cfg"), "x");
            File.WriteAllText(Path.Combine(folder, "tool.exe"), "x");

            var sources = LogBundleBuilder.CollectFolder(folder, "VladMultiplayer/Traces", 3, 1000);
            CollectionAssert.AreEqual(new[] { "VladMultiplayer/Traces/trace5.jsonl", "VladMultiplayer/Traces/trace4.jsonl", "VladMultiplayer/Traces/trace3.jsonl" },
                sources.Select(s => s.EntryName).ToArray());
            Assert.IsTrue(sources.All(s => s.MaxBytes == 1000));

            var all = LogBundleBuilder.CollectFolder(folder, "VladMultiplayer/Traces", 100, 1000);
            Assert.AreEqual(7, all.Count);
            Assert.IsTrue(all.Any(s => s.EntryName == "VladMultiplayer/Traces/weird_name__1_.log"));
            Assert.IsTrue(all.All(s => LogZipWriter.IsValidEntryName(s.EntryName)));
            Assert.AreEqual(0, LogBundleBuilder.CollectFolder(Path.Combine(_dir, "nope"), "x", 10, 10).Count);
        }

        [DataTestMethod]
        [DataRow("settings.xml", true)]
        [DataRow("Settings_bkp.XML", true)]
        [DataRow("settings.txt", true)]
        [DataRow("connection.private.json", true)]
        [DataRow("my-password.txt", true)]
        [DataRow("token.log", true)]
        [DataRow("GeneralSettings.xml", true)]
        [DataRow("KSPModControl.cfg", true)]
        [DataRow("", true)]
        [DataRow("KSP.log", false)]
        [DataRow("sync-20261024.jsonl", false)]
        public void ForbiddenFileNames(string name, bool forbidden)
        {
            Assert.AreEqual(forbidden, LogBundleBuilder.IsForbiddenFileName(name));
        }

        [TestMethod]
        public void BuildHonoursCancellation()
        {
            var kspLog = Path.Combine(_dir, "KSP.log");
            File.WriteAllText(kspLog, "x\n");
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => LogBundleBuilder.Build(new LogBundleOptions
            {
                OutputPath = Path.Combine(_dir, "bundle.zip"),
                Sources = new List<LogBundleSource> { new LogBundleSource { EntryName = "KSP.log", Path = kspLog } },
            }, cancelled.Token, null));
        }

        #endregion
    }
}
