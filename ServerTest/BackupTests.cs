using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Backup;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using ZstdSharp;

namespace ServerTest
{
    [TestClass]
    public class BackupRoundTripTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        private string _root;
        private string _server;
        private string _out;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "vmp-backup-tests", Guid.NewGuid().ToString("N"));
            _server = Path.Combine(_root, "server");
            _out = Path.Combine(_root, "Backups");
            MakeServerFolder(_server);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private static string Lorem(Random random, int bytes)
        {
            var words = new[] { "PART", "{", "name = fuelTank", "mass = 0.25", "temp = 296.15", "crew = Jebediah Kerman", "ORBIT", "SMA = 700000", "ECC = 0.0012", "}", "MODULE", "isEnabled = True" };
            var text = new StringBuilder();
            while (text.Length < bytes) text.Append(words[random.Next(words.Length)]).Append(random.Next(3) == 0 ? " " + random.Next(100000) : string.Empty).Append('\n');
            return text.ToString();
        }

        /// <summary>A server folder with a few hundred small vessel files, some bigger text files, a binary flag, and Config with secrets next to harmless settings.</summary>
        private static void MakeServerFolder(string folder)
        {
            var random = new Random(1234);
            var vessels = Path.Combine(folder, "Universe", "Vessels");
            var scenarios = Path.Combine(folder, "Universe", "Scenarios");
            var flags = Path.Combine(folder, "Universe", "Flags", "Sub Folder");
            var config = Path.Combine(folder, "Config");
            foreach (var dir in new[] { vessels, scenarios, flags, config }) Directory.CreateDirectory(dir);

            for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(vessels, "vessel-" + i + ".txt"), Lorem(random, 400 + random.Next(600)));
            for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(scenarios, "Scenario" + i + ".txt"), Lorem(random, 150 * 1024));
            for (var i = 0; i < 3; i++)
            {
                var png = new byte[20000];
                random.NextBytes(png);
                File.WriteAllBytes(Path.Combine(flags, "flag" + i + ".png"), png);
            }

            File.WriteAllBytes(Path.Combine(folder, "Universe", "empty.txt"), new byte[0]);
            File.WriteAllText(Path.Combine(config, "IntervalSettings.xml"), "<IntervalSettingsDefinition><BackupIntervalMs>30000</BackupIntervalMs></IntervalSettingsDefinition>");
            File.WriteAllText(Path.Combine(config, "WarpSettings.xml"), "<WarpSettingsDefinition><WarpMode>Subspace</WarpMode></WarpSettingsDefinition>");
            File.WriteAllText(Path.Combine(config, "GeneralSettings.xml"), "<GeneralSettingsDefinition><ServerName>X</ServerName><Password>hunter2</Password><AdminPassword>swordfish</AdminPassword></GeneralSettingsDefinition>");
            File.WriteAllText(Path.Combine(config, "ExtraSecret.xml"), "<Other><Name>n</Name><ApiToken>live-key-123</ApiToken></Other>");
            File.WriteAllText(Path.Combine(config, "EmptySecret.xml"), "<Other><Name>n</Name><Password></Password><Password/></Other>");
            File.WriteAllText(Path.Combine(config, "admin-token.txt"), new string('a', 64));
        }

        private static string[] RelativeFiles(string folder)
        {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        [TestMethod]
        public void RoundTrip_WritesVerifiesAndRestoresByteIdentical()
        {
            var result = BackupWriter.Create(_server, _out, Now);

            Assert.AreEqual("Universe-20261028-120000.tar.zst", result.FileName);
            Assert.IsTrue(File.Exists(result.ArchivePath));
            Assert.IsFalse(File.Exists(result.ArchivePath + ".part"), "the .part file is renamed away");
            Assert.IsTrue(result.Verified);
            Assert.AreEqual(19, result.Level);
            Assert.IsTrue(result.CompressedBytes < result.RawBytes / 3, "text compresses well: " + result.Summary());
            Assert.AreEqual(result.CompressedBytes, new FileInfo(result.ArchivePath).Length);

            var sidecar = File.ReadAllText(result.ArchivePath + ".sha256");
            StringAssert.StartsWith(sidecar, result.Sha256 + "  Universe-20261028-120000.tar.zst");

            var verify = BackupReader.Verify(result.ArchivePath);
            Assert.IsTrue(verify.Ok, verify.Message);
            Assert.AreEqual(result.Files, verify.Entries);
            Assert.AreEqual(result.RawBytes, verify.Bytes);

            var target = Path.Combine(_root, "restored");
            var restored = BackupReader.Restore(result.ArchivePath, target);
            Assert.AreEqual(result.Files, restored.Files);
            Assert.AreEqual(result.RawBytes, restored.RawBytes);

            var expected = RelativeFiles(_server).Where(p => p.StartsWith("Universe/", StringComparison.Ordinal) || p == "Config/IntervalSettings.xml" || p == "Config/WarpSettings.xml" || p == "Config/EmptySecret.xml").ToArray();
            var sanitized = new[] { "Config/ExtraSecret.xml.sanitized", "Config/GeneralSettings.xml.sanitized" };
            CollectionAssert.AreEqual(expected.Concat(sanitized).OrderBy(p => p, StringComparer.Ordinal).ToArray(), RelativeFiles(target));
            foreach (var relative in expected)
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(_server, relative)), File.ReadAllBytes(Path.Combine(target, relative)), relative);
            }
        }

        [TestMethod]
        public void Credentials_AreNeverPacked_AndTheResultSaysWhatWasLeftOut()
        {
            var plan = BackupSourceSelector.Build(_server);
            var names = plan.Entries.Select(e => e.ArchiveName).ToList();

            CollectionAssert.DoesNotContain(names, "Config/admin-token.txt");
            CollectionAssert.DoesNotContain(names, "Config/GeneralSettings.xml");
            CollectionAssert.DoesNotContain(names, "Config/ExtraSecret.xml");
            CollectionAssert.Contains(names, "Config/IntervalSettings.xml");
            CollectionAssert.Contains(names, "Config/EmptySecret.xml", "an empty password element is not a secret");
            Assert.AreEqual(3, plan.Excluded.Count, string.Join("; ", plan.Excluded));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("admin-token.txt")));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("GeneralSettings.xml")));
            Assert.IsTrue(plan.Excluded.Any(e => e.Contains("ExtraSecret.xml") && e.Contains("ApiToken")));

            var result = BackupWriter.Create(_server, _out, Now, 3);
            Assert.AreEqual(3, result.Excluded.Count);

            // Nothing of the secrets is inside the archive, not even inside a larger file.
            var target = Path.Combine(_root, "restored");
            BackupReader.Restore(result.ArchivePath, target);
            foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                Assert.IsFalse(text.Contains("hunter2") || text.Contains("swordfish") || text.Contains("live-key-123") || text.Contains(new string('a', 64)), file);
            }
        }

        [TestMethod]
        public void TamperedArchive_FailsVerification_WithAndWithoutTheSidecar()
        {
            var result = BackupWriter.Create(_server, _out, Now, 3);
            var bytes = File.ReadAllBytes(result.ArchivePath);

            // Flip one byte in the middle: the sha256 sidecar notices.
            bytes[bytes.Length / 2] ^= 0x5A;
            File.WriteAllBytes(result.ArchivePath, bytes);
            var withSidecar = BackupReader.Verify(result.ArchivePath);
            Assert.IsFalse(withSidecar.Ok);
            StringAssert.Contains(withSidecar.Message, "sha256");

            // Without the sidecar the zstd checksum / tar structure still catches a flipped byte anywhere in the file (much of this archive
            // is raw, incompressible data, where only the checksum can tell).
            File.Delete(result.ArchivePath + ".sha256");
            bytes[bytes.Length / 2] ^= 0x5A;
            var original = (byte[])bytes.Clone();
            var positions = Enumerable.Range(1, 9).Select(i => original.Length * i / 10).Concat(new[] { original.Length - 1 });
            foreach (var position in positions)
            {
                var damaged = (byte[])original.Clone();
                damaged[position] ^= 0x5A;
                File.WriteAllBytes(result.ArchivePath, damaged);
                Assert.IsFalse(BackupReader.Verify(result.ArchivePath).Ok, "flipped byte at " + position + " of " + original.Length);
            }

            File.WriteAllBytes(result.ArchivePath, original);
            Assert.IsTrue(BackupReader.Verify(result.ArchivePath).Ok, "the pristine bytes verify again (no sidecar left)");

            // So does a truncated file.
            File.WriteAllBytes(result.ArchivePath, bytes.Take(bytes.Length / 2).ToArray());
            Assert.IsFalse(BackupReader.Verify(result.ArchivePath).Ok);

            var target = Path.Combine(_root, "restored");
            Assert.ThrowsException<InvalidDataException>(() => BackupReader.Restore(result.ArchivePath, target));
            Assert.IsFalse(Directory.Exists(Path.Combine(target, "Universe")), "nothing is extracted from a damaged archive");
        }

        [TestMethod]
        public void OldCompressArchiveZip_StillRestores()
        {
            var zipPath = Path.Combine(_root, "Universe-20260101-000000.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // Windows PowerShell 5.1 writes backslashes in entry names.
                WriteZipEntry(zip, "Universe\\Vessels\\a.txt", "vessel a");
                WriteZipEntry(zip, "Universe/Scenarios/b.txt", "scenario b");
                WriteZipEntry(zip, "Config\\IntervalSettings.xml", "<x/>");
                zip.CreateEntry("Universe/Empty/");
            }

            Assert.IsTrue(BackupReader.Verify(zipPath).Ok);

            var target = Path.Combine(_root, "from-zip");
            var result = BackupReader.Restore(zipPath, target);
            Assert.AreEqual(3, result.Files);
            Assert.AreEqual("vessel a", File.ReadAllText(Path.Combine(target, "Universe", "Vessels", "a.txt")));
            Assert.AreEqual("scenario b", File.ReadAllText(Path.Combine(target, "Universe", "Scenarios", "b.txt")));
            Assert.AreEqual("<x/>", File.ReadAllText(Path.Combine(target, "Config", "IntervalSettings.xml")));
            Assert.IsTrue(Directory.Exists(Path.Combine(target, "Universe", "Empty")));
        }

        private static void WriteZipEntry(ZipArchive zip, string name, string text)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
        }

        [TestMethod]
        public void HostileTarEntries_AreRejected_AndNothingEscapesTheTarget()
        {
            var hostile = new List<TarEntry>
            {
                Entry(TarEntryType.RegularFile, "../evil.txt"),
                Entry(TarEntryType.RegularFile, "Universe/../../evil.txt"),
                Entry(TarEntryType.RegularFile, "Universe/..\\..\\evil.txt"),
                Entry(TarEntryType.RegularFile, "/evil.txt"),
                Entry(TarEntryType.RegularFile, "\\evil.txt"),
                Entry(TarEntryType.RegularFile, "C:/evil.txt"),
                Entry(TarEntryType.RegularFile, "C:\\evil.txt"),
                Entry(TarEntryType.RegularFile, "Universe/file.txt:stream"),
                Entry(TarEntryType.RegularFile, "Universe/NUL.txt"),
                Entry(TarEntryType.SymbolicLink, "Universe/link", "../../outside"),
                Entry(TarEntryType.HardLink, "Universe/hard", "Universe/ok.txt"),
            };

            var index = 0;
            foreach (var entry in hostile)
            {
                var sandbox = Path.Combine(_root, "hostile" + index++);
                var target = Path.Combine(sandbox, "target");
                var archive = Path.Combine(sandbox, "Universe-20261028-130000.tar.zst");
                Directory.CreateDirectory(sandbox);
                WriteTarZst(archive, entry);

                Assert.ThrowsException<UnsafeBackupEntryException>(() => BackupReader.Restore(archive, target), entry.EntryType + " " + entry.Name);
                Assert.AreEqual(0, Directory.GetFiles(sandbox, "evil.txt", SearchOption.AllDirectories).Length, entry.Name);
                Assert.AreEqual(0, Directory.Exists(target) ? Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories).Length : 0, "nothing was created for " + entry.Name);
            }
        }

        [TestMethod]
        public void HostileZipEntries_AreRejected_BeforeAnythingIsExtracted()
        {
            var zipPath = Path.Combine(_root, "hostile.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteZipEntry(zip, "Universe/fine.txt", "fine");
                WriteZipEntry(zip, "..\\..\\evil.txt", "evil");
            }

            var target = Path.Combine(_root, "target");
            Assert.ThrowsException<UnsafeBackupEntryException>(() => BackupReader.Restore(zipPath, target));
            Assert.IsFalse(File.Exists(Path.Combine(target, "Universe", "fine.txt")), "names are all checked before the first file is written");
            Assert.IsFalse(File.Exists(Path.Combine(_root, "evil.txt")));
        }

        [TestMethod]
        public void RestoreRefusesToOverwrite_UnlessAskedTo()
        {
            var result = BackupWriter.Create(_server, _out, Now, 3);
            var target = Path.Combine(_root, "restored");
            BackupReader.Restore(result.ArchivePath, target);

            var file = Path.Combine(target, "Universe", "Vessels", "vessel-1.txt");
            File.WriteAllText(file, "changed");
            Assert.ThrowsException<IOException>(() => BackupReader.Restore(result.ArchivePath, target));
            Assert.AreEqual("changed", File.ReadAllText(file));

            BackupReader.Restore(result.ArchivePath, target, true);
            Assert.AreNotEqual("changed", File.ReadAllText(file));
        }

        [TestMethod]
        public void SameSecondTwice_GetsTwoDifferentNames()
        {
            var first = BackupWriter.Create(_server, _out, Now, 3);
            var second = BackupWriter.Create(_server, _out, Now, 3);

            Assert.AreNotEqual(first.FileName, second.FileName);
            Assert.AreEqual("Universe-20261028-120001.tar.zst", second.FileName);
            Assert.IsTrue(BackupReader.Verify(second.ArchivePath).Ok);
        }

        [TestMethod]
        public void FilesHeldOpenByTheRunningServer_AreStillBackedUp()
        {
            var live = Path.Combine(_server, "Universe", "Vessels", "vessel-7.txt");
            using (var writer = new FileStream(live, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                var result = BackupWriter.Create(_server, _out, Now, 3);
                Assert.IsTrue(result.Verified);
                Assert.AreEqual(0, result.Skipped.Count, string.Join("; ", result.Skipped));
            }
        }

        [TestMethod]
        public void MissingUniverse_IsAnError_AndLeavesNoPartFile()
        {
            Directory.Delete(Path.Combine(_server, "Universe"), true);
            Assert.ThrowsException<DirectoryNotFoundException>(() => BackupWriter.Create(_server, _out, Now, 3));
            Assert.AreEqual(0, Directory.Exists(_out) ? Directory.GetFiles(_out).Length : 0);
        }

        [TestMethod]
        public void OnlyOneArchiveBackupRunsAtATime()
        {
            using (var hold = new ManualResetEventSlim(false))
            {
                var first = BackupService.StartAsync(_server, _out, () => hold.Wait(TimeSpan.FromSeconds(30)), 10, 14, () => Now);
                Assert.IsTrue(BackupService.IsRunning);

                var second = BackupService.StartAsync(_server, _out, null, 10, 14, () => Now).GetAwaiter().GetResult();
                Assert.IsTrue(second.AlreadyRunning);
                Assert.IsNull(second.Result);

                hold.Set();
                var done = first.GetAwaiter().GetResult();
                Assert.IsFalse(done.AlreadyRunning);
                Assert.IsNull(done.Error);
                Assert.IsTrue(done.Result.Verified);
                Assert.IsFalse(BackupService.IsRunning);
            }
        }

        [TestMethod]
        public void CommandLine_BackupAndRestore_PrintOneSummaryLineAndSetTheExitCode()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.IsFalse(BackupCli.TryRun(new string[0], output, error, _server, out var code), "no verb: start the server");
            Assert.IsFalse(BackupCli.TryRun(new[] { "--something" }, output, error, _server, out code));

            Assert.IsTrue(BackupCli.TryRun(new[] { "backup", "--out", _out, "--level", "3" }, output, error, _server, out code));
            Assert.AreEqual(0, code, error.ToString());
            var lines = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(1, lines.Length);
            StringAssert.StartsWith(lines[0], "backup ok: ");
            StringAssert.Contains(lines[0], "files=");
            StringAssert.Contains(lines[0], "raw=");
            StringAssert.Contains(lines[0], "compressed=");
            StringAssert.Contains(lines[0], "ratio=");
            StringAssert.Contains(lines[0], "seconds=");
            StringAssert.Contains(lines[0], "admin-token.txt");

            var archive = Directory.GetFiles(_out, "Universe-*.tar.zst").Single();
            output = new StringWriter();
            var target = Path.Combine(_root, "cli-restore");
            Assert.IsTrue(BackupCli.TryRun(new[] { "restore", archive, "--to", target }, output, error, _server, out code));
            Assert.AreEqual(0, code, error.ToString());
            StringAssert.StartsWith(output.ToString(), "restore ok: ");
            Assert.IsTrue(File.Exists(Path.Combine(target, "Universe", "Vessels", "vessel-0.txt")));

            error = new StringWriter();
            Assert.IsTrue(BackupCli.TryRun(new[] { "restore", archive, "--to", target }, new StringWriter(), error, _server, out code));
            Assert.AreEqual(1, code, "files already there");
            StringAssert.StartsWith(error.ToString(), "restore FAILED");

            error = new StringWriter();
            Assert.IsTrue(BackupCli.TryRun(new[] { "restore", Path.Combine(_root, "missing.tar.zst"), "--to", target }, new StringWriter(), error, _server, out code));
            Assert.AreEqual(1, code);

            error = new StringWriter();
            Assert.IsTrue(BackupCli.TryRun(new[] { "backup" }, new StringWriter(), error, _server, out code));
            Assert.AreEqual(2, code, "usage error");

            error = new StringWriter();
            Assert.IsTrue(BackupCli.TryRun(new[] { "backup", "--out", _out }, new StringWriter(), error, Path.Combine(_root, "nowhere"), out code));
            Assert.AreEqual(1, code, "no Universe next to the exe");
            StringAssert.Contains(error.ToString(), "Universe folder not found");
        }

        private static TarEntry Entry(TarEntryType type, string name, string link = null)
        {
            var entry = new GnuTarEntry(type, name);
            if (link != null) entry.LinkName = link;
            else entry.DataStream = new MemoryStream(Encoding.ASCII.GetBytes("payload"));
            return entry;
        }

        private static void WriteTarZst(string path, params TarEntry[] entries)
        {
            using (var file = File.Create(path))
            using (var zstd = new CompressionStream(file, 3))
            using (var tar = new TarWriter(zstd, TarEntryFormat.Gnu, true))
            {
                foreach (var entry in entries) tar.WriteEntry(entry);
            }
        }
    }

    [TestClass]
    public class BackupRetentionTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        private static string Name(int daysAgo, int hour, int minute = 0)
        {
            return BackupNaming.MakeName(Now.Date.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute));
        }

        [TestMethod]
        public void Naming_OnlyExactArchiveNamesMatch()
        {
            Assert.AreEqual("Universe-20261028-120000.tar.zst", BackupNaming.MakeName(Now));
            Assert.IsTrue(BackupNaming.TryParseArchive("Universe-20261028-120000.tar.zst", out var parsed));
            Assert.AreEqual(Now, parsed);

            foreach (var other in new[]
            {
                "Universe-20261028-120000.zip", "Universe-20261028-120000.tar.zst.sha256", "Universe-20261028-120000.tar.zst.part",
                "universe-20261028-120000.tar.zst", "Universe-2026-10-28.tar.zst", "Universe-20261328-120000.tar.zst", "xUniverse-20261028-120000.tar.zst",
                "Universe-20261028-120000.tar.zst.bak", "notes.txt", string.Empty, null,
            })
            {
                Assert.IsFalse(BackupNaming.TryParseArchive(other, out _), other);
            }

            Assert.AreEqual("zip", BackupNaming.KindOf("Universe-20260101-000000.zip"));
            Assert.AreEqual("tar.zst", BackupNaming.KindOf("Universe-20261028-120000.tar.zst"));
            Assert.IsNull(BackupNaming.KindOf("Universe-20261028-120000.tar.zst.sha256"));
        }

        [TestMethod]
        public void KeepsTheNewestN_AndDeletesTheRest()
        {
            var names = Enumerable.Range(0, 15).Select(i => BackupNaming.MakeName(Now.AddMinutes(-i * 30))).ToList();
            var delete = BackupRetention.SelectDeletions(names, Now, 10, 0, null);

            Assert.AreEqual(5, delete.Count);
            CollectionAssert.AreEquivalent(names.Skip(10).ToList(), delete, "the five oldest");
        }

        [TestMethod]
        public void KeepsTheNewestOfEachDayForDDays()
        {
            var today1 = Name(0, 10);
            var today2 = Name(0, 8);
            var today3 = Name(0, 6);
            var yesterdayLate = Name(1, 20);
            var yesterdayEarly = Name(1, 4);
            var twoDaysAgo = Name(2, 15);
            var twoDaysAgoOther = Name(2, 3);
            var threeDaysAgo = Name(3, 9);
            var longAgo = Name(40, 9);

            var all = new[] { longAgo, today3, twoDaysAgoOther, today1, yesterdayEarly, threeDaysAgo, today2, twoDaysAgo, yesterdayLate };
            var delete = BackupRetention.SelectDeletions(all, Now, 2, 3, null);

            // Newest 2 = today1, today2. Daily for the last 3 days (today, yesterday, 2 days ago) = today1, yesterdayLate, twoDaysAgo.
            CollectionAssert.AreEquivalent(new[] { today3, yesterdayEarly, twoDaysAgoOther, threeDaysAgo, longAgo }, delete);
        }

        [TestMethod]
        public void TheFileJustCreated_IsNeverDeleted()
        {
            var names = new[] { Name(0, 1), Name(0, 2), Name(0, 3), Name(5, 3) };
            var justCreated = Name(5, 3);

            var delete = BackupRetention.SelectDeletions(names, Now, 1, 0, justCreated);
            CollectionAssert.AreEquivalent(new[] { Name(0, 1), Name(0, 2) }, delete);
            CollectionAssert.DoesNotContain(delete, justCreated);

            Assert.AreEqual(0, BackupRetention.SelectDeletions(names, Now, 0, 0, null).Count(n => n == "none"));
            Assert.AreEqual(3, BackupRetention.SelectDeletions(names, Now, 0, 0, justCreated).Count);
        }

        [TestMethod]
        public void FilesThatDoNotMatchTheNamePattern_AreNeverSelected()
        {
            var keepers = new[]
            {
                "Universe-20260101-000000.zip", "Universe-20260101-000000.tar.zst.sha256", "Universe-20260101-000000.tar.zst.part",
                "universe-20260101-000000.tar.zst", "Universe-bad.tar.zst", "notes.txt", "Manage-VmpServer.ps1", "Backup-2026.tar.zst",
            };
            var real = Name(30, 1);
            var delete = BackupRetention.SelectDeletions(keepers.Concat(new[] { real }), Now, 0, 0, null);

            CollectionAssert.AreEqual(new[] { real }, delete);
        }

        [TestMethod]
        public void Apply_DeletesArchivesWithTheirSidecars_AndNothingElse()
        {
            var folder = Path.Combine(Path.GetTempPath(), "vmp-backup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var old = Name(20, 1);
                var older = Name(21, 1);
                var fresh = Name(0, 11);
                foreach (var name in new[] { old, older, fresh })
                {
                    File.WriteAllText(Path.Combine(folder, name), "x");
                    File.WriteAllText(Path.Combine(folder, name + ".sha256"), "x");
                }

                File.WriteAllText(Path.Combine(folder, "Universe-20200101-000000.zip"), "x");
                File.WriteAllText(Path.Combine(folder, "Universe-20200101-000000.tar.zst.sha256"), "orphan");
                File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");

                var deleted = BackupRetention.Apply(folder, Now, 1, 3, fresh);

                CollectionAssert.AreEquivalent(new[] { old, older }, deleted);
                var left = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                CollectionAssert.AreEqual(
                    new[] { fresh, fresh + ".sha256", "notes.txt", "Universe-20200101-000000.tar.zst.sha256", "Universe-20200101-000000.zip" }.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                    left);

                Assert.AreEqual(0, BackupRetention.Apply(Path.Combine(folder, "missing"), Now, 1, 1, null).Count);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }
    }
}
