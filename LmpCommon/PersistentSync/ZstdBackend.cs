using System;
using System.IO;
using System.Reflection;

namespace LmpCommon.PersistentSync
{
    /// <summary>
    /// Late-bound access to ZstdSharp (the pure managed Zstandard port), so LmpCommon compiles into the KSP client and the
    /// server without a compile-time reference to it.
    /// <para>
    /// Why late-bound: ZstdSharp is built against System.Runtime.CompilerServices.Unsafe 6.0 and System.Memory 4.5.5, the
    /// client ships Unsafe 4.5.3 and Memory 4.5.4 (KSP's Mono binds by name and ignores the version). Decompression works
    /// with those older DLLs, compression does not (it calls Unsafe.SkipInit, added in Unsafe 5.0), so the client only ever
    /// decompresses and the server, which runs on a current runtime, compresses. Only the array overloads are used, so no
    /// Span (which would tie the call sites to one System.Memory version) appears in any signature.
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

        /// <summary>True when a compress/decompress round trip, plain and with a dictionary, worked. Servers only.</summary>
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
        // SelfTestTextChanged when SelfTestText is the dictionary. The client cannot compress, so it proves decoding with these.
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

            if (!CompressCore(text, text.Length, null, out var plain, out var plainLength) ||
                !DecompressCore(plain, plainLength, null, text.Length, out var back) || !Same(back, text))
            {
                return false;
            }

            if (!CompressCore(changed, changed.Length, text, out var delta, out var deltaLength) ||
                !DecompressCore(delta, deltaLength, text, changed.Length, out var backDelta) || !Same(backDelta, changed))
            {
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
