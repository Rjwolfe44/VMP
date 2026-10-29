using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Server.System.Backup
{
    public sealed class BackupOutcome
    {
        public bool AlreadyRunning;
        public BackupResult Result;
        public string Error;
        public List<string> Pruned = new List<string>();
        /// <summary>The daily screenshot archive made right after the main one (null when none was due or nothing was new).</summary>
        public ScreenshotArchiveResult Screenshots;
        public string ScreenshotError;
        public List<string> PrunedScreenshots = new List<string>();
    }

    /// <summary>
    /// Runs one archive backup at a time on its own below-normal-priority thread, then applies the retention rules. Used by the
    /// periodic backup and by POST /api/v1/backups; a second request while one is running is answered with <see cref="BackupOutcome.AlreadyRunning"/>.
    /// </summary>
    public static class BackupService
    {
        private static int _running;

        public static bool IsRunning => Volatile.Read(ref _running) != 0;

        /// <param name="beforeBackup">Called on the backup thread first, to flush in-memory state to the Universe folder.</param>
        /// <param name="includeScreenshots">Put Universe\Screenshots into the main archive (normally they are left out).</param>
        /// <param name="screenshotArchive">After the main archive, also pack the screenshots that are new since the previous Screenshots-*.tar.</param>
        public static Task<BackupOutcome> StartAsync(string baseDirectory, string outputFolder, Action beforeBackup, int keepNewest, int keepDailyDays,
            Func<DateTime> utcNow = null, bool includeScreenshots = false, bool screenshotArchive = false, int keepScreenshotArchives = 10)
        {
            var source = new TaskCompletionSource<BackupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                source.SetResult(new BackupOutcome { AlreadyRunning = true });
                return source.Task;
            }

            var clock = utcNow ?? (() => DateTime.UtcNow);
            var thread = new Thread(() =>
            {
                var outcome = new BackupOutcome();
                try
                {
                    beforeBackup?.Invoke();
                    var now = clock();
                    outcome.Result = BackupWriter.Create(baseDirectory, outputFolder, now, 0, includeScreenshots);
                    outcome.Pruned = BackupRetention.Apply(outputFolder, now, keepNewest, keepDailyDays, outcome.Result.FileName);

                    if (screenshotArchive && !includeScreenshots)
                    {
                        try
                        {
                            outcome.Screenshots = ScreenshotArchiver.CreateIfNew(baseDirectory, outputFolder, now);
                            outcome.PrunedScreenshots = ScreenshotArchiver.ApplyRetention(outputFolder, keepScreenshotArchives, outcome.Screenshots?.FileName);
                        }
                        catch (Exception e)
                        {
                            outcome.ScreenshotError = e.Message;
                        }
                    }
                }
                catch (Exception e)
                {
                    outcome.Error = e.Message;
                    outcome.Result = null;
                }
                finally
                {
                    Volatile.Write(ref _running, 0);
                }

                source.SetResult(outcome);
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "VMP archive backup",
            };
            thread.Start();
            return source.Task;
        }
    }
}
