using Server.Settings.Definition;
using System;

namespace Server.System.Backup
{
    /// <summary>The backup retention numbers in force (from the interval settings, or defaults when they cannot be read).</summary>
    public sealed class RetentionPolicy
    {
        public const int DefaultMaxTotalMegabytes = 2048;
        /// <summary>How many zips an explicit "also old zips" prune keeps when BackupLegacyZipKeep is 0.</summary>
        public const int ExplicitLegacyZipKeep = 3;
        /// <summary>The size cap never deletes the newest Universe archive and never leaves fewer than this many Universe archives.</summary>
        public const int MinArchivesForCap = 3;

        /// <summary>Universe-*.tar.zst: how many of the newest archives are always kept.</summary>
        public int KeepNewest = 10;
        /// <summary>Universe-*.tar.zst: besides the newest ones, the newest archive of each of the last N days is kept.</summary>
        public int KeepDailyDays = 14;
        /// <summary>Screenshots-*.tar: how many of the newest archives are kept.</summary>
        public int KeepScreenshotArchives = 10;
        /// <summary>Old Universe-*.zip backups: how many of the newest are kept when pruning; 0 = the keep rules never delete zips.</summary>
        public int LegacyZipKeep;
        /// <summary>Cap on the total size of every file in the Backups folder, in bytes; 0 = no cap.</summary>
        public long MaxTotalBytes = DefaultMaxTotalMegabytes * 1024L * 1024L;

        public int MaxTotalMegabytes => (int)Math.Min(int.MaxValue, MaxTotalBytes / (1024L * 1024L));

        public static RetentionPolicy FromSettings(IntervalSettingsDefinition settings)
        {
            if (settings == null) return new RetentionPolicy();
            return new RetentionPolicy
            {
                KeepNewest = Math.Max(0, settings.BackupKeepNewest),
                KeepDailyDays = Math.Max(0, settings.BackupKeepDailyDays),
                KeepScreenshotArchives = Math.Max(0, settings.BackupKeepScreenshotArchives),
                LegacyZipKeep = Math.Max(0, settings.BackupLegacyZipKeep),
                MaxTotalBytes = Math.Max(0, settings.BackupMaxTotalMegabytes) * 1024L * 1024L,
            };
        }

        public RetentionPolicy Clone() => (RetentionPolicy)MemberwiseClone();
    }
}
