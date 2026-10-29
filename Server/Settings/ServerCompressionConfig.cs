using LmpCommon.Compression;
using Server.Settings.Definition;
using Server.Settings.Structures;

namespace Server.Settings
{
    /// <summary>
    /// The message codec's view of the live [compression] settings: every value is read from the settings store each time it is used
    /// (so a change through the admin API or the settings file takes effect at once) and clamped to the supported range, so a hand-edited
    /// file with a silly value can never ask for level 99 or send clients level 0.
    /// </summary>
    public sealed class ServerCompressionConfig : ICompressionConfig
    {
        public static readonly ServerCompressionConfig Instance = new ServerCompressionConfig();

        private static CompressionSettingsDefinition Store => CompressionSettings.SettingsStore;

        /// <summary>Makes this process the server side of the message codec. Called once at start.</summary>
        public static void Install()
        {
            MessageCodec.UseServer(Instance);
        }

        public CompressionMode Mode => Store.Mode;

        public int ServerLevel => CompressionLimits.Clamp(Store.ServerLevel, CompressionLimits.MinLevel, CompressionLimits.MaxServerLevel);

        /// <summary>What clients are told: 1-9.</summary>
        public int ClientLevel => CompressionLimits.Clamp(Store.ClientLevel, CompressionLimits.MinLevel, CompressionLimits.MaxClientLevel);

        public int MaxAdaptiveLevel => CompressionLimits.Clamp(Store.MaxAdaptiveLevel, CompressionLimits.MinLevel, CompressionLimits.MaxServerLevel);

        public int MinSizeBytes => CompressionLimits.Clamp(Store.MinSizeBytes, 0, CompressionLimits.MaxMinSizeBytes);
    }
}
