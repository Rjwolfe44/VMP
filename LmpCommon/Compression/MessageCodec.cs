using CachedQuickLz;
using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using System;
using System.Diagnostics;

namespace LmpCommon.Compression
{
    /// <summary>
    /// Compresses and decompresses the payload of the messages that carry big blobs (vessels, kerbals, scenarios, contracts, craft):
    /// zstd at a level chosen by <see cref="CompressionPolicy"/>. The reader tells the formats apart by their first bytes: a zstd frame
    /// starts with 28 B5 2F FD, a QuickLZ payload (what older builds and a client that cannot compress with zstd send) is recognised by
    /// QuickLZ, everything else is raw. Used through <see cref="Common.ThreadSafeCompress"/> and <see cref="Common.ThreadSafeDecompress"/>.
    /// <para>
    /// The server decompresses what clients send, so decoding is bounded: a frame that declares more than
    /// <see cref="CompressionLimits.MaxDecodedBytes"/> is refused before anything is allocated, a frame that does not say its size is decoded
    /// into a buffer that grows only while the data demands it, and every failure is an <see cref="InvalidOperationException"/> (which the
    /// message layer treats as a bad message).
    /// </para>
    /// </summary>
    public static class MessageCodec
    {
        private static readonly object QuickLzLock = new object();
        private static CompressionSide _side = CompressionSide.Client;
        private static ICompressionConfig _config;

        static MessageCodec()
        {
            _config = ClientConfig;
        }

        /// <summary>What the client follows: the defaults until the server's settings arrive.</summary>
        public static readonly CompressionConfig ClientConfig = new CompressionConfig();

        public static readonly CompressionPolicy Policy = new CompressionPolicy();

        /// <summary>Compressed payloads the server sent before. Only the server uses it (see <see cref="UseServer"/>).</summary>
        public static readonly CompressedPayloadCache Cache = new CompressedPayloadCache(CompressionLimits.CacheMaxBytes);

        /// <summary>Which side of the connection this process is. A process is a client until <see cref="UseServer"/> says otherwise.</summary>
        public static CompressionSide Side
        {
            get { return _side; }
        }

        public static ICompressionConfig Config
        {
            get { return _config; }
        }

        /// <summary>The server calls this once at start with a config that reads the live settings.</summary>
        public static void UseServer(ICompressionConfig config)
        {
            Configure(CompressionSide.Server, config);
        }

        /// <summary>Back to the client defaults (tests).</summary>
        public static void UseClient()
        {
            Configure(CompressionSide.Client, ClientConfig);
        }

        public static void Configure(CompressionSide side, ICompressionConfig config)
        {
            _config = config ?? ClientConfig;
            _side = side;
        }

        /// <summary>True when the payload already starts like a zstd frame or a QuickLZ payload.</summary>
        public static bool IsCompressedPayload(byte[] data, int length)
        {
            if (data == null || length <= 0 || length > data.Length) return false;
            return ZstdBackend.IsFrame(data, length) || CachedQlz.IsCompressed(data, length);
        }

        /// <summary>
        /// Compresses the first <paramref name="numBytes"/> bytes of <paramref name="data"/>; on success <paramref name="data"/> is a new
        /// array holding the compressed bytes and <paramref name="numBytes"/> their count. Otherwise (too small, already compressed, no
        /// gain of at least <see cref="CompressionLimits.MinGainBytes"/>) both are left as they were.
        /// </summary>
        public static void Compress(ref byte[] data, ref int numBytes, string statsName)
        {
            var length = numBytes;
            if (data == null || length <= 0 || length > data.Length) return;
            if (IsCompressedPayload(data, length)) return;

            var side = _side;
            var config = _config;
            var level = Policy.ChooseLevel(side, length, statsName, config);
            if (level <= 0) return;

            if (!ZstdBackend.CanCompress)
            {
                CompressWithQuickLz(ref data, ref numBytes, statsName);
                return;
            }

            var useCache = side == CompressionSide.Server && !string.IsNullOrEmpty(statsName);
            ulong hash = 0;
            if (useCache)
            {
                hash = CompressedPayloadCache.HashOf(data, length);
                byte[] cached;
                if (Cache.TryGet(statsName, hash, length, level, out cached))
                {
                    VmpTrafficStats.RecordCacheHit();
                    VmpTrafficStats.RecordMessageCompression(statsName, SnapshotEncodings.Zstd, length, cached.Length, level);
                    data = cached;
                    numBytes = cached.Length;
                    return;
                }

                VmpTrafficStats.RecordCacheMiss();
            }

            byte[] output;
            int written;
            var started = Stopwatch.GetTimestamp();
            var ok = ZstdBackend.TryCompressAtLevel(data, length, level, out output, out written);
            var micros = (Stopwatch.GetTimestamp() - started) * 1000000.0 / Stopwatch.Frequency;
            if (!ok)
            {
                CompressWithQuickLz(ref data, ref numBytes, statsName);
                return;
            }

            Policy.Report(side, level, length, micros);
            if (written + CompressionLimits.MinGainBytes > length) return; // not worth it: the raw bytes go out

            if (useCache)
            {
                var exact = new byte[written];
                Buffer.BlockCopy(output, 0, exact, 0, written);
                Cache.Add(statsName, hash, length, level, exact);
            }

            VmpTrafficStats.RecordMessageCompression(statsName, SnapshotEncodings.Zstd, length, written, level);
            data = output;
            numBytes = written;
        }

        /// <summary>
        /// Restores a payload: zstd frame, QuickLZ payload or raw bytes (returned as they are). Throws <see cref="InvalidOperationException"/>
        /// for anything corrupt or above <see cref="CompressionLimits.MaxDecodedBytes"/>.
        /// </summary>
        public static void Decompress(ref byte[] data, int length, out int numBytes, string statsName)
        {
            if (data == null || length < 0 || length > data.Length)
                throw new InvalidOperationException("The compressed payload has an impossible length");

            if (ZstdBackend.IsFrame(data, length))
            {
                byte[] raw;
                string error;
                if (!ZstdBackend.TryDecompressFrame(data, length, CompressionLimits.MaxDecodedBytes, out raw, out error))
                    throw new InvalidOperationException("Cannot decode a zstd payload: " + error);

                data = raw;
                numBytes = raw.Length;
                VmpTrafficStats.RecordMessageCompression(statsName, SnapshotEncodings.Zstd, numBytes, length);
                return;
            }

            if (length > 0 && CachedQlz.IsCompressed(data, length))
            {
                var declared = QuickLzDeclaredSize(data, length);
                if (declared > CompressionLimits.MaxDecodedBytes)
                    throw new InvalidOperationException("A QuickLZ payload declares " + declared + " bytes, above the limit of " + CompressionLimits.MaxDecodedBytes);

                try
                {
                    CachedQlz.Decompress(ref data, out numBytes);
                }
                catch (Exception e) when (!(e is InvalidOperationException))
                {
                    throw new InvalidOperationException("Cannot decode a QuickLZ payload: " + e.Message, e);
                }

                VmpTrafficStats.RecordMessageCompression(statsName, SnapshotEncodings.QuickLz, numBytes, length);
                return;
            }

            numBytes = length;
        }

        /// <summary>QuickLZ for a process that cannot compress with zstd (a KSP client whose Unsafe.dll is older than 5.0), and as the fallback when zstd fails.</summary>
        private static void CompressWithQuickLz(ref byte[] data, ref int numBytes, string statsName)
        {
            byte[] output;
            int written;
            if (!TryQuickLzCompress(data, numBytes, out output, out written)) return;
            if (written + CompressionLimits.MinGainBytes > numBytes) return;

            VmpTrafficStats.RecordMessageCompression(statsName, SnapshotEncodings.QuickLz, numBytes, written);
            data = output;
            numBytes = written;
        }

        /// <summary>QuickLZ-compresses a private copy (the library recycles the array it is given). Used by the fallback and the benchmark.</summary>
        public static bool TryQuickLzCompress(byte[] data, int length, out byte[] output, out int written)
        {
            output = null;
            written = 0;
            if (data == null || length <= 0 || length > data.Length) return false;
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            var count = length;
            lock (QuickLzLock)
            {
                CachedQlz.Compress(ref copy, ref count);
            }

            output = copy;
            written = count;
            return true;
        }

        /// <summary>The decoded size a QuickLZ payload declares in its header, or -1 when the header is too short to say.</summary>
        public static long QuickLzDeclaredSize(byte[] data, int length)
        {
            if (data == null || length < 3 || length > data.Length) return -1;
            // Flag bit 1 set: 9-byte header (compressed size at 1, decoded size at 5, 32 bit little endian). Clear: 3-byte header, one byte each.
            if ((data[0] & 2) != 0) return length < 9 ? -1L : (long)BitConverter.ToUInt32(data, 5);
            return data[2];
        }
    }
}
