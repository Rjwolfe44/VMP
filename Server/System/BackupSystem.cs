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
            return BackupService.StartAsync(BaseDirectory, BackupsDirectory, RunBackup, settings.BackupKeepNewest, settings.BackupKeepDailyDays, null,
                settings.BackupIncludeScreenshots, screenshotArchive, settings.BackupKeepScreenshotArchives);
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

            LunaLog.Normal($"[Backup] {outcome.Result.FileName}: {outcome.Result.Summary()}, pruned {outcome.Pruned.Count}");
            if (outcome.Screenshots != null)
                LunaLog.Normal($"[Backup] {outcome.Screenshots.FileName}: {outcome.Screenshots.Files} new screenshots, {outcome.Screenshots.ArchiveBytes} bytes, pruned {outcome.PrunedScreenshots.Count}");
            if (outcome.ScreenshotError != null)
                LunaLog.Error($"[Backup] screenshot archive failed: {outcome.ScreenshotError}");
            foreach (var skipped in outcome.Result.Skipped) LunaLog.Warning($"[Backup] not included: {skipped}");
        }

        public static async void PerformBackups(CancellationToken token)
        {
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
