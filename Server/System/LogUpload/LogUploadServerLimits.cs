using LmpCommon.LogUpload;
using System;

namespace Server.System.LogUpload
{
    /// <summary>
    /// The limits of the log upload receiver that the host can change in the server settings (general.LogUploadsPerPlayerPerHour,
    /// general.LogUploadMinSecondsBetween, general.LogUploadsPerHourServerWide, general.LogUploadFolderMaxFiles,
    /// general.LogUploadFolderMaxMegabytes). Everything else (size cap, concurrency, chunk order, automatic report budgets,
    /// timeouts) is fixed in <see cref="LogUploadLimits"/>. Values from a hand-edited file are clamped to a sane range, so a typo
    /// can neither lock everyone out for days nor remove the protection.
    /// </summary>
    public sealed class LogUploadServerLimits
    {
        public const int MaxPerHour = 1000;
        public const int MaxServerWidePerHour = 10000;
        public const int MaxGapSeconds = 3600;
        public const int MaxFolderFiles = 100000;
        public const int MaxFolderMegabytes = 1024 * 1024;

        /// <summary>The library defaults: the values of <see cref="LogUploadLimits"/>.</summary>
        public static LogUploadServerLimits Defaults { get; } = new LogUploadServerLimits(
            LogUploadLimits.MaxUploadsPerPlayerPerHour, LogUploadLimits.MinTimeBetweenUploads, LogUploadLimits.MaxUploadsPerHourServerWide,
            LogUploadLimits.FolderMaxFiles, LogUploadLimits.FolderMaxBytes);

        /// <summary>Uploads a player may start per hour by hand (automatic and host-requested ones have their own budgets).</summary>
        public int ManualPerPlayerPerHour { get; }

        /// <summary>Shortest time between the end of one upload and the start of the next from the same player, whatever the reason.</summary>
        public TimeSpan MinTimeBetweenUploads { get; }

        /// <summary>Uploads of every kind the whole server accepts per hour.</summary>
        public int PerHourServerWide { get; }

        public int FolderMaxFiles { get; }

        public long FolderMaxBytes { get; }

        public LogUploadServerLimits(int manualPerPlayerPerHour, TimeSpan minTimeBetweenUploads, int perHourServerWide, int folderMaxFiles, long folderMaxBytes)
        {
            ManualPerPlayerPerHour = Math.Max(1, Math.Min(MaxPerHour, manualPerPlayerPerHour));
            MinTimeBetweenUploads = minTimeBetweenUploads < TimeSpan.Zero ? TimeSpan.Zero : minTimeBetweenUploads > TimeSpan.FromSeconds(MaxGapSeconds) ? TimeSpan.FromSeconds(MaxGapSeconds) : minTimeBetweenUploads;
            PerHourServerWide = Math.Max(1, Math.Min(MaxServerWidePerHour, perHourServerWide));
            FolderMaxFiles = Math.Max(1, Math.Min(MaxFolderFiles, folderMaxFiles));
            FolderMaxBytes = Math.Max(1, Math.Min(MaxFolderMegabytes * 1024L * 1024L, folderMaxBytes));
        }

        /// <summary>Builds the limits from the settings values (gap in seconds, quota in megabytes).</summary>
        public static LogUploadServerLimits FromSettings(int manualPerPlayerPerHour, int minSecondsBetween, int perHourServerWide, int folderMaxFiles, int folderMaxMegabytes)
        {
            return new LogUploadServerLimits(manualPerPlayerPerHour, TimeSpan.FromSeconds(Math.Max(0, minSecondsBetween)), perHourServerWide,
                folderMaxFiles, Math.Max(1, folderMaxMegabytes) * 1024L * 1024L);
        }
    }
}
