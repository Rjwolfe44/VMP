using System;
using System.Diagnostics;

namespace LmpCommon.Compression
{
    /// <summary>How the server picks the zstd level for message payloads.</summary>
    public enum CompressionMode
    {
        /// <summary>The level follows the payload size and how long compressing takes, never above MaxAdaptiveLevel.</summary>
        Adaptive = 0,
        /// <summary>Always ServerLevel.</summary>
        Fixed = 1,
    }

    public enum CompressionSide
    {
        Server = 0,
        Client = 1,
    }

    /// <summary>The compression settings the policy reads. The server implements it on top of its settings, the client holds the values the server sent.</summary>
    public interface ICompressionConfig
    {
        CompressionMode Mode { get; }
        int ServerLevel { get; }
        int ClientLevel { get; }
        int MaxAdaptiveLevel { get; }
        int MinSizeBytes { get; }
    }

    /// <summary>Fixed numbers shared by the policy, the codec, the settings and the documentation.</summary>
    public static class CompressionLimits
    {
        public const int MinLevel = 1;
        public const int MaxServerLevel = 19;
        /// <summary>No client ever compresses above this, whatever the server says.</summary>
        public const int MaxClientLevel = 9;
        /// <summary>A client never compresses a payload above <see cref="LargePayloadBytes"/> with more than this.</summary>
        public const int LargePayloadClientMaxLevel = 3;
        public const int LargePayloadBytes = 4 * 1024 * 1024;

        public const int DefaultServerLevel = 9;
        public const int DefaultClientLevel = 5;
        public const int DefaultMaxAdaptiveLevel = 15;
        public const int DefaultMinSizeBytes = 160;
        public const int MaxMinSizeBytes = 65536;

        /// <summary>A compressed payload must be at least this many bytes smaller than the input, otherwise the raw bytes are sent.</summary>
        public const int MinGainBytes = 8;

        /// <summary>Largest payload a peer may make us decompress (declared or actual size).</summary>
        public const int MaxDecodedBytes = 64 * 1024 * 1024;

        /// <summary>Upper bound of the compressed payload cache.</summary>
        public const long CacheMaxBytes = 64L * 1024 * 1024;

        public static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }

    /// <summary>A plain settable <see cref="ICompressionConfig"/>: the client's copy of what the server chose, the defaults, and tests.</summary>
    public sealed class CompressionConfig : ICompressionConfig
    {
        public CompressionMode Mode { get; set; } = CompressionMode.Adaptive;
        public int ServerLevel { get; set; } = CompressionLimits.DefaultServerLevel;
        public int ClientLevel { get; set; } = CompressionLimits.DefaultClientLevel;
        public int MaxAdaptiveLevel { get; set; } = CompressionLimits.DefaultMaxAdaptiveLevel;
        public int MinSizeBytes { get; set; } = CompressionLimits.DefaultMinSizeBytes;

        /// <summary>
        /// Applies the values a server sent to this client. The server is not trusted with the client's CPU: the level is bounded to
        /// 1-9 and the minimum size to 0-65536 here, whatever the message said. A value of 0 or less (a server that does not send the
        /// field) keeps the safe default.
        /// </summary>
        public void ApplyFromServer(int clientLevel, int minSizeBytes)
        {
            ClientLevel = clientLevel <= 0 ? CompressionLimits.DefaultClientLevel : CompressionLimits.Clamp(clientLevel, CompressionLimits.MinLevel, CompressionLimits.MaxClientLevel);
            MinSizeBytes = minSizeBytes < 0 ? CompressionLimits.DefaultMinSizeBytes : CompressionLimits.Clamp(minSizeBytes, 0, CompressionLimits.MaxMinSizeBytes);
        }
    }

    /// <summary>
    /// Chooses the zstd level (or "do not compress") for one payload. Pure logic with an injectable clock, so it is unit tested with
    /// made-up timings. See docs/compression.md for the rules in prose.
    /// <list type="bullet">
    /// <item>Below <c>MinSizeBytes</c>: no compression (0).</item>
    /// <item>Fixed: the server uses ServerLevel; a client uses ClientLevel (never above 9, never above 3 over 4 MB).</item>
    /// <item>Adaptive: the centre (ServerLevel, or ClientLevel on a client) plus a size delta (small payloads cost little, so they get more
    /// effort; big ones less), plus an adjustment of -18..+8 that the timing feedback moves by one at most every 5 seconds, clamped to
    /// 1..cap (MaxAdaptiveLevel on the server, ClientLevel on a client), then lowered while the measured time per KB of that level predicts
    /// more than the time budget for this payload.</item>
    /// </list>
    /// </summary>
    public sealed class CompressionPolicy
    {
        /// <summary>Time one compression may take on average before the level is lowered (microseconds).</summary>
        public const double ServerBudgetMicros = 25000;
        public const double ClientBudgetMicros = 3000;

        /// <summary>Only payloads up to this size feed the per-message average; bigger ones are limited by the size table and the per-level prediction.</summary>
        public const int TypicalMaxBytes = 256 * 1024;

        /// <summary>The payload size <see cref="EffectiveLevelNow"/> reports for.</summary>
        public const int ReferenceBytes = 32 * 1024;

        public const int MinSamplesBeforeAdjusting = 3;
        public const int MinAdjustment = -18;
        public const int MaxAdjustment = 8;
        public const double EwmaAlpha = 0.2;
        public static readonly TimeSpan AdjustInterval = TimeSpan.FromSeconds(5);

        private sealed class SideState
        {
            public int Adjustment;
            public int LastCap = 1;
            public long LastChangeMs = long.MinValue / 2;
            public double AvgMicrosPerMessage;
            public int Samples;
            public double AvgMicrosPerKb;
            public readonly double[] MicrosPerKb = new double[CompressionLimits.MaxServerLevel + 1];
            public readonly bool[] HasMicrosPerKb = new bool[CompressionLimits.MaxServerLevel + 1];
        }

        private readonly object _lock = new object();
        private readonly Func<long> _nowMs;
        private readonly SideState[] _sides = { new SideState(), new SideState() };

        public CompressionPolicy(Func<long> nowMs = null)
        {
            _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency);
        }

        /// <summary>The level for this payload; 0 means "send it uncompressed".</summary>
        /// <param name="messageType">The statistics name of the message type. Accepted so a type can be treated differently; no type is today.</param>
        public int ChooseLevel(CompressionSide side, int size, string messageType, ICompressionConfig config)
        {
            if (size <= 0) return 0;
            if (size < CompressionLimits.Clamp(config.MinSizeBytes, 0, CompressionLimits.MaxMinSizeBytes)) return 0;
            return LevelFor(side, size, config);
        }

        /// <summary>The level that would be used for a payload of <paramref name="size"/> bytes, ignoring the minimum size.</summary>
        public int LevelFor(CompressionSide side, int size, ICompressionConfig config)
        {
            var state = _sides[(int)side];
            if (side == CompressionSide.Server)
            {
                var serverLevel = CompressionLimits.Clamp(config.ServerLevel, CompressionLimits.MinLevel, CompressionLimits.MaxServerLevel);
                if (config.Mode == CompressionMode.Fixed) return serverLevel;

                var cap = CompressionLimits.Clamp(config.MaxAdaptiveLevel, CompressionLimits.MinLevel, CompressionLimits.MaxServerLevel);
                lock (_lock)
                {
                    state.LastCap = cap;
                    var level = CompressionLimits.Clamp(serverLevel + ServerDelta(size) + state.Adjustment, CompressionLimits.MinLevel, cap);
                    return Predicted(state, level, size, ServerBudgetMicros);
                }
            }

            var clientCap = CompressionLimits.Clamp(config.ClientLevel, CompressionLimits.MinLevel, CompressionLimits.MaxClientLevel);
            int clientLevel;
            if (config.Mode == CompressionMode.Fixed)
            {
                clientLevel = clientCap;
            }
            else
            {
                lock (_lock)
                {
                    state.LastCap = clientCap;
                    clientLevel = CompressionLimits.Clamp(clientCap + ClientDelta(size) + state.Adjustment, CompressionLimits.MinLevel, clientCap);
                    clientLevel = Predicted(state, clientLevel, size, ClientBudgetMicros);
                }
            }

            if (size > CompressionLimits.LargePayloadBytes && clientLevel > CompressionLimits.LargePayloadClientMaxLevel)
                clientLevel = CompressionLimits.LargePayloadClientMaxLevel;
            return clientLevel;
        }

        /// <summary>The level a typical (<see cref="ReferenceBytes"/>) payload gets right now; what the admin API reports as the level in use.</summary>
        public int EffectiveLevelNow(CompressionSide side, ICompressionConfig config)
        {
            return LevelFor(side, ReferenceBytes, config);
        }

        /// <summary>
        /// Server: the size-dependent offset from ServerLevel, from the benchmark on real vessel files (docs/compression.md): payloads up to
        /// 256 KB get +6 (level 15 with the defaults: it makes typical vessels about 2.6 times smaller than level 9 and costs about 1 to
        /// 8 ms), up to 1 MB the centre level (about 6 ms per MB), up to 4 MB five less, beyond that eight less (so level 1).
        /// </summary>
        public static int ServerDelta(int size)
        {
            if (size <= 256 * 1024) return 6;
            if (size <= 1024 * 1024) return 0;
            if (size <= 4 * 1024 * 1024) return -5;
            return -8;
        }

        /// <summary>Client: the offset from ClientLevel (which is also the client's cap).</summary>
        public static int ClientDelta(int size)
        {
            if (size <= 16 * 1024) return 0;
            if (size <= 64 * 1024) return -1;
            if (size <= 256 * 1024) return -2;
            if (size <= 1024 * 1024) return -3;
            if (size <= 4 * 1024 * 1024) return -4;
            return -5;
        }

        /// <summary>
        /// Feedback after a real compression of <paramref name="rawBytes"/> bytes at <paramref name="level"/> that took
        /// <paramref name="micros"/> microseconds (cache hits are not reported).
        /// </summary>
        public void Report(CompressionSide side, int level, int rawBytes, double micros)
        {
            if (rawBytes <= 0 || level < CompressionLimits.MinLevel || level > CompressionLimits.MaxServerLevel || micros < 0 || double.IsNaN(micros)) return;

            var state = _sides[(int)side];
            var budget = side == CompressionSide.Server ? ServerBudgetMicros : ClientBudgetMicros;
            var perKb = micros / Math.Max(rawBytes / 1024.0, 0.001);
            lock (_lock)
            {
                state.AvgMicrosPerKb = state.AvgMicrosPerKb <= 0 ? perKb : Ewma(state.AvgMicrosPerKb, perKb);
                state.MicrosPerKb[level] = state.HasMicrosPerKb[level] ? Ewma(state.MicrosPerKb[level], perKb) : perKb;
                state.HasMicrosPerKb[level] = true;

                if (rawBytes > TypicalMaxBytes) return;

                state.AvgMicrosPerMessage = state.Samples == 0 ? micros : Ewma(state.AvgMicrosPerMessage, micros);
                state.Samples++;
                if (state.Samples < MinSamplesBeforeAdjusting) return;

                var now = _nowMs();
                if (now - state.LastChangeMs < (long)AdjustInterval.TotalMilliseconds) return;

                if (state.AvgMicrosPerMessage > budget)
                {
                    if (state.Adjustment > MinAdjustment)
                    {
                        state.Adjustment--;
                        state.LastChangeMs = now;
                    }
                }
                else if (state.AvgMicrosPerMessage < budget / 3 && level < state.LastCap && state.Adjustment < MaxAdjustment)
                {
                    // Only raise while the level in use is still below the cap: otherwise the adjustment would wind up with no effect.
                    state.Adjustment++;
                    state.LastChangeMs = now;
                }
            }
        }

        /// <summary>The current feedback offset of a side (negative: lowered because compressing was slow).</summary>
        public int Adjustment(CompressionSide side)
        {
            lock (_lock) return _sides[(int)side].Adjustment;
        }

        /// <summary>Smoothed microseconds per KB over all levels, 0 until the first real compression.</summary>
        public double AvgMicrosPerKb(CompressionSide side)
        {
            lock (_lock) return _sides[(int)side].AvgMicrosPerKb;
        }

        /// <summary>Smoothed microseconds per KB measured at one level, 0 when that level was never used.</summary>
        public double MicrosPerKbAtLevel(CompressionSide side, int level)
        {
            if (level < 0 || level > CompressionLimits.MaxServerLevel) return 0;
            lock (_lock) return _sides[(int)side].MicrosPerKb[level];
        }

        /// <summary>Forgets all measurements (tests; a settings change does not need it).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _sides[0] = new SideState();
                _sides[1] = new SideState();
            }
        }

        private static int Predicted(SideState state, int level, int size, double budgetMicros)
        {
            // Lower the level while its measured speed says this payload would take longer than the budget.
            while (level > CompressionLimits.MinLevel && state.HasMicrosPerKb[level] && state.MicrosPerKb[level] * (size / 1024.0) > budgetMicros)
            {
                level--;
            }

            return level;
        }

        private static double Ewma(double previous, double sample)
        {
            return previous + EwmaAlpha * (sample - previous);
        }
    }
}
