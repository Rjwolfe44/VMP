using LmpCommon.Compression;
using LmpCommon.Xml;
using System;

namespace Server.Settings.Definition
{
    [Serializable]
    public class CompressionSettingsDefinition
    {
        [XmlComment(Value = "How the server picks the zstd level for vessel, kerbal, scenario, contract and craft data. Adaptive: the level follows the payload size and how long compressing takes, never above MaxAdaptiveLevel. Fixed: always ServerLevel. Applies immediately.")]
        public CompressionMode Mode { get; set; } = CompressionMode.Adaptive;

        [XmlComment(Value = "Zstd level the server uses when Mode is Fixed, and the middle of the adaptive range. Higher is smaller but slower (min value is 1, max value is 19). Applies immediately.")]
        public int ServerLevel { get; set; } = CompressionLimits.DefaultServerLevel;

        [XmlComment(Value = "Highest zstd level clients use for what they send (min value is 1, max value is 9). Clients never go above 9, and never above 3 for payloads over 4 MB, whatever this says. Sent to each client when it joins.")]
        public int ClientLevel { get; set; } = CompressionLimits.DefaultClientLevel;

        [XmlComment(Value = "Highest level adaptive mode may use on the server (min value is 1, max value is 19). Applies immediately.")]
        public int MaxAdaptiveLevel { get; set; } = CompressionLimits.DefaultMaxAdaptiveLevel;

        [XmlComment(Value = "Payloads smaller than this many bytes are sent without compression (max value is 65536). Sent to each client when it joins.")]
        public int MinSizeBytes { get; set; } = CompressionLimits.DefaultMinSizeBytes;
    }
}
