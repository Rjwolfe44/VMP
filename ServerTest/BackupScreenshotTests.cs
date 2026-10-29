using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server;
using Server.System.Backup;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace ServerTest
{
    [TestClass]
    public class BackupScreenshotTests
    {
        private static readonly DateTime Day1 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        private string _root;
        private string _server;
        private string _out;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "vmp-backup-tests", Guid.NewGuid().ToString("N"));
            _server = Path.Combine(_root, "server");
            _out = Path.Combine(_root, "Backups");
            Directory.CreateDirectory(Path.Combine(_server, "Universe", "Vessels"));
            Directory.CreateDirectory(Path.Combine(_server, "Universe", "Screenshots", "Jeb"));
            Directory.CreateDirectory(Path.Combine(_server, "Universe", "Screenshots", "Bill"));
            Directory.CreateDirectory(Path.Combine(_server, "Config"));
            File.WriteAllText(Path.Combine(_server, "Universe", "Vessels", "v.txt"), "vessel text vessel text vessel text");
            File.WriteAllText(Path.Combine(_server, "Config", "IntervalSettings.xml"), "<IntervalSettingsDefinition/>");
            File.WriteAllText(Path.Combine(_server, "Config", "GeneralSettings.xml"),
                "<GeneralSettingsDefinition>\r\n  <ServerName>X</ServerName>\r\n  <Password>hunter2</Password>\r\n  <AdminPassword>swordfish</AdminPassword>\r\n</GeneralSettingsDefinition>");
            File.WriteAllText(Path.Combine(_server, "Config", "admin-token.txt"), new string('t', 64));
            AddShot("Jeb", "a.png", Day1.AddHours(-2), 1);
            AddShot("Jeb", "b.png", Day1.AddHours(-1.9), 2);
            AddShot("Bill", "c.png", Day1.AddHours(-1.8), 3);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string AddShot(string player, string name, DateTime writtenUtc, int seed)
        {
            var path = Path.Combine(_server, "Universe", "Screenshots", player, name);
            var bytes = new byte[20000];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, writtenUtc);
            return path;
        }

        private static string[] RelativeFiles(string folder)
        {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        private static List<string> TarNames(string path)
        {
            var names = new List<string>();
            using (var file = File.OpenRead(path))
            using (var tar = new TarReader(file))
            {
                TarEntry entry;
                while ((entry = tar.GetNextEntry()) != null) names.Add(entry.Name);
            }

            return names;
        }

        [TestMethod]
        public void Screenshots_AreLeftOutOfTheMainArchiveByDefault_AndListedAsExcluded()
        {
            var result = BackupWriter.Create(_server, _out, Day1, 3);

            var target = Path.Combine(_root, "restored");
            BackupReader.Restore(result.ArchivePath, target);
            Assert.IsFalse(Directory.Exists(Path.Combine(target, "Universe", "Screenshots")));
            Assert.IsTrue(File.Exists(Path.Combine(target, "Universe", "Vessels", "v.txt")));
            Assert.IsTrue(result.RawBytes < 20000, "no PNG bytes in the archive: " + result.Summary());

            var line = result.Excluded.Single(e => e.StartsWith("Universe/Screenshots", StringComparison.Ordinal));
            StringAssert.Contains(line, "3 files");
            Assert.AreEqual(3, BackupSourceSelector.Build(_server).ScreenshotFiles);
        }

        [TestMethod]
        public void Screenshots_AreIncludedByTheFlag()
        {
            var result = BackupWriter.Create(_server, _out, Day1, 3, includeScreenshots: true);

            Assert.IsFalse(result.Excluded.Any(e => e.StartsWith("Universe/Screenshots", StringComparison.Ordinal)));
            var target = Path.Combine(_root, "restored");
            BackupReader.Restore(result.ArchivePath, target);
            foreach (var shot in new[] { "Jeb/a.png", "Jeb/b.png", "Bill/c.png" })
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(_server, "Universe", "Screenshots", shot)),
                    File.ReadAllBytes(Path.Combine(target, "Universe", "Screenshots", shot)), shot);
            }
        }

        [TestMethod]
        public void DailyScreenshotArchive_HoldsOnlyFilesNewSinceThePreviousOne()
        {
            Assert.IsTrue(ScreenshotArchiver.IsDue(_out, Day1, TimeSpan.FromHours(24)), "never archived: due");

            var first = ScreenshotArchiver.CreateIfNew(_server, _out, Day1);
            Assert.AreEqual("Screenshots-20261028-120000.tar", first.FileName);
            Assert.AreEqual(3, first.Files);
            Assert.IsNull(first.SinceUtc);
            CollectionAssert.AreEqual(new[] { "Universe/Screenshots/Bill/c.png", "Universe/Screenshots/Jeb/a.png", "Universe/Screenshots/Jeb/b.png" }, TarNames(first.ArchivePath));
            using (var stream = File.OpenRead(first.ArchivePath))
            {
                var head = new byte[4];
                stream.Read(head, 0, 4);
                CollectionAssert.AreNotEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, head, "plain tar, no zstd");
            }

            Assert.IsFalse(ScreenshotArchiver.IsDue(_out, Day1.AddHours(23), TimeSpan.FromHours(24)));
            Assert.IsTrue(ScreenshotArchiver.IsDue(_out, Day1.AddHours(24), TimeSpan.FromHours(24)));
            Assert.AreEqual(Day1, ScreenshotArchiver.NewestArchiveTime(_out));

            // Two new screenshots after the first archive, and one that is older than it (copied in later) which is not new.
            AddShot("Jeb", "d.png", Day1.AddHours(1), 4);
            AddShot("Bill", "e.png", Day1.AddHours(5), 5);
            AddShot("Bill", "old.png", Day1.AddHours(-30), 6);
            var second = ScreenshotArchiver.CreateIfNew(_server, _out, Day1.AddDays(1));
            Assert.AreEqual("Screenshots-20261029-120000.tar", second.FileName);
            Assert.AreEqual(Day1, second.SinceUtc);
            CollectionAssert.AreEqual(new[] { "Universe/Screenshots/Bill/e.png", "Universe/Screenshots/Jeb/d.png" }, TarNames(second.ArchivePath));
            Assert.AreEqual("screenshots.tar", BackupNaming.KindOf(second.ArchivePath));
            Assert.IsTrue(BackupReader.Verify(second.ArchivePath).Ok);

            Assert.IsNull(ScreenshotArchiver.CreateIfNew(_server, _out, Day1.AddDays(2)), "nothing new: no empty archive");
            Assert.AreEqual(2, Directory.GetFiles(_out, "Screenshots-*").Length);
            Assert.AreEqual(0, Directory.GetFiles(_out, "*.part").Length);
        }

        [TestMethod]
        public void RestoreOfAnArchiveWithoutScreenshots_LeavesTheLiveScreenshotsAlone()
        {
            var main = BackupWriter.Create(_server, _out, Day1, 3);
            var shots = ScreenshotArchiver.CreateIfNew(_server, _out, Day1);

            var live = Path.Combine(_root, "live");
            var liveShot = Path.Combine(live, "Universe", "Screenshots", "Jeb", "live.png");
            Directory.CreateDirectory(Path.GetDirectoryName(liveShot));
            File.WriteAllText(liveShot, "LIVE");
            var before = RelativeFiles(Path.Combine(live, "Universe", "Screenshots"));

            BackupReader.Restore(main.ArchivePath, live, true);
            CollectionAssert.AreEqual(before, RelativeFiles(Path.Combine(live, "Universe", "Screenshots")), "the main archive has no screenshots");
            Assert.AreEqual("LIVE", File.ReadAllText(liveShot));
            Assert.IsTrue(File.Exists(Path.Combine(live, "Universe", "Vessels", "v.txt")));

            // The screenshot archive only adds its own files next to the live ones.
            var restored = BackupReader.Restore(shots.ArchivePath, live, true);
            Assert.AreEqual(3, restored.Files);
            Assert.AreEqual("LIVE", File.ReadAllText(liveShot));
            Assert.IsTrue(File.Exists(Path.Combine(live, "Universe", "Screenshots", "Bill", "c.png")));
        }

        [TestMethod]
        public void ScreenshotArchiveRetention_KeepsTheNewest10_AndOnlyTouchesMatchingNames()
        {
            Directory.CreateDirectory(_out);
            var archives = Enumerable.Range(0, 12).Select(i => BackupNaming.MakeScreenshotName(Day1.AddDays(-i))).ToList();
            var others = new[]
            {
                "Screenshots-bad.tar", "Screenshots-20200101-000000.tar.part", "Screenshots-20200101-000000.tar.zst", "screenshots-20200101-000000.tar",
                "Universe-20200101-000000.tar.zst", "Universe-20200101-000000.zip", "notes.txt",
            };
            foreach (var name in archives.Concat(others)) File.WriteAllText(Path.Combine(_out, name), "x");

            var deleted = ScreenshotArchiver.ApplyRetention(_out, 10, archives[0]);

            CollectionAssert.AreEquivalent(new[] { archives[10], archives[11] }, deleted);
            foreach (var name in others) Assert.IsTrue(File.Exists(Path.Combine(_out, name)), name);
            Assert.AreEqual(10, Directory.GetFiles(_out, "Screenshots-2026*.tar").Length);
            Assert.AreEqual(0, ScreenshotArchiver.SelectDeletions(others, 0, null).Count);
        }

        [TestMethod]
        public void GeneralSettings_IsPackedOnlyAsASanitizedCopy_AndNeverRestoredOverTheLiveFile()
        {
            var result = BackupWriter.Create(_server, _out, Day1, 3);

            var names = BackupSourceSelector.Build(_server).Entries.Select(e => e.ArchiveName).ToList();
            CollectionAssert.Contains(names, "Config/GeneralSettings.xml.sanitized");
            CollectionAssert.DoesNotContain(names, "Config/GeneralSettings.xml");
            CollectionAssert.DoesNotContain(names, "Config/admin-token.txt");
            Assert.IsTrue(result.Excluded.Any(e => e.Contains("GeneralSettings.xml") && e.Contains("Password") && e.Contains("AdminPassword")));
            Assert.IsTrue(result.Excluded.Any(e => e.Contains("admin-token.txt")));

            var fresh = Path.Combine(_root, "fresh");
            BackupReader.Restore(result.ArchivePath, fresh);
            var sanitized = File.ReadAllText(Path.Combine(fresh, "Config", "GeneralSettings.xml.sanitized"));
            StringAssert.Contains(sanitized, "<ServerName>X</ServerName>");
            StringAssert.Contains(sanitized, "<Password></Password>");
            StringAssert.Contains(sanitized, "<AdminPassword></AdminPassword>");
            Assert.IsFalse(sanitized.Contains("hunter2") || sanitized.Contains("swordfish"));
            Assert.IsFalse(File.Exists(Path.Combine(fresh, "Config", "GeneralSettings.xml")));
            Assert.IsFalse(File.Exists(Path.Combine(fresh, "Config", "admin-token.txt")));

            // Restoring into a folder that has a live GeneralSettings.xml leaves it alone, even with overwrite.
            var live = Path.Combine(_root, "live");
            Directory.CreateDirectory(Path.Combine(live, "Config"));
            File.WriteAllText(Path.Combine(live, "Config", "GeneralSettings.xml"), "LIVE-SETTINGS");
            var restored = BackupReader.Restore(result.ArchivePath, live, true);
            Assert.AreEqual("LIVE-SETTINGS", File.ReadAllText(Path.Combine(live, "Config", "GeneralSettings.xml")));
            Assert.IsTrue(File.Exists(Path.Combine(live, "Config", "GeneralSettings.xml.sanitized")));
            Assert.AreEqual(0, restored.Skipped.Count);
        }

        [TestMethod]
        public void OldZipsThatCarryTheRealGeneralSettings_DoNotOverwriteALiveOne()
        {
            var zipPath = Path.Combine(_root, "Universe-20260101-000000.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Config\\GeneralSettings.xml").Open())) writer.Write("OLD-PASSWORD-SETTINGS");
                using (var writer = new StreamWriter(zip.CreateEntry("Config\\admin-token.txt").Open())) writer.Write("OLD-TOKEN");
                using (var writer = new StreamWriter(zip.CreateEntry("Universe\\Vessels\\a.txt").Open())) writer.Write("a");
            }

            var live = Path.Combine(_root, "live");
            Directory.CreateDirectory(Path.Combine(live, "Config"));
            File.WriteAllText(Path.Combine(live, "Config", "GeneralSettings.xml"), "LIVE-SETTINGS");
            File.WriteAllText(Path.Combine(live, "Config", "admin-token.txt"), "LIVE-TOKEN");

            var result = BackupReader.Restore(zipPath, live, true);
            Assert.AreEqual("LIVE-SETTINGS", File.ReadAllText(Path.Combine(live, "Config", "GeneralSettings.xml")));
            Assert.AreEqual("LIVE-TOKEN", File.ReadAllText(Path.Combine(live, "Config", "admin-token.txt")));
            Assert.AreEqual(2, result.Skipped.Count);
            Assert.AreEqual(1, result.Files);
            StringAssert.Contains(result.Summary(), "skipped=[");

            // Into an empty folder an old backup still restores completely.
            var empty = Path.Combine(_root, "empty");
            Assert.AreEqual(3, BackupReader.Restore(zipPath, empty).Files);
            Assert.AreEqual("OLD-PASSWORD-SETTINGS", File.ReadAllText(Path.Combine(empty, "Config", "GeneralSettings.xml")));
        }

        [TestMethod]
        public void Sanitizer_BlanksOnlyCredentialValues()
        {
            var clean = BackupSourceSelector.Sanitize(
                "<a><Password> x </Password><AdminPassword>y</AdminPassword><ServerName>S</ServerName><Token></Token><MasterApiKey>k</MasterApiKey></a>", out var blanked);

            Assert.AreEqual("<a><Password></Password><AdminPassword></AdminPassword><ServerName>S</ServerName><Token></Token><MasterApiKey></MasterApiKey></a>", clean);
            CollectionAssert.AreEqual(new[] { "Password", "AdminPassword", "MasterApiKey" }, blanked);
        }

        [TestMethod]
        public void CommandLine_ScreenshotsFlag_MovesThemIntoTheMainArchive()
        {
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.IsTrue(BackupCli.TryRun(new[] { "backup", "--out", _out, "--level", "3" }, output, error, _server, out var code));
            Assert.AreEqual(0, code, error.ToString());
            StringAssert.Contains(output.ToString(), "Universe/Screenshots (3 files");

            output = new StringWriter();
            var out2 = Path.Combine(_root, "Backups2");
            Assert.IsTrue(BackupCli.TryRun(new[] { "backup", "--out", out2, "--level", "3", "--screenshots" }, output, error, _server, out code));
            Assert.AreEqual(0, code, error.ToString());
            Assert.IsFalse(output.ToString().Contains("Universe/Screenshots ("));
            var archive = Directory.GetFiles(out2, "*.tar.zst").Single();
            Assert.IsTrue(new FileInfo(archive).Length > 50000, "three incompressible 20 KB PNGs are inside");
        }
    }
}
