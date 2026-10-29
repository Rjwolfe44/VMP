using Server.Admin;
using Server.Context;
using Server.Events;
using Server.Log;
using Server.Settings.Structures;
using Server.System.Backup;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Server.System
{
    public class BackupSystem
    {
        //Subscribe to the exit event so a backup is performed when closing the server
        static BackupSystem() => ExitEvent.ServerClosing += RunBackup;

        private static readonly object LockObj = new object();

        public static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string BackupsDirectory = Path.Combine(BaseDirectory, "Backups");
        private static DateTime _lastArchiveUtc = DateTime.UtcNow;

        /// <summary>
        /// Compressed archive (tar.zst) of Universe + Config xml, then retention. One at a time: when one is already running the outcome has
        /// AlreadyRunning set. Used by the periodic backup and by POST /api/v1/backups.
        /// </summary>
        public static Task<BackupOutcome> CreateArchiveAsync(bool screenshotArchive = false)
        {
            var settings = IntervalSettings.SettingsStore;
            return BackupService.StartAsync(BaseDirectory, BackupsDirectory, RunBackup, CurrentPolicy(), null, settings.BackupIncludeScreenshots, screenshotArchive);
        }

        /// <summary>The retention numbers from the live interval settings (read again on every use, so a change applies at once).</summary>
        public static RetentionPolicy CurrentPolicy() => RetentionPolicy.FromSettings(IntervalSettings.SettingsStore);

        /// <summary>List, size, delete and prune of the Backups folder for the admin API.</summary>
        public static BackupAdmin Admin => new BackupAdmin(BackupsDirectory, CurrentPolicy, () => BackupService.IsRunning, () => DateTime.UtcNow, LunaLog.Normal);

        private static int _startPruneDone;

        /// <summary>Applies the retention rules once when the server has started (old zips only if BackupLegacyZipKeep is above 0), so a folder that grew while the server was off shrinks again.</summary>
        public static void PruneAtStart()
        {
            if (Interlocked.Exchange(ref _startPruneDone, 1) != 0) return;
            try
            {
                var result = Admin.Prune(false, false);
                if (result.Running) return;
                if (result.Deleted.Count > 0) LunaLog.Normal($"[Backup] startup prune: deleted {result.Deleted.Count} file(s), {result.FreedBytes} bytes freed");
                if (result.StillOverCap) LunaLog.Warning($"[Backup] the Backups folder is still {result.TotalBytesAfter / (1024 * 1024)} MB, above the {result.CapBytes / (1024 * 1024)} MB cap: the newest archive and at least {RetentionPolicy.MinArchivesForCap} archives are always kept");
                foreach (var name in result.Failed) LunaLog.Warning($"[Backup] startup prune could not delete {name} (in use)");
            }
            catch (Exception e)
            {
                LunaLog.Error($"[Backup] startup prune failed: {e.Message}");
            }
        }

        private static void MaybeStartArchive()
        {
            var minutes = IntervalSettings.SettingsStore.ArchiveBackupIntervalMinutes;
            if (minutes <= 0 || BackupService.IsRunning || DateTime.UtcNow - _lastArchiveUtc < TimeSpan.FromMinutes(minutes)) return;

            _lastArchiveUtc = DateTime.UtcNow;
            // Screenshots stay out of the main archive; once a day the new ones get their own tar (see ScreenshotArchiver).
            var screenshotsDue = ScreenshotArchiver.IsDue(BackupsDirectory, DateTime.UtcNow, TimeSpan.FromHours(24));
            CreateArchiveAsync(screenshotsDue).ContinueWith(LogOutcome, TaskContinuationOptions.ExecuteSynchronously);
        }

        public static void LogOutcome(Task<BackupOutcome> task)
        {
            var outcome = task.IsFaulted ? new BackupOutcome { Error = task.Exception?.GetBaseException().Message ?? "unknown error" } : task.Result;
            if (outcome.AlreadyRunning) return;

            if (outcome.Error != null)
            {
                LunaLog.Error($"[Backup] archive failed: {outcome.Error}");
                return;
            }

            var freed = outcome.Retention?.FreedBytes ?? 0;
            LunaLog.Normal($"[Backup] {outcome.Result.FileName}: {outcome.Result.Summary()}, pruned {outcome.Pruned.Count}" + (freed > 0 ? $" ({freed} bytes freed)" : string.Empty));
            if (outcome.RetentionError != null)
                LunaLog.Error($"[Backup] retention failed: {outcome.RetentionError}");
            if (outcome.Retention != null && outcome.Retention.StillOverCap)
                LunaLog.Warning($"[Backup] the Backups folder is {outcome.Retention.TotalBytesAfter / (1024 * 1024)} MB, above the {outcome.Retention.CapBytes / (1024 * 1024)} MB cap, but the newest archive and at least {RetentionPolicy.MinArchivesForCap} archives are always kept");
            if (outcome.Screenshots != null)
                LunaLog.Normal($"[Backup] {outcome.Screenshots.FileName}: {outcome.Screenshots.Files} new screenshots, {outcome.Screenshots.ArchiveBytes} bytes, pruned {outcome.PrunedScreenshots.Count}");
            if (outcome.ScreenshotError != null)
                LunaLog.Error($"[Backup] screenshot archive failed: {outcome.ScreenshotError}");
            foreach (var skipped in outcome.Result.Skipped) LunaLog.Warning($"[Backup] not included: {skipped}");
        }

        public static async void PerformBackups(CancellationToken token)
        {
            PruneAtStart();
            while (ServerContext.ServerRunning)
            {
                if (ServerContext.PlayerCount > 0)
                {
                    RunBackup();
                    MaybeStartArchive();
                }

                try
                {
                    await Task.Delay(IntervalSettings.SettingsStore.BackupIntervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        public static void RunBackup()
        {
            lock (LockObj)
            {
                LunaLog.Debug("Performing backups...");
                VesselStoreSystem.BackupVessels();
                WarpSystem.BackupSubspaces();
                TimeSystem.BackupStartTime();
                ScenarioStoreSystem.BackupScenarios();
            }
        }
    }
}
