using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace LmpCommon.PersistentSync
{
    /// <summary>
    /// Late-bound access to ZstdSharp (the pure managed Zstandard port), so LmpCommon compiles into the KSP client and the
    /// server without a compile-time reference to it.
    /// <para>
    /// Why late-bound: ZstdSharp is built against System.Runtime.CompilerServices.Unsafe 6.0 and System.Memory 4.5.5, the
    /// client ships Unsafe 6.0.0 and Memory 4.5.4 (KSP's Mono binds by name and ignores the version). Compression calls
    /// Unsafe.SkipInit, added in Unsafe 5.0: with the Unsafe 4.5.x the client used to ship only decompression worked, so the
    /// client sent QuickLZ. With Unsafe 6.0.0 (External\Dependencies\ZstdSharp.README.txt) both directions work. Another mod
    /// can still win KSP's duplicate-assembly choice with an older Unsafe, and then the self test below fails and the client
    /// falls back to QuickLZ for compression while still decoding zstd. Only the array overloads are used, so no Span (which
    /// would tie the call sites to one System.Memory version) appears in any signature.
    /// </para>
    /// <para>
    /// Nothing here may throw to the caller: when ZstdSharp is missing or broken <see cref="CanDecompress"/> and
    /// <see cref="CanCompress"/> stay false and the sender falls back to QuickLZ or raw payloads.
    /// </para>
    /// </summary>
    public static class ZstdBackend
    {
        public const int CompressionLevel = 3;
        private const string AssemblyName = "ZstdSharp";

        /// <summary>Set to anything non-empty to switch Zstd off in this process.</summary>
        public const string DisableVariable = "VMP_DISABLE_ZSTD";

        private delegate int ArrayCodecDelegate(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength);
        private delegate void LoadDictionaryDelegate(byte[] dictionary);
        private delegate int CompressBoundDelegate(int length);

        private sealed class Codec
        {
            public readonly object Lock = new object();
            public ArrayCodecDelegate Run;
            public LoadDictionaryDelegate LoadDictionary;
        }

        private static readonly object InitLock = new object();
        private static bool _initialized;
        private static Codec _plainCompressor;
        private static Codec _dictionaryCompressor;
        private static Codec _plainDecompressor;
        private static Codec _dictionaryDecompressor;
        private static CompressBoundDelegate _compressBound;
        private static bool _canDecompress;
        private static bool _canCompress;

        /// <summary>Why the backend is unavailable (or the last failure), for logs. Empty when everything works.</summary>
        public static string Error { get; private set; } = string.Empty;

        /// <summary>True when frames, plain and with a dictionary, were decoded correctly by the built-in self test.</summary>
        public static bool CanDecompress
        {
            get
            {
                EnsureInitialized();
                return _canDecompress;
            }
        }

        /// <summary>
        /// True when a compress/decompress round trip, plain and with a dictionary, worked in this process. The server always can; the
        /// KSP client can when the Unsafe.dll KSP picked is 5.0 or newer (the client logs it as "compresses with zstd").
        /// </summary>
        public static bool CanCompress
        {
            get
            {
                EnsureInitialized();
                return _canCompress;
            }
        }

        /// <summary>Largest compressed size <see cref="TryCompress"/> can produce for <paramref name="length"/> input bytes.</summary>
        public static int CompressBound(int length)
        {
            EnsureInitialized();
            return _compressBound != null ? _compressBound(length) : length + (length >> 7) + 512;
        }

        /// <summary>Compresses <paramref name="length"/> bytes of <paramref name="src"/>, optionally using <paramref name="dictionary"/> (raw content) as the reference.</summary>
        public static bool TryCompress(byte[] src, int length, byte[] dictionary, out byte[] output, out int outputLength)
        {
            output = null;
            outputLength = 0;
            EnsureInitialized();
            return CompressCore(src, length, dictionary, out output, out outputLength);
        }

        /// <summary>Decompresses exactly <paramref name="rawLength"/> bytes; false on any failure (wrong dictionary, corrupt frame, size mismatch).</summary>
        public static bool TryDecompress(byte[] src, int length, byte[] dictionary, int rawLength, out byte[] raw)
        {
            raw = null;
            EnsureInitialized();
            return DecompressCore(src, length, dictionary, rawLength, out raw);
        }

        #region Message payloads: any level, pooled codecs, bounded decoding

        /// <summary>The four bytes every zstd frame starts with (little-endian 0xFD2FB528).</summary>
        public const byte FrameMagic0 = 0x28, FrameMagic1 = 0xB5, FrameMagic2 = 0x2F, FrameMagic3 = 0xFD;

        /// <summary>Highest level the library accepts for the payload codec; the message policy stays far below it.</summary>
        public const int MaxPayloadLevel = 22;

        private const int MaxPooledPerLevel = 4;
        private const int MaxPooledDecompressors = 4;
        private static readonly Type[] ArrayArgs = { typeof(byte[]), typeof(int), typeof(int), typeof(byte[]), typeof(int), typeof(int) };
        private static readonly ConcurrentQueue<Codec>[] LevelPools = CreateLevelPools();
        private static readonly ConcurrentQueue<Codec> DecompressorPool = new ConcurrentQueue<Codec>();
        private static Type _compressorType;
        private static Type _decompressorType;

        private static ConcurrentQueue<Codec>[] CreateLevelPools()
        {
            var pools = new ConcurrentQueue<Codec>[MaxPayloadLevel + 1];
            for (var i = 0; i < pools.Length; i++) pools[i] = new ConcurrentQueue<Codec>();
            return pools;
        }

        /// <summary>True when <paramref name="data"/> starts with the zstd frame magic.</summary>
        public static bool IsFrame(byte[] data, int length)
        {
            return data != null && length >= 4 && length <= data.Length &&
                   data[0] == FrameMagic0 && data[1] == FrameMagic1 && data[2] == FrameMagic2 && data[3] == FrameMagic3;
        }

        /// <summary>
        /// Reads what a frame says about itself without decoding anything: the size of the decoded content (-1 when the frame does
        /// not say) and the id of the dictionary it needs (0 for none). False when the bytes are not a valid frame header.
        /// </summary>
        public static bool TryReadFrameHeader(byte[] data, int length, out long contentSize, out long dictionaryId)
        {
            contentSize = -1;
            dictionaryId = 0;
            if (!IsFrame(data, length) || length < 6) return false;

            var descriptor = data[4];
            if ((descriptor & 0x08) != 0) return false; // reserved bit
            var sizeFlag = descriptor >> 6;
            var singleSegment = (descriptor & 0x20) != 0;
            var dictFlag = descriptor & 3;
            var position = 5;
            if (!singleSegment) position++; // window descriptor

            var dictionaryBytes = dictFlag == 0 ? 0 : dictFlag == 1 ? 1 : dictFlag == 2 ? 2 : 4;
            var sizeBytes = sizeFlag == 0 ? (singleSegment ? 1 : 0) : sizeFlag == 1 ? 2 : sizeFlag == 2 ? 4 : 8;
            if (length < position + dictionaryBytes + sizeBytes) return false;

            dictionaryId = (long)ReadLittleEndian(data, position, dictionaryBytes);
            position += dictionaryBytes;
            if (sizeBytes > 0)
            {
                var value = ReadLittleEndian(data, position, sizeBytes);
                if (sizeBytes == 2) value += 256;
                contentSize = value > long.MaxValue ? long.MaxValue : (long)value;
            }

            return true;
        }

        private static ulong ReadLittleEndian(byte[] data, int offset, int count)
        {
            ulong value = 0;
            for (var i = 0; i < count; i++) value |= (ulong)data[offset + i] << (8 * i);
            return value;
        }

        /// <summary>
        /// Compresses <paramref name="length"/> bytes at <paramref name="level"/> (1-22) with a pooled compressor of that level, so
        /// nothing but the output buffer is allocated per call. The frame carries the content size. False when zstd cannot compress
        /// here (a KSP client whose Unsafe.dll is older than 5.0) or on any failure; never throws.
        /// </summary>
        public static bool TryCompressAtLevel(byte[] src, int length, int level, out byte[] output, out int outputLength)
        {
            output = null;
            outputLength = 0;
            EnsureInitialized();
            if (!_canCompress || _compressBound == null || _compressorType == null || src == null || length < 0 || length > src.Length) return false;

            if (level < 1) level = 1;
            if (level > MaxPayloadLevel) level = MaxPayloadLevel;
            var pool = LevelPools[level];
            Codec codec;
            if (!pool.TryDequeue(out codec))
            {
                try
                {
                    var instance = Activator.CreateInstance(_compressorType, level);
                    codec = new Codec { Run = (ArrayCodecDelegate)Delegate.CreateDelegate(typeof(ArrayCodecDelegate), instance, _compressorType.GetMethod("Wrap", ArrayArgs)) };
                }
                catch (Exception e)
                {
                    Error = "compress: " + Describe(e);
                    return false;
                }
            }

            try
            {
                var buffer = new byte[_compressBound(length)];
                var written = codec.Run(src, 0, length, buffer, 0, buffer.Length);
                if (pool.Count < MaxPooledPerLevel) pool.Enqueue(codec);
                if (written <= 0) return false;
                output = buffer;
                outputLength = written;
                return true;
            }
            catch (Exception e)
            {
                Error = "compress: " + Describe(e); // the codec is dropped, not returned to the pool
                return false;
            }
        }

        /// <summary>
        /// Decodes one frame that came from a peer. The frame's own header is checked first: a declared content size above
        /// <paramref name="maxBytes"/> or a dictionary id (this build has none) is refused without decoding. A frame that does not say
        /// its size is decoded into a buffer that only grows while the data demands it, never past <paramref name="maxBytes"/>; a frame
        /// that declares a huge size but is tiny does not get a huge buffer up front either. Never throws.
        /// </summary>
        public static bool TryDecompressFrame(byte[] src, int length, int maxBytes, out byte[] raw, out string error)
        {
            raw = null;
            error = null;
            long declared;
            long dictionaryId;
            if (!TryReadFrameHeader(src, length, out declared, out dictionaryId))
            {
                error = "not a valid zstd frame header";
                return false;
            }

            if (dictionaryId != 0)
            {
                error = "the frame needs dictionary " + dictionaryId + ", which this build does not have";
                return false;
            }

            if (declared > maxBytes)
            {
                error = "the frame declares " + declared + " bytes, above the limit of " + maxBytes;
                return false;
            }

            EnsureInitialized();
            if (!_canDecompress || _decompressorType == null)
            {
                error = "zstd decoding is not available in this process" + (string.IsNullOrEmpty(Error) ? string.Empty : " (" + Error + ")");
                return false;
            }

            long target = declared >= 0 ? declared : maxBytes;
            var size = Math.Min(target, Math.Max(1L << 20, (long)length * 64));
            for (;;)
            {
                var buffer = new byte[size];
                int written;
                bool tooSmall;
                if (TryUnwrap(src, length, buffer, out written, out tooSmall))
                {
                    if (declared >= 0 && written != declared)
                    {
                        error = "the frame decoded to " + written + " bytes but declared " + declared;
                        return false;
                    }

                    if (written == buffer.Length)
                    {
                        raw = buffer;
                    }
                    else
                    {
                        raw = new byte[written];
                        Buffer.BlockCopy(buffer, 0, raw, 0, written);
                    }

                    return true;
                }

                if (!tooSmall)
                {
                    error = "the frame is corrupt or truncated";
                    return false;
                }

                if (size >= target)
                {
                    error = "the frame decodes to more than " + (declared >= 0 ? "its declared size" : "the limit of " + maxBytes + " bytes");
                    return false;
                }

                size = Math.Min(target, size * 4);
            }
        }

        private static bool TryUnwrap(byte[] src, int length, byte[] destination, out int written, out bool tooSmall)
        {
            written = 0;
            tooSmall = false;
            Codec codec;
            if (!DecompressorPool.TryDequeue(out codec))
            {
                try
                {
                    var instance = Activator.CreateInstance(_decompressorType);
                    codec = new Codec { Run = (ArrayCodecDelegate)Delegate.CreateDelegate(typeof(ArrayCodecDelegate), instance, _decompressorType.GetMethod("Unwrap", ArrayArgs)) };
                }
                catch (Exception e)
                {
                    Error = "decompress: " + Describe(e);
                    return false;
                }
            }

            try
            {
                written = codec.Run(src, 0, length, destination, 0, destination.Length);
                if (DecompressorPool.Count < MaxPooledDecompressors) DecompressorPool.Enqueue(codec);
                return written >= 0;
            }
            catch (Exception e)
            {
                Error = "decompress: " + Describe(e);
                // Only "the buffer was too small" is a reason to try a bigger one; corrupt data stops here, so a tiny hostile frame never
                // makes us allocate a big buffer.
                tooSmall = Error.IndexOf("too small", StringComparison.OrdinalIgnoreCase) >= 0;
                return false;
            }
        }

        #endregion

        private static bool CompressCore(byte[] src, int length, byte[] dictionary, out byte[] output, out int outputLength)
        {
            output = null;
            outputLength = 0;
            var codec = dictionary == null ? _plainCompressor : _dictionaryCompressor;
            if (codec == null || _compressBound == null || src == null || length < 0 || length > src.Length) return false;

            try
            {
                var buffer = new byte[_compressBound(length)];
                int written;
                lock (codec.Lock)
                {
                    if (dictionary != null) codec.LoadDictionary(dictionary);
                    written = codec.Run(src, 0, length, buffer, 0, buffer.Length);
                }

                if (written <= 0) return false;
                output = buffer;
                outputLength = written;
                return true;
            }
            catch (Exception e)
            {
                Error = "compress: " + Describe(e);
                return false;
            }
        }

        private static bool DecompressCore(byte[] src, int length, byte[] dictionary, int rawLength, out byte[] raw)
        {
            raw = null;
            var codec = dictionary == null ? _plainDecompressor : _dictionaryDecompressor;
            if (codec == null || src == null || length <= 0 || length > src.Length || rawLength < 0) return false;

            try
            {
                var buffer = new byte[rawLength];
                int written;
                lock (codec.Lock)
                {
                    if (dictionary != null) codec.LoadDictionary(dictionary);
                    written = codec.Run(src, 0, length, buffer, 0, buffer.Length);
                }

                if (written != rawLength) return false;
                raw = buffer;
                return true;
            }
            catch (Exception e)
            {
                Error = "decompress: " + Describe(e);
                return false;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (InitLock)
            {
                if (_initialized) return;
                try
                {
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableVariable)))
                    {
                        // Escape hatch for a machine where ZstdSharp misbehaves: the server then sends this client QuickLZ.
                        Error = "disabled by the " + DisableVariable + " environment variable";
                        _initialized = true;
                        return;
                    }

                    Bind();
                    _canDecompress = SelfTestDecompress();
                    _canCompress = _canDecompress && SelfTestCompress();
                }
                catch (Exception e)
                {
                    Error = "init: " + Describe(e);
                    _canDecompress = false;
                    _canCompress = false;
                }

                _initialized = true;
            }
        }

        private static void Bind()
        {
            var assembly = FindAssembly();
            if (assembly == null)
            {
                Error = "ZstdSharp.dll was not found";
                return;
            }

            var compressorType = assembly.GetType("ZstdSharp.Compressor", true);
            var decompressorType = assembly.GetType("ZstdSharp.Decompressor", true);
            _compressorType = compressorType;
            _decompressorType = decompressorType;
            var arrayArgs = new[] { typeof(byte[]), typeof(int), typeof(int), typeof(byte[]), typeof(int), typeof(int) };
            var dictionaryArgs = new[] { typeof(byte[]) };

            _compressBound = (CompressBoundDelegate)Delegate.CreateDelegate(typeof(CompressBoundDelegate),
                compressorType.GetMethod("GetCompressBound", new[] { typeof(int) }));

            Codec CreateCompressor()
            {
                var instance = Activator.CreateInstance(compressorType, CompressionLevel);
                return new Codec
                {
                    Run = (ArrayCodecDelegate)Delegate.CreateDelegate(typeof(ArrayCodecDelegate), instance, compressorType.GetMethod("Wrap", arrayArgs)),
                    LoadDictionary = (LoadDictionaryDelegate)Delegate.CreateDelegate(typeof(LoadDictionaryDelegate), instance, compressorType.GetMethod("LoadDictionary", dictionaryArgs)),
                };
            }

            Codec CreateDecompressor()
            {
                var instance = Activator.CreateInstance(decompressorType);
                return new Codec
                {
                    Run = (ArrayCodecDelegate)Delegate.CreateDelegate(typeof(ArrayCodecDelegate), instance, decompressorType.GetMethod("Unwrap", arrayArgs)),
                    LoadDictionary = (LoadDictionaryDelegate)Delegate.CreateDelegate(typeof(LoadDictionaryDelegate), instance, decompressorType.GetMethod("LoadDictionary", dictionaryArgs)),
                };
            }

            _plainDecompressor = CreateDecompressor();
            _dictionaryDecompressor = CreateDecompressor();
            _plainCompressor = CreateCompressor();
            _dictionaryCompressor = CreateCompressor();
        }

        private static Assembly FindAssembly()
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, AssemblyName, StringComparison.Ordinal)) return loaded;
            }

            try
            {
                return Assembly.Load(AssemblyName);
            }
            catch (Exception)
            {
                // Not in the default probing path (KSP plugins live in GameData); try the usual spots below.
            }

            var candidates = new[]
            {
                SafeDirectoryOf(typeof(ZstdBackend).Assembly),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, "GameData", "VladMultiplayer", "Plugins"),
                AppDomain.CurrentDomain.BaseDirectory,
            };

            foreach (var directory in candidates)
            {
                if (string.IsNullOrEmpty(directory)) continue;
                var path = Path.Combine(directory, AssemblyName + ".dll");
                if (!File.Exists(path)) continue;
                try
                {
                    return Assembly.LoadFrom(path);
                }
                catch (Exception e)
                {
                    Error = "load " + path + ": " + Describe(e);
                }
            }

            return null;
        }

        /// <summary>Reflection wraps the real failure in TargetInvocationException/TypeInitializationException; report the innermost one.</summary>
        private static string Describe(Exception e)
        {
            var inner = e;
            while ((inner is TargetInvocationException || inner is TypeInitializationException) && inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            return inner.GetType().Name + ": " + inner.Message;
        }

        private static string SafeDirectoryOf(Assembly assembly)
        {
            try
            {
                return string.IsNullOrEmpty(assembly.Location) ? null : Path.GetDirectoryName(assembly.Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Two frames made offline with ZstdSharp 0.8.5 (level 3): SelfTestPlain decodes to SelfTestText, SelfTestDelta decodes to
        // SelfTestTextChanged when SelfTestText is the dictionary. They prove decoding on their own (a process that can decode but not
        // compress, e.g. KSP with an old Unsafe.dll, still reports CanDecompress); SelfTestCompress then proves compression.
        internal static readonly string SelfTestText = "VMP zstd self test: contract{guid=1; state=Offered; funds=1000} contract{guid=2; state=Active; funds=2000} contract{guid=3; state=Offered; funds=3000}";
        internal static readonly string SelfTestTextChanged = "VMP zstd self test: contract{guid=1; state=Offered; funds=1000} contract{guid=2; state=Active; funds=2500} contract{guid=3; state=Offered; funds=3000}";
        internal static readonly byte[] SelfTestPlain = SelfTestVectors.Plain;
        internal static readonly byte[] SelfTestDelta = SelfTestVectors.Delta;

        private static bool SelfTestDecompress()
        {
            if (_plainDecompressor == null || _dictionaryDecompressor == null) return false;

            var text = System.Text.Encoding.UTF8.GetBytes(SelfTestText);
            var changed = System.Text.Encoding.UTF8.GetBytes(SelfTestTextChanged);

            if (!DecompressCore(SelfTestPlain, SelfTestPlain.Length, null, text.Length, out var plain) || !Same(plain, text))
            {
                if (string.IsNullOrEmpty(Error)) Error = "self test: plain frame did not decode";
                return false;
            }

            if (!DecompressCore(SelfTestDelta, SelfTestDelta.Length, text, changed.Length, out var delta) || !Same(delta, changed))
            {
                if (string.IsNullOrEmpty(Error)) Error = "self test: dictionary frame did not decode";
                return false;
            }

            return true;
        }

        private static bool SelfTestCompress()
        {
            var text = System.Text.Encoding.UTF8.GetBytes(SelfTestText);
            var changed = System.Text.Encoding.UTF8.GetBytes(SelfTestTextChanged);

            // CompressCore/DecompressCore put the exception (for example the missing Unsafe.SkipInit) into Error themselves.
            if (!CompressCore(text, text.Length, null, out var plain, out var plainLength) ||
                !DecompressCore(plain, plainLength, null, text.Length, out var back) || !Same(back, text))
            {
                if (string.IsNullOrEmpty(Error)) Error = "self test: plain compress round trip failed";
                return false;
            }

            if (!CompressCore(changed, changed.Length, text, out var delta, out var deltaLength) ||
                !DecompressCore(delta, deltaLength, text, changed.Length, out var backDelta) || !Same(backDelta, changed))
            {
                if (string.IsNullOrEmpty(Error)) Error = "self test: dictionary compress round trip failed";
                return false;
            }

            return true;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }
    }
}
