using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using Server.Settings;
using Server.Settings.Definition;
using Server.System.Backup;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerTest
{
    /// <summary>Phase 5: deleting backups and the retention policy (keep rules, old zips, size cap), from the engine up to the admin API and the offline verbs.</summary>
    [TestClass]
    public class BackupManagementTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        private string _root;
        private string _backups;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "vmp-backup-mgmt-tests", Guid.NewGuid().ToString("N"));
            _backups = Path.Combine(_root, "Backups");
            Directory.CreateDirectory(_backups);
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

        // ---- helpers ----------------------------------------------------------------------------------------------------------------------

        private static BackupRetention.FolderFile F(string name, long bytes = 100) => new BackupRetention.FolderFile { Name = name, Bytes = bytes };
        private static string Tar(int day, int hour = 12) => $"Universe-202610{day:00}-{hour:00}0000.tar.zst";
        private static string Zip(int day) => $"Universe-202609{day:00}-100000.zip";
        private static string Shot(int day) => $"Screenshots-202610{day:00}-030000.tar";
        private static RetentionPolicy Policy(int keepNewest = 10, int days = 14, int shots = 10, int zips = 0, long cap = 0) =>
            new RetentionPolicy { KeepNewest = keepNewest, KeepDailyDays = days, KeepScreenshotArchives = shots, LegacyZipKeep = zips, MaxTotalBytes = cap };

        private static List<string> Names(BackupRetention.RetentionResult result) => result.Deleted.Select(d => d.Name).ToList();

        private string Put(string name, int bytes = 100)
        {
            var path = Path.Combine(_backups, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        private BackupAdmin MakeAdmin(RetentionPolicy policy, Func<bool> running = null, List<string> log = null) =>
            new BackupAdmin(_backups, () => policy, running ?? (() => false), () => Now, log == null ? (Action<string>)null : log.Add);

        // ---- name validation --------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TryClassify_AcceptsOnlyTheExactBackupNames()
        {
            Assert.IsTrue(BackupNaming.TryClassify("Universe-20261028-120000.tar.zst", out var kind, out var utc, out var sidecar));
            Assert.AreEqual(BackupNaming.FileKind.TarZst, kind);
            Assert.AreEqual(Now, utc);
            Assert.IsFalse(sidecar);

            Assert.IsTrue(BackupNaming.TryClassify("Universe-20261028-120000.tar.zst.sha256", out kind, out _, out sidecar));
            Assert.AreEqual(BackupNaming.FileKind.TarZst, kind);
            Assert.IsTrue(sidecar);

            Assert.IsTrue(BackupNaming.TryClassify("Universe-20260905-101500.zip", out kind, out _, out sidecar));
            Assert.AreEqual(BackupNaming.FileKind.LegacyZip, kind);
            Assert.IsFalse(sidecar);
            Assert.IsTrue(BackupNaming.TryClassify("Universe-20260905-101500.zip.sha256", out kind, out _, out sidecar));
            Assert.IsTrue(sidecar);

            Assert.IsTrue(BackupNaming.TryClassify("Screenshots-20261028-030000.tar", out kind, out _, out sidecar));
            Assert.AreEqual(BackupNaming.FileKind.Screenshots, kind);
            Assert.IsFalse(sidecar);
        }

        [TestMethod]
        public void TryClassify_RejectsTraversalSeparatorsAndOddNames()
        {
            var bad = new[]
            {
                null, string.Empty, " ", ".", "..", "../Universe-20261028-120000.tar.zst", "..\\Universe-20261028-120000.tar.zst", "sub/Universe-20261028-120000.tar.zst",
                "sub\\Universe-20261028-120000.tar.zst", "C:\\Universe-20261028-120000.tar.zst", "/Universe-20261028-120000.tar.zst", "%2e%2e%2fUniverse-20261028-120000.tar.zst",
                "Universe-20261028-120000.tar.zst\n", "\nUniverse-20261028-120000.tar.zst", "Universe-20261028-120000.tar.zst ", " Universe-20261028-120000.tar.zst",
                "Universe-20261028-120000.tar.zst.", "Universe-20261028-120000.tar.zst\0", "Universe-20261028-120000.tar.zst:stream", "Universe-20261028-120000.tar.zst.part",
                "Universe-20261028-120000.tar.zst.sha256.sha256", "Universe-20261028-120000.tar.zst.SHA256", "universe-20261028-120000.tar.zst", "UNIVERSE-20261028-120000.TAR.ZST",
                "Universe-20261028-120000.TAR.ZST", "Universe-20261028-120000.ZIP", "Universe-20261028-120000.zip.bak", "Universe-20261028-12000.tar.zst",
                "Universe-20261028-1200000.tar.zst", "Universe-2026102-120000.tar.zst", "Universe-20261328-120000.tar.zst", "Universe-20261028-250000.tar.zst",
                "Universe-20260230-120000.tar.zst", "Universe-\u0662\u0660\u0662\u0666\u0661\u0660\u0662\u0668-120000.tar.zst", "Universe-20261028-120000-extra.tar.zst",
                "Universe-20261028-120000.tar", "Universe-.tar.zst", "Universe-*.tar.zst", "Universe-20261028-120000.tar.zst*", "Screenshots-20261028-030000.tar.sha256",
                "Screenshots-20261028-030000.tar.zst", "screenshots-20261028-030000.tar", "Screenshots-20261028-030000.tar\n", "vmp-server.json", "admin-token.txt", "CON", "NUL.tar.zst",
                new string('a', 200) + ".tar.zst",
            };

            foreach (var name in bad)
            {
                Assert.IsFalse(BackupNaming.TryClassify(name, out _, out _, out var sidecar), "should be rejected: " + (name ?? "<null>").Replace("\n", "\\n").Replace("\0", "\\0"));
                Assert.IsFalse(sidecar);
            }
        }

        // ---- retention math ---------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void KeepRules_TarZst_NewestNPlusDailyAndSidecarsGoWithTheArchive()
        {
            var files = new List<BackupRetention.FolderFile>();
            for (var day = 20; day <= 24; day++) files.Add(F(Tar(day), 100));
            files.Add(F(Tar(20) + ".sha256", 7));
            files.Add(F(Tar(22) + ".sha256", 7));

            var plan = BackupRetention.Plan(files, Now, Policy(keepNewest: 2, days: 0), null);

            CollectionAssert.AreEquivalent(new[] { Tar(20), Tar(21), Tar(22) }, Names(plan));
            Assert.IsTrue(plan.Deleted.All(d => d.Reason == "keep-rules"));
            Assert.AreEqual(107, plan.Deleted.Single(d => d.Name == Tar(20)).Bytes, "archive plus sidecar");
            Assert.AreEqual(Tar(22) + ".sha256", plan.Deleted.Single(d => d.Name == Tar(22)).Sidecar);
            Assert.IsNull(plan.Deleted.Single(d => d.Name == Tar(21)).Sidecar);
            Assert.AreEqual(314, plan.FreedBytes);
            Assert.AreEqual(plan.TotalBytesBefore - 314, plan.TotalBytesAfter);
            Assert.IsFalse(plan.StillOverCap, "no cap set");
        }

        [TestMethod]
        public void KeepRules_DailyRuleKeepsOneNewestPerDayInTheWindow()
        {
            var files = new[] { F(Tar(28, 11)), F(Tar(28, 9)), F(Tar(27, 20)), F(Tar(27, 8)), F(Tar(26, 8)), F(Tar(20, 8)) };
            var plan = BackupRetention.Plan(files, Now, Policy(keepNewest: 1, days: 3), null);

            // newest one is kept (newest N); the newest of 27 and of 26 are kept by the daily rule; the older ones of a day and day 20 (outside 3 days) go.
            CollectionAssert.AreEquivalent(new[] { Tar(28, 9), Tar(27, 8), Tar(20, 8) }, Names(plan));
        }

        [TestMethod]
        public void KeepRules_ScreenshotArchivesKeepNewestN()
        {
            var files = new[] { F(Shot(20)), F(Shot(21)), F(Shot(22)), F(Shot(23)) };
            var plan = BackupRetention.Plan(files, Now, Policy(shots: 2), null);

            CollectionAssert.AreEquivalent(new[] { Shot(20), Shot(21) }, Names(plan));
            Assert.IsTrue(plan.Deleted.All(d => d.Kind == BackupNaming.FileKind.Screenshots && d.Sidecar == null));
        }

        [TestMethod]
        public void LegacyZips_AreNeverDeletedByTheKeepRulesWhenTheSettingIsZero()
        {
            var files = Enumerable.Range(1, 6).Select(d => F(Zip(d))).ToList();
            Assert.AreEqual(0, BackupRetention.Plan(files, Now, Policy(zips: 0), null).Deleted.Count);
        }

        [TestMethod]
        public void LegacyZips_KeepNewestN_WhenTheSettingIsAboveZero()
        {
            var files = Enumerable.Range(1, 6).Select(d => F(Zip(d))).ToList();
            files.Add(F(Zip(1) + ".sha256", 5));
            var plan = BackupRetention.Plan(files, Now, Policy(zips: 2), null);

            CollectionAssert.AreEquivalent(new[] { Zip(1), Zip(2), Zip(3), Zip(4) }, Names(plan));
            Assert.IsTrue(plan.Deleted.All(d => d.Reason == "legacy-zip-keep"));
            Assert.AreEqual(105, plan.Deleted.Single(d => d.Name == Zip(1)).Bytes, "the zip sidecar goes along");
        }

        [TestMethod]
        public void LegacyZips_ExplicitPrune_KeepsThreeWhenTheSettingIsZeroAndTheSettingWhenItIsSet()
        {
            var files = Enumerable.Range(1, 6).Select(d => F(Zip(d))).ToList();

            var explicitZero = BackupRetention.Plan(files, Now, Policy(zips: 0), null, includeLegacyZips: true);
            CollectionAssert.AreEquivalent(new[] { Zip(1), Zip(2), Zip(3) }, Names(explicitZero));

            var explicitFive = BackupRetention.Plan(files, Now, Policy(zips: 5), null, includeLegacyZips: true);
            CollectionAssert.AreEquivalent(new[] { Zip(1) }, Names(explicitFive));
        }

        [TestMethod]
        public void Cap_DeletesZipsFirstThenOldestArchivesAndStopsWhenItFits()
        {
            var files = new List<BackupRetention.FolderFile> { F(Zip(1)), F(Zip(2)) };
            for (var day = 20; day <= 24; day++) files.Add(F(Tar(day)));
            files.Add(F(Tar(20) + ".sha256", 10));
            files.Add(F(Tar(21) + ".sha256", 10));
            files.Add(F(Shot(20)));
            files.Add(F(Shot(21)));
            // 200 zips + 500 archives + 20 sidecars + 200 screenshots = 920

            var plan = BackupRetention.Plan(files, Now, Policy(cap: 600), null);

            CollectionAssert.AreEqual(new[] { Zip(1), Zip(2), Tar(20), Tar(21) }, Names(plan));
            Assert.IsTrue(plan.Deleted.All(d => d.Reason == "size-cap"));
            Assert.AreEqual(920, plan.TotalBytesBefore);
            Assert.AreEqual(500, plan.TotalBytesAfter);
            Assert.AreEqual(600, plan.CapBytes);
            Assert.IsFalse(plan.StillOverCap);
        }

        [TestMethod]
        public void Cap_ExactlyAtTheCapDeletesNothing_AndZeroMeansNoCap()
        {
            var files = new[] { F(Tar(20)), F(Tar(21)), F(Tar(22)), F(Tar(23)), F(Tar(24)) };
            Assert.AreEqual(0, BackupRetention.Plan(files, Now, Policy(cap: 500), null).Deleted.Count);
            Assert.AreEqual(1, BackupRetention.Plan(files, Now, Policy(cap: 499), null).Deleted.Count);
            Assert.AreEqual(0, BackupRetention.Plan(files, Now, Policy(cap: 0), null).Deleted.Count);
        }

        [TestMethod]
        public void Cap_NeverTakesTheNewestArchiveAndNeverLeavesFewerThanThree()
        {
            var files = new List<BackupRetention.FolderFile>();
            for (var day = 20; day <= 24; day++) files.Add(F(Tar(day), 1000));
            files.Add(F(Shot(20), 1000));
            files.Add(F(Shot(21), 1000));

            var plan = BackupRetention.Plan(files, Now, Policy(cap: 100), null);

            // Archives stop at three; the screenshot archives (the last group) still go; the folder stays above the cap.
            CollectionAssert.AreEqual(new[] { Tar(20), Tar(21), Shot(20), Shot(21) }, Names(plan));
            Assert.IsTrue(plan.StillOverCap);
            Assert.AreEqual(3000, plan.TotalBytesAfter);

            var two = BackupRetention.Plan(new[] { F(Tar(23), 1000), F(Tar(24), 1000) }, Now, Policy(cap: 1), null);
            Assert.AreEqual(0, two.Deleted.Count, "fewer than three archives: none is touched by the cap");
            Assert.IsTrue(two.StillOverCap);

            var one = BackupRetention.Plan(new[] { F(Tar(24), 1000) }, Now, Policy(cap: 1), null);
            Assert.AreEqual(0, one.Deleted.Count, "the newest archive is never taken");
        }

        [TestMethod]
        public void Cap_CountsWhatTheKeepRulesAlreadyFreed()
        {
            var files = new List<BackupRetention.FolderFile>();
            for (var day = 20; day <= 24; day++) files.Add(F(Tar(day), 100));

            // Keep rules take two (200 bytes) leaving 300; a cap of 350 then needs nothing more, a cap of 250 takes one more of the three left... but three is the minimum.
            var fits = BackupRetention.Plan(files, Now, Policy(keepNewest: 3, days: 0, cap: 350), null);
            CollectionAssert.AreEquivalent(new[] { Tar(20), Tar(21) }, Names(fits));
            Assert.IsTrue(fits.Deleted.All(d => d.Reason == "keep-rules"));

            var tooSmall = BackupRetention.Plan(files, Now, Policy(keepNewest: 3, days: 0, cap: 250), null);
            Assert.AreEqual(2, tooSmall.Deleted.Count);
            Assert.IsTrue(tooSmall.StillOverCap);
        }

        [TestMethod]
        public void NeverDelete_IsHonouredByEveryRule()
        {
            var files = new List<BackupRetention.FolderFile> { F(Zip(1)), F(Zip(2)), F(Shot(20)), F(Shot(21)) };
            for (var day = 20; day <= 24; day++) files.Add(F(Tar(day)));

            var plan = BackupRetention.Plan(files, Now, Policy(keepNewest: 1, days: 0, shots: 1, zips: 1, cap: 1), new[] { Tar(20), Zip(1), Shot(20) });

            var deleted = Names(plan);
            CollectionAssert.DoesNotContain(deleted, Tar(20));
            CollectionAssert.DoesNotContain(deleted, Zip(1));
            CollectionAssert.DoesNotContain(deleted, Shot(20));
            CollectionAssert.DoesNotContain(deleted, Tar(24), "newest archive");
        }

        [TestMethod]
        public void FilesThatDoNotMatchTheNamePatterns_AreNeverTouched_ButCountTowardsTheCap()
        {
            var odd = new[]
            {
                "Universe-20261020-120000.tar.zst.part", "notes.txt", "Universe-19990101-000000.tar.zst.sha256", "universe-20261020-120000.tar.zst", "Universe-20261020-120000.TAR.ZST",
                "Universe-20261020-120000.zip.bak", "Screenshots-20261020-030000.tar.sha256", "Universe-20261020-120000-predeploy.zip", "vmp-server.json",
            };
            var files = new List<BackupRetention.FolderFile>();
            for (var day = 20; day <= 24; day++) files.Add(F(Tar(day), 100));
            files.AddRange(odd.Select(n => F(n, 5000)));

            var plan = BackupRetention.Plan(files, Now, Policy(cap: 1000), null);

            Assert.AreEqual(500 + odd.Length * 5000, plan.TotalBytesBefore, "unknown files count towards the total");
            foreach (var deleted in plan.Deleted)
            {
                Assert.IsTrue(BackupNaming.TryClassify(deleted.Name, out _, out _, out var sidecar) && !sidecar, "only exact backup names are deleted: " + deleted.Name);
                CollectionAssert.DoesNotContain(odd, deleted.Name);
            }

            Assert.AreEqual(2, plan.Deleted.Count, "five archives down to the minimum of three");
            Assert.IsTrue(plan.StillOverCap);
        }

        [TestMethod]
        public void Policy_FromSettings_ConvertsAndClamps()
        {
            var settings = new IntervalSettingsDefinition { BackupKeepNewest = -4, BackupKeepDailyDays = 3, BackupKeepScreenshotArchives = 7, BackupLegacyZipKeep = -1, BackupMaxTotalMegabytes = 100 };
            var policy = RetentionPolicy.FromSettings(settings);
            Assert.AreEqual(0, policy.KeepNewest);
            Assert.AreEqual(3, policy.KeepDailyDays);
            Assert.AreEqual(7, policy.KeepScreenshotArchives);
            Assert.AreEqual(0, policy.LegacyZipKeep);
            Assert.AreEqual(100L * 1024 * 1024, policy.MaxTotalBytes);
            Assert.AreEqual(100, policy.MaxTotalMegabytes);

            var defaults = RetentionPolicy.FromSettings(new IntervalSettingsDefinition());
            Assert.AreEqual(10, defaults.KeepNewest);
            Assert.AreEqual(14, defaults.KeepDailyDays);
            Assert.AreEqual(10, defaults.KeepScreenshotArchives);
            Assert.AreEqual(0, defaults.LegacyZipKeep);
            Assert.AreEqual(2048L * 1024 * 1024, defaults.MaxTotalBytes);
            Assert.AreEqual(0, RetentionPolicy.FromSettings(new IntervalSettingsDefinition { BackupMaxTotalMegabytes = 0 }).MaxTotalBytes);
        }

        [TestMethod]
        public void NewSettings_ExistWithDefaultsDescriptionsAndAreLive()
        {
            var metas = SettingsCatalog.Describe(typeof(IntervalSettingsDefinition));
            var cap = metas.Single(m => m.Key == "interval.BackupMaxTotalMegabytes");
            var zips = metas.Single(m => m.Key == "interval.BackupLegacyZipKeep");

            Assert.AreEqual("int", cap.Type);
            Assert.AreEqual(0.0, cap.Min.Value);
            StringAssert.Contains(cap.Description, "0 = no cap");
            StringAssert.Contains(cap.Description, "newest");
            StringAssert.Contains(zips.Description, "0 =");
            Assert.IsFalse(cap.RestartRequired);
            Assert.IsFalse(zips.RestartRequired);
            foreach (var key in new[] { "ArchiveBackupIntervalMinutes", "BackupKeepNewest", "BackupKeepDailyDays", "BackupIncludeScreenshots", "BackupKeepScreenshotArchives" })
            {
                Assert.IsFalse(metas.Single(m => m.Name == key).RestartRequired, key + " is read again for each backup");
            }

            Assert.IsTrue(metas.Single(m => m.Name == "BackupIntervalMs").RestartRequired, "settings that are not on the live list still need a restart");
            var defaults = new IntervalSettingsDefinition();
            Assert.AreEqual(2048, defaults.BackupMaxTotalMegabytes);
            Assert.AreEqual(0, defaults.BackupLegacyZipKeep);
        }

        [TestMethod]
        public void SettingsFile_TryReadSection_ReadsWithDefaultsForMissingValues_AndNeverWrites()
        {
            var config = Path.Combine(_root, "Config");
            Directory.CreateDirectory(config);
            Assert.IsFalse(SettingsFile.TryReadSection<IntervalSettingsDefinition>(config, "interval", out var none, out var problem), "no file");
            Assert.IsNull(none);
            StringAssert.Contains(problem, "not found");

            var path = Path.Combine(config, "vmp-server.json");
            File.WriteAllText(path, "{ \"interval\": { \"BackupKeepNewest\": 4, \"BackupMaxTotalMegabytes\": 0 }, \"general\": { \"ServerName\": \"x\" } }");
            var before = File.ReadAllBytes(path);
            Assert.IsTrue(SettingsFile.TryReadSection<IntervalSettingsDefinition>(config, "interval", out var interval, out _));
            Assert.AreEqual(4, interval.BackupKeepNewest);
            Assert.AreEqual(0, interval.BackupMaxTotalMegabytes);
            Assert.AreEqual(14, interval.BackupKeepDailyDays, "missing value: default");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            Assert.AreEqual(1, Directory.GetFiles(config).Length, "nothing else was created");

            File.WriteAllText(path, "{ not json");
            Assert.IsFalse(SettingsFile.TryReadSection<IntervalSettingsDefinition>(config, "interval", out _, out problem));
            Assert.IsFalse(string.IsNullOrEmpty(problem));
            File.WriteAllText(path, "{ \"general\": {} }");
            Assert.IsFalse(SettingsFile.TryReadSection<IntervalSettingsDefinition>(config, "interval", out _, out _), "section missing");
        }

        // ---- the engine on a real folder --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Run_DryRunTouchesNothing_AndTheRealRunDeletesExactlyTheSameFilesWithTheirSidecars()
        {
            for (var day = 20; day <= 24; day++)
            {
                Put(Tar(day));
                Put(Tar(day) + ".sha256", 10);
            }

            Put("Universe-20261020-120000.tar.zst.part", 50);
            Put("notes.txt", 50);
            var policy = Policy(keepNewest: 2, days: 0);

            var dry = BackupRetention.Run(_backups, Now, policy, null, false, true);
            Assert.IsTrue(dry.DryRun);
            Assert.AreEqual(3, dry.Deleted.Count);
            Assert.AreEqual(12, Directory.GetFiles(_backups).Length, "dry run deletes nothing");

            var real = BackupRetention.Run(_backups, Now, policy, null, false, false);
            CollectionAssert.AreEquivalent(Names(dry), Names(real));
            Assert.AreEqual(330, real.FreedBytes);
            var left = Directory.GetFiles(_backups).Select(Path.GetFileName).OrderBy(n => n).ToList();
            CollectionAssert.AreEqual(new[] { "notes.txt", Tar(23), Tar(23) + ".sha256", Tar(24), Tar(24) + ".sha256", "Universe-20261020-120000.tar.zst.part" }.OrderBy(n => n).ToList(), left);
        }

        [TestMethod]
        public void Run_AFileInUseStaysAndIsReported_WhileTheRestIsDeleted()
        {
            if (!OperatingSystem.IsWindows()) return;

            for (var day = 20; day <= 24; day++) Put(Tar(day));
            using (new FileStream(Path.Combine(_backups, Tar(20)), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = BackupRetention.Run(_backups, Now, Policy(keepNewest: 2, days: 0), null, false, false);
                CollectionAssert.AreEquivalent(new[] { Tar(21), Tar(22) }, Names(result));
                CollectionAssert.AreEqual(new[] { Tar(20) }, result.Failed.Select(f => f.Name).ToList());
                Assert.AreEqual(result.TotalBytesBefore - 200, result.TotalBytesAfter);
            }

            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(20))));
        }

        [TestMethod]
        public void Run_MissingFolder_IsEmpty()
        {
            var result = BackupRetention.Run(Path.Combine(_root, "nope"), Now, Policy(cap: 1), null, false, false);
            Assert.AreEqual(0, result.Deleted.Count);
            Assert.AreEqual(0, result.TotalBytesBefore);
        }

        [TestMethod]
        public void BackupService_AppliesTheFullPolicyAfterTheBackup_AndKeepsWhatItJustMade()
        {
            var server = Path.Combine(_root, "server");
            Directory.CreateDirectory(Path.Combine(server, "Universe", "Vessels"));
            Directory.CreateDirectory(Path.Combine(server, "Config"));
            File.WriteAllText(Path.Combine(server, "Universe", "Vessels", "v.txt"), "vessel");
            for (var day = 20; day <= 24; day++) Put($"Universe-202010{day:00}-120000.tar.zst", 20);
            Put("Universe-20200901-100000.zip", 20);
            Put("Universe-20200902-100000.zip", 20);
            Put("Screenshots-20201020-030000.tar", 20);

            var policy = Policy(cap: 1);
            var outcome = BackupService.StartAsync(server, _backups, null, policy, () => Now).GetAwaiter().GetResult();

            Assert.IsNull(outcome.Error);
            Assert.IsNull(outcome.RetentionError);
            Assert.IsTrue(File.Exists(Path.Combine(_backups, outcome.Result.FileName)));
            CollectionAssert.AreEquivalent(new[] { "Universe-20200901-100000.zip", "Universe-20200902-100000.zip", "Universe-20201020-120000.tar.zst", "Universe-20201021-120000.tar.zst", "Universe-20201022-120000.tar.zst" }, outcome.Pruned);
            CollectionAssert.AreEqual(new[] { "Screenshots-20201020-030000.tar" }, outcome.PrunedScreenshots);
            Assert.IsTrue(outcome.Retention.StillOverCap);
            var archives = Directory.GetFiles(_backups, "Universe-*.tar.zst").Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.AreEqual(3, archives.Count);
            Assert.AreEqual(outcome.Result.FileName, archives.Last(), "the new archive is the newest and stays");
        }

        // ---- BackupAdmin: delete, prune, folder info --------------------------------------------------------------------------------------

        [TestMethod]
        public void Admin_Delete_RemovesTheArchiveAndItsSidecar_AndLogsOneLine()
        {
            Put(Tar(20), 30);
            Put(Tar(20) + ".sha256", 5);
            Put(Tar(24), 30);
            var log = new List<string>();

            var result = MakeAdmin(Policy(), null, log).Delete(Tar(20), false);

            Assert.AreEqual(BackupDeleteStatus.Deleted, result.Status);
            CollectionAssert.AreEqual(new[] { Tar(20), Tar(20) + ".sha256" }, result.Files);
            Assert.AreEqual(35, result.FreedBytes);
            Assert.IsFalse(File.Exists(Path.Combine(_backups, Tar(20))));
            Assert.IsFalse(File.Exists(Path.Combine(_backups, Tar(20) + ".sha256")));
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(24))));
            Assert.AreEqual(1, log.Count);
            StringAssert.Contains(log[0], Tar(20));
            StringAssert.Contains(log[0], "35 bytes");
        }

        [TestMethod]
        public void Admin_Delete_ZipScreenshotArchiveAndSidecarOnly()
        {
            Put(Zip(1), 40);
            Put(Zip(1) + ".sha256", 5);
            Put(Shot(20), 60);
            Put(Tar(20) + ".sha256", 5);
            Put(Tar(24));
            var admin = MakeAdmin(Policy());

            Assert.AreEqual(45, admin.Delete(Zip(1), false).FreedBytes);
            Assert.AreEqual(60, admin.Delete(Shot(20), false).FreedBytes);
            var sidecar = admin.Delete(Tar(20) + ".sha256", false);
            Assert.AreEqual(BackupDeleteStatus.Deleted, sidecar.Status);
            Assert.AreEqual(5, sidecar.FreedBytes);
            Assert.AreEqual(1, Directory.GetFiles(_backups).Length);
        }

        [TestMethod]
        public void Admin_Delete_MissingOrInvalidName_IsNotFound_AndNothingElseIsTouched()
        {
            Put(Tar(24));
            Put("Universe-20261024-120000.tar.zst.part");
            var admin = MakeAdmin(Policy());

            Assert.AreEqual(BackupDeleteStatus.NotFound, admin.Delete(Tar(23), false).Status);
            Assert.AreEqual(BackupDeleteStatus.NotFound, admin.Delete("Universe-20261024-120000.tar.zst.part", false).Status);
            Assert.AreEqual(BackupDeleteStatus.NotFound, admin.Delete("..\\Backups\\" + Tar(24), false).Status);
            Assert.AreEqual(BackupDeleteStatus.NotFound, admin.Delete(null, false).Status);
            Assert.AreEqual(2, Directory.GetFiles(_backups).Length);
        }

        [TestMethod]
        public void Admin_Delete_NewestArchiveIsProtected_UnlessForced()
        {
            Put(Tar(23));
            Put(Tar(24));
            Put(Tar(24) + ".sha256", 5);
            var admin = MakeAdmin(Policy());

            Assert.AreEqual(BackupDeleteStatus.Protected, admin.Delete(Tar(24), false).Status);
            Assert.AreEqual(BackupDeleteStatus.Protected, admin.Delete(Tar(24) + ".sha256", false).Status, "its sidecar belongs to the protected backup");
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(24))));

            var forced = admin.Delete(Tar(24), true);
            Assert.AreEqual(BackupDeleteStatus.Deleted, forced.Status);
            Assert.AreEqual(105, forced.FreedBytes);
            Assert.AreEqual(BackupDeleteStatus.Protected, admin.Delete(Tar(23), false).Status, "now Tar(23) is the newest one left and is protected in turn");
        }

        [TestMethod]
        public void Admin_Delete_WhileABackupRuns_IsAConflict_AndKeepsTheFile()
        {
            Put(Tar(20));
            var running = true;
            var admin = MakeAdmin(Policy(), () => running);

            Assert.AreEqual(BackupDeleteStatus.Running, admin.Delete(Tar(20), false).Status);
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(20))));
            Assert.IsTrue(admin.Prune(false, false).Running);

            running = false;
            Put(Tar(24));
            Assert.AreEqual(BackupDeleteStatus.Deleted, admin.Delete(Tar(20), false).Status);
        }

        [TestMethod]
        public void Admin_Prune_DryRunThenReal_AndTheNewestArchiveSurvivesEvenWithKeepZero()
        {
            for (var day = 20; day <= 24; day++) Put(Tar(day));
            var log = new List<string>();
            var admin = MakeAdmin(Policy(keepNewest: 0, days: 0), null, log);

            var dry = admin.Prune(true, false);
            Assert.IsTrue(dry.DryRun);
            Assert.AreEqual(4, dry.Deleted.Count);
            Assert.AreEqual(400, dry.FreedBytes);
            Assert.AreEqual(5, Directory.GetFiles(_backups).Length);
            Assert.AreEqual(0, log.Count, "a dry run logs nothing");

            var real = admin.Prune(false, false);
            Assert.AreEqual(4, real.Deleted.Count);
            CollectionAssert.AreEqual(new[] { Tar(24) }, Directory.GetFiles(_backups).Select(Path.GetFileName).ToList());
            Assert.AreEqual(1, log.Count);
        }

        [TestMethod]
        public void Admin_Prune_IncludeLegacyZips_AppliesTheKeepCountEvenWhenTheSettingIsZero()
        {
            Put(Tar(24));
            for (var day = 1; day <= 5; day++) Put(Zip(day));
            var admin = MakeAdmin(Policy(zips: 0));

            Assert.AreEqual(0, admin.Prune(true, false).Deleted.Count, "setting 0: zips are not pruned");
            var dry = admin.Prune(true, true);
            CollectionAssert.AreEquivalent(new[] { Zip(1), Zip(2) }, dry.Deleted.Select(d => d.Name).ToList());
            Assert.IsTrue(dry.Deleted.All(d => d.Reason == "legacy-zip-keep"));
            Assert.AreEqual(6, Directory.GetFiles(_backups).Length);

            admin.Prune(false, true);
            Assert.AreEqual(4, Directory.GetFiles(_backups).Length);
        }

        [TestMethod]
        public void Admin_FolderInfo_CountsRecognizedBackups_SumsEveryFile_AndReportsThePolicy()
        {
            Put(Tar(24), 1000);
            Put(Tar(24) + ".sha256", 70);
            Put(Zip(1), 500);
            Put("Universe-20261024-120000.tar.zst.part", 30);
            var info = MakeAdmin(Policy(keepNewest: 4, days: 5, shots: 6, zips: 2, cap: 3L * 1024 * 1024)).GetFolderInfo();

            Assert.AreEqual(2, info.Count);
            Assert.AreEqual(1600, info.TotalBytes);
            Assert.AreEqual(3L * 1024 * 1024, info.CapBytes);
            Assert.AreEqual(4, info.Policy.KeepNewest);
            Assert.AreEqual(5, info.Policy.KeepDailyDays);
            Assert.AreEqual(6, info.Policy.KeepScreenshotArchives);
            Assert.AreEqual(2, info.Policy.LegacyZipKeep);
            Assert.AreEqual(3, info.Policy.MaxTotalMegabytes);
        }

        // ---- the HTTP surface -------------------------------------------------------------------------------------------------------------

        private sealed class FolderBackend : IAdminApiBackend
        {
            private readonly BackupAdmin _admin;
            public FolderBackend(BackupAdmin admin) => _admin = admin;
            public StatusInfo GetStatus() => new StatusInfo();
            public NetworkInfo GetNetwork() => new NetworkInfo();
            public List<ClientInfo> GetClients() => new List<ClientInfo>();
            public List<string> ReadLog(int lines) => new List<string>();
            public ReportsInfo GetReports() => new ReportsInfo();
            public List<BackupFileEntry> ListBackups() => _admin.List();
            public Task<BackupCreateInfo> CreateBackupAsync() => Task.FromResult<BackupCreateInfo>(null);
            public void Say(string text) { }
            public bool Kick(string name, string reason) => false;
            public BackupFolderInfo GetBackupFolderInfo() => _admin.GetFolderInfo();
            public BackupDeleteResult DeleteBackup(string fileName, bool force) => _admin.Delete(fileName, force);
            public BackupPruneResult PruneBackups(bool dryRun, bool includeLegacyZips) => _admin.Prune(dryRun, includeLegacyZips);
        }

        private AdminApiHandler MakeHandler(RetentionPolicy policy, Func<bool> running = null, Func<DateTime> clock = null) =>
            new AdminApiHandler(new FolderBackend(MakeAdmin(policy, running)), Token, null, null, clock);

        private static AdminApiRequest Req(string method, string path, string query = "", string body = null, string auth = "Bearer " + Token) => new AdminApiRequest
        {
            Method = method,
            Path = path,
            Query = query,
            Authorization = auth,
            Body = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body),
            RemoteAddress = IPAddress.Loopback,
        };

        private static AdminApiResponse Send(AdminApiHandler handler, AdminApiRequest request) => handler.HandleAsync(request).GetAwaiter().GetResult();

        private static JsonElement Json(AdminApiResponse response) => JsonDocument.Parse(response.Json).RootElement.Clone();

        [TestMethod]
        public void Api_GetBackups_AddsCountTotalCapAndPolicy()
        {
            Put(Tar(24), 1000);
            Put(Zip(1), 500);
            var response = Send(MakeHandler(Policy(keepNewest: 7, days: 9, shots: 3, zips: 1, cap: 2L * 1024 * 1024)), Req("GET", "/api/v1/backups"));

            Assert.AreEqual(200, response.StatusCode);
            var root = Json(response);
            Assert.AreEqual(2, root.GetProperty("backups").GetArrayLength());
            Assert.AreEqual(2, root.GetProperty("count").GetInt32());
            Assert.AreEqual(1500, root.GetProperty("totalBytes").GetInt64());
            Assert.AreEqual(2L * 1024 * 1024, root.GetProperty("capBytes").GetInt64());
            var policy = root.GetProperty("policy");
            Assert.AreEqual(7, policy.GetProperty("keepNewest").GetInt32());
            Assert.AreEqual(9, policy.GetProperty("keepDailyDays").GetInt32());
            Assert.AreEqual(3, policy.GetProperty("keepScreenshotArchives").GetInt32());
            Assert.AreEqual(1, policy.GetProperty("legacyZipKeep").GetInt32());
            Assert.AreEqual(2, policy.GetProperty("maxTotalMegabytes").GetInt32());
        }

        [TestMethod]
        public void Api_Delete_Statuses()
        {
            Put(Tar(23));
            Put(Tar(23) + ".sha256", 5);
            Put(Tar(24));
            var handler = MakeHandler(Policy());

            var ok = Send(handler, Req("DELETE", "/api/v1/backups/" + Tar(23)));
            Assert.AreEqual(200, ok.StatusCode);
            var json = Json(ok);
            Assert.AreEqual(Tar(23), json.GetProperty("name").GetString());
            Assert.AreEqual(2, json.GetProperty("deleted").GetArrayLength());
            Assert.AreEqual(105, json.GetProperty("freedBytes").GetInt64());

            Assert.AreEqual(404, Send(handler, Req("DELETE", "/api/v1/backups/" + Tar(23))).StatusCode, "already gone");

            var protectedNewest = Send(handler, Req("DELETE", "/api/v1/backups/" + Tar(24)));
            Assert.AreEqual(409, protectedNewest.StatusCode);
            StringAssert.Contains(Json(protectedNewest).GetProperty("error").GetString(), "force=true");
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(24))));

            Assert.AreEqual(409, Send(handler, Req("DELETE", "/api/v1/backups/" + Tar(24), "force=false")).StatusCode);
            Assert.AreEqual(200, Send(handler, Req("DELETE", "/api/v1/backups/" + Tar(24), "force=true")).StatusCode);
            Assert.AreEqual(0, Directory.GetFiles(_backups).Length);
        }

        [TestMethod]
        public void Api_Delete_RunningBackup_Is409_AndTheFileStays()
        {
            Put(Tar(20));
            Put(Tar(24));
            var handler = MakeHandler(Policy(), () => true);

            var response = Send(handler, Req("DELETE", "/api/v1/backups/" + Tar(20)));
            Assert.AreEqual(409, response.StatusCode);
            StringAssert.Contains(Json(response).GetProperty("error").GetString(), "running");
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(20))));
        }

        [TestMethod]
        public void Api_Delete_OddNames_Are400_AndNothingIsDeleted()
        {
            Put(Tar(24));
            Put("vmp-server.json", 10);
            Directory.CreateDirectory(Path.Combine(_root, "outside"));
            File.WriteAllText(Path.Combine(_root, "outside", Tar(20)), "must survive");
            var handler = MakeHandler(Policy());

            var paths = new[]
            {
                "/api/v1/backups/..%2F" + Tar(20), "/api/v1/backups/../outside/" + Tar(20), "/api/v1/backups/..\\outside\\" + Tar(20), "/api/v1/backups/vmp-server.json",
                "/api/v1/backups/" + Tar(24).ToUpperInvariant(), "/api/v1/backups/" + Tar(24) + ".part", "/api/v1/backups/" + Tar(24) + "%00", "/api/v1/backups/*.tar.zst",
                "/api/v1/backups/" + Tar(24) + "\n", "/api/v1/backups//" + Tar(24), "/api/v1/backups/.", "/api/v1/backups/..",
            };
            foreach (var path in paths)
            {
                var response = Send(handler, Req("DELETE", path, "force=true"));
                Assert.IsTrue(response.StatusCode == 400 || response.StatusCode == 404, path + " -> " + response.StatusCode);
                Assert.IsNotNull(Json(response).GetProperty("error").GetString(), "JSON error body");
            }

            Assert.AreEqual(400, Send(handler, Req("DELETE", "/api/v1/backups/vmp-server.json")).StatusCode);
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(24))));
            Assert.IsTrue(File.Exists(Path.Combine(_backups, "vmp-server.json")));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "outside", Tar(20))));
        }

        [TestMethod]
        public void Api_Delete_OtherVerbsAreNotAllowed_AndAuthIsStillRequired()
        {
            Put(Tar(20));
            Put(Tar(24));
            var handler = MakeHandler(Policy());
            var path = "/api/v1/backups/" + Tar(20);

            foreach (var method in new[] { "GET", "POST", "PUT", "PATCH" }) Assert.AreEqual(405, Send(handler, Req(method, path)).StatusCode, method);
            Assert.AreEqual(405, Send(handler, Req("DELETE", "/api/v1/backups")).StatusCode);
            Assert.AreEqual(401, Send(handler, Req("DELETE", path, auth: null)).StatusCode);
            Assert.AreEqual(401, Send(handler, Req("DELETE", path, auth: "Bearer nope")).StatusCode);
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(20))));
        }

        [TestMethod]
        public void Api_Delete_FailedLoginsStillLockOut()
        {
            Put(Tar(20));
            var clock = Now;
            var handler = MakeHandler(Policy(), null, () => clock);
            var path = "/api/v1/backups/" + Tar(20);

            for (var i = 0; i < AdminApiHandler.AuthFailureLimit; i++) Assert.AreEqual(401, Send(handler, Req("DELETE", path, auth: "Bearer wrong")).StatusCode);
            Assert.AreEqual(429, Send(handler, Req("DELETE", path)).StatusCode, "locked out even with the right token");
            Assert.IsTrue(File.Exists(Path.Combine(_backups, Tar(20))));

            clock = clock.Add(AdminApiHandler.AuthLockout).AddSeconds(1);
            Assert.AreEqual(409, Send(handler, Req("DELETE", path)).StatusCode, "after the lockout the request is handled again (newest archive is protected)");
        }

        [TestMethod]
        public void Api_Prune_DryRunIsTheDefault_AndDeletesNothing()
        {
            for (var day = 20; day <= 24; day++) Put(Tar(day));
            var handler = MakeHandler(Policy(keepNewest: 2, days: 0));

            foreach (var body in new string[] { null, "{}", "{\"dryRun\":true}" })
            {
                var response = Send(handler, Req("POST", "/api/v1/backups/prune", body: body));
                Assert.AreEqual(200, response.StatusCode, body);
                var json = Json(response);
                Assert.IsTrue(json.GetProperty("dryRun").GetBoolean(), body);
                Assert.AreEqual(3, json.GetProperty("deleted").GetArrayLength());
                Assert.AreEqual(300, json.GetProperty("freedBytes").GetInt64());
                Assert.AreEqual(Tar(22), json.GetProperty("deleted")[0].GetProperty("name").GetString(), "newest of the deletions first");
                Assert.AreEqual(100, json.GetProperty("deleted")[0].GetProperty("bytes").GetInt64());
            }

            Assert.AreEqual(5, Directory.GetFiles(_backups).Length);
        }

        [TestMethod]
        public void Api_Prune_RealRun_AndLegacyZipOption()
        {
            for (var day = 20; day <= 24; day++) Put(Tar(day));
            for (var day = 1; day <= 4; day++) Put(Zip(day));
            var handler = MakeHandler(Policy(keepNewest: 4, days: 0, zips: 0));

            var plain = Send(handler, Req("POST", "/api/v1/backups/prune", body: "{\"dryRun\":false,\"includeLegacyZips\":false}"));
            Assert.AreEqual(200, plain.StatusCode);
            Assert.IsFalse(Json(plain).GetProperty("dryRun").GetBoolean());
            Assert.AreEqual(1, Json(plain).GetProperty("deleted").GetArrayLength());
            Assert.AreEqual(8, Directory.GetFiles(_backups).Length);

            var zips = Send(handler, Req("POST", "/api/v1/backups/prune", body: "{\"dryRun\":false,\"includeLegacyZips\":true}"));
            var json = Json(zips);
            Assert.AreEqual(1, json.GetProperty("deleted").GetArrayLength());
            Assert.AreEqual(Zip(1), json.GetProperty("deleted")[0].GetProperty("name").GetString());
            Assert.AreEqual("legacy-zip-keep", json.GetProperty("deleted")[0].GetProperty("reason").GetString());
            Assert.AreEqual(7, Directory.GetFiles(_backups).Length);
            Assert.IsFalse(json.GetProperty("stillOverCap").GetBoolean());
        }

        [TestMethod]
        public void Api_Prune_Errors()
        {
            Put(Tar(20));
            Put(Tar(24));
            var path = "/api/v1/backups/prune";

            Assert.AreEqual(409, Send(MakeHandler(Policy(), () => true), Req("POST", path, body: "{\"dryRun\":false}")).StatusCode);
            var handler = MakeHandler(Policy(keepNewest: 1, days: 0));
            Assert.AreEqual(400, Send(handler, Req("POST", path, body: "{ nope")).StatusCode);
            Assert.AreEqual(400, Send(handler, Req("POST", path, body: "null")).StatusCode);
            Assert.AreEqual(413, Send(handler, Req("POST", path, body: "{\"dryRun\":false,\"x\":\"" + new string('a', AdminApiHandler.MaxBodyBytes) + "\"}")).StatusCode);
            Assert.AreEqual(405, Send(handler, Req("GET", path)).StatusCode);
            Assert.AreEqual(405, Send(handler, Req("DELETE", path)).StatusCode);
            Assert.AreEqual(401, Send(handler, Req("POST", path, auth: null)).StatusCode);
            Assert.AreEqual(2, Directory.GetFiles(_backups).Length, "nothing was deleted by any of these");
        }

        // ---- the offline verbs ------------------------------------------------------------------------------------------------------------

        private string MakeBase()
        {
            var baseDirectory = Path.Combine(_root, "srv");
            Directory.CreateDirectory(Path.Combine(baseDirectory, "Universe", "Vessels"));
            Directory.CreateDirectory(Path.Combine(baseDirectory, "Config"));
            File.WriteAllText(Path.Combine(baseDirectory, "Universe", "Vessels", "v.txt"), "vessel");
            return baseDirectory;
        }

        private static int Cli(string baseDirectory, out string output, out string error, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            Assert.IsTrue(BackupCli.TryRun(args, o, e, baseDirectory, out var code));
            output = o.ToString();
            error = e.ToString();
            return code;
        }

        private static string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        [TestMethod]
        public void Cli_Prune_DryRunListsNamesAndDeletesNothing_ThenRealRun()
        {
            var baseDirectory = MakeBase();
            File.WriteAllText(Path.Combine(baseDirectory, "Config", "vmp-server.json"), "{ \"interval\": { \"BackupKeepNewest\": 2, \"BackupKeepDailyDays\": 0 } }");
            var folder = Path.Combine(baseDirectory, "Backups");
            Directory.CreateDirectory(folder);
            for (var day = 20; day <= 24; day++) File.WriteAllBytes(Path.Combine(folder, Tar(day)), new byte[100]);

            var code = Cli(baseDirectory, out var output, out var error, "prune", "--dry-run");
            Assert.AreEqual(0, code, error);
            var lines = Lines(output);
            Assert.AreEqual(4, lines.Length, "one summary line plus three names");
            StringAssert.StartsWith(lines[0], "prune dry-run: would delete 3 file(s)");
            StringAssert.Contains(lines[0], "300 bytes");
            CollectionAssert.AreEquivalent(new[] { Tar(20), Tar(21), Tar(22) }, lines.Skip(1).ToList());
            Assert.AreEqual(5, Directory.GetFiles(folder).Length);

            code = Cli(baseDirectory, out output, out error, "prune");
            Assert.AreEqual(0, code, error);
            lines = Lines(output);
            StringAssert.StartsWith(lines[0], "prune ok: deleted 3 file(s)");
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual(2, Directory.GetFiles(folder).Length);
            Assert.IsTrue(File.Exists(Path.Combine(folder, Tar(24))));
        }

        [TestMethod]
        public void Cli_Prune_DirOption_Defaults_AndErrors()
        {
            var baseDirectory = MakeBase();
            var other = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(other);
            for (var day = 1; day <= 5; day++) File.WriteAllBytes(Path.Combine(other, Zip(day)), new byte[10]);
            File.WriteAllBytes(Path.Combine(other, Tar(24)), new byte[10]);

            // No settings file: defaults (zips untouched without the option); the unreadable case is covered below.
            Assert.AreEqual(0, Cli(baseDirectory, out var output, out var error, "prune", "--dir", other, "--dry-run"), error);
            StringAssert.StartsWith(Lines(output)[0], "prune dry-run: would delete 0 file(s)");
            Assert.AreEqual(string.Empty, error, "a missing settings file is normal and silent");

            Assert.AreEqual(0, Cli(baseDirectory, out output, out error, "prune", "--dir", other, "--include-legacy-zips"), error);
            CollectionAssert.AreEquivalent(new[] { Zip(1), Zip(2) }, Lines(output).Skip(1).ToList());
            Assert.AreEqual(4, Directory.GetFiles(other).Length);

            Assert.AreEqual(1, Cli(baseDirectory, out _, out error, "prune"), "no Backups folder next to the exe");
            StringAssert.StartsWith(error, "prune FAILED");
            Assert.AreEqual(2, Cli(baseDirectory, out _, out error, "prune", "stray"), "usage error");
            Assert.AreEqual(1, Cli(baseDirectory, out _, out error, "prune", "--bogus"));
            StringAssert.Contains(error, "unknown option");
        }

        [TestMethod]
        public void Cli_Prune_UnreadableSettings_UsesDefaultsAndSaysSo()
        {
            var baseDirectory = MakeBase();
            File.WriteAllText(Path.Combine(baseDirectory, "Config", "vmp-server.json"), "{ not json at all");
            var folder = Path.Combine(baseDirectory, "Backups");
            Directory.CreateDirectory(folder);
            for (var day = 20; day <= 24; day++) File.WriteAllBytes(Path.Combine(folder, Tar(day)), new byte[100]);

            Assert.AreEqual(0, Cli(baseDirectory, out var output, out var error, "prune"));
            StringAssert.Contains(error, "using the defaults");
            StringAssert.StartsWith(Lines(output)[0], "prune ok: deleted 0 file(s)");
            Assert.AreEqual("{ not json at all", File.ReadAllText(Path.Combine(baseDirectory, "Config", "vmp-server.json")), "the settings file is never touched");
        }

        [TestMethod]
        public void Cli_Prune_AppliesTheCapFromTheSettingsFile_WithoutDeletingTheNewestOrGoingBelowThree()
        {
            var baseDirectory = MakeBase();
            File.WriteAllText(Path.Combine(baseDirectory, "Config", "vmp-server.json"), "{ \"interval\": { \"BackupMaxTotalMegabytes\": 1 } }");
            var folder = Path.Combine(baseDirectory, "Backups");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, Zip(1)), new byte[600 * 1024]);
            for (var day = 20; day <= 23; day++) File.WriteAllBytes(Path.Combine(folder, Tar(day)), new byte[600 * 1024]);

            Assert.AreEqual(0, Cli(baseDirectory, out var output, out var error, "prune"), error);
            var lines = Lines(output);
            StringAssert.Contains(lines[0], "STILL OVER CAP");
            CollectionAssert.AreEqual(new[] { Zip(1), Tar(20) }, lines.Skip(1).ToList(), "the zip first, then the oldest archive; three archives stay");
            Assert.AreEqual(3, Directory.GetFiles(folder).Length);
        }

        [TestMethod]
        public void Cli_Backup_AppliesTheRetentionOfTheSettingsFile_AndOverrideOptionsWin()
        {
            var baseDirectory = MakeBase();
            File.WriteAllText(Path.Combine(baseDirectory, "Config", "vmp-server.json"), "{ \"interval\": { \"BackupKeepNewest\": 1, \"BackupKeepDailyDays\": 0, \"BackupMaxTotalMegabytes\": 0 } }");
            var folder = Path.Combine(baseDirectory, "Backups");
            Directory.CreateDirectory(folder);
            for (var day = 20; day <= 22; day++)
            {
                File.WriteAllBytes(Path.Combine(folder, $"Universe-202010{day:00}-120000.tar.zst"), new byte[50]);
                File.WriteAllBytes(Path.Combine(folder, $"Universe-202010{day:00}-120000.tar.zst.sha256"), new byte[5]);
            }

            var code = Cli(baseDirectory, out var output, out var error, "backup", "--out", folder, "--level", "3");
            Assert.AreEqual(0, code, error);
            Assert.AreEqual(1, Lines(output).Length, "still one summary line");
            StringAssert.Contains(output, "pruned=3");
            StringAssert.Contains(output, "freed=165");
            Assert.AreEqual(1, Directory.GetFiles(folder, "Universe-*.tar.zst").Length);
            Assert.AreEqual(0, Directory.GetFiles(folder, "Universe-2020*").Length);
            global::System.Threading.Thread.Sleep(1100); // the archive name has a resolution of one second

            // The command line options replace the keep counts of the file.
            for (var day = 20; day <= 22; day++) File.WriteAllBytes(Path.Combine(folder, $"Universe-202010{day:00}-120000.tar.zst"), new byte[50]);
            code = Cli(baseDirectory, out output, out error, "backup", "--out", folder, "--level", "3", "--keep-newest", "20", "--keep-days", "0");
            Assert.AreEqual(0, code, error);
            StringAssert.Contains(output, "pruned=0");
            Assert.AreEqual(5, Directory.GetFiles(folder, "Universe-*.tar.zst").Length);
        }

        [TestMethod]
        public void Cli_Backup_WithoutASettingsFile_UsesTheDefaultPolicy()
        {
            var baseDirectory = MakeBase();
            var folder = Path.Combine(baseDirectory, "Backups");
            Directory.CreateDirectory(folder);
            for (var day = 1; day <= 12; day++) File.WriteAllBytes(Path.Combine(folder, $"Universe-202010{day:00}-120000.tar.zst"), new byte[10]);

            var code = Cli(baseDirectory, out var output, out var error, "backup", "--out", folder, "--level", "3");

            Assert.AreEqual(0, code, error);
            Assert.AreEqual(string.Empty, error, "no settings file is normal and silent");
            StringAssert.Contains(output, "pruned=3");
            var left = Directory.GetFiles(folder, "Universe-*.tar.zst").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.AreEqual(10, left.Count, "the default keeps the newest ten");
            Assert.AreEqual("Universe-20201004-120000.tar.zst", left[0]);
        }
    }
}
