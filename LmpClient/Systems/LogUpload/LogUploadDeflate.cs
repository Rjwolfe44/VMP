using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LmpClient.Systems.LogUpload
{
    /// <summary>
    /// Picks a raw-deflate compressor that works in this game: System.IO.Compression.DeflateStream (in System.dll; Mono
    /// backs it with the MonoPosixHelper native library that ships with KSP), else the managed Ionic.Zlib.DeflateStream
    /// from the Ionic.Zip.dll that KSP itself ships in KSP_x64_Data/Managed (reached by reflection, so the mod neither
    /// references nor ships it), else none (entries are stored and logs trimmed harder to fit).
    /// </summary>
    internal static class LogUploadDeflate
    {
        private static readonly object Lock = new object();
        private static bool _probed;
        private static Func<Stream, Stream> _factory;
        private static string _name = "none";

        public static Func<Stream, Stream> GetFactory(out string name)
        {
            lock (Lock)
            {
                if (!_probed)
                {
                    _probed = true;
                    Func<Stream, Stream> system = s => new DeflateStream(s, CompressionMode.Compress, true);
                    if (Works(system))
                    {
                        _factory = system;
                        _name = "System.IO.Compression";
                    }
                    else
                    {
                        var ionic = TryCreateIonicFactory();
                        if (ionic != null && Works(ionic))
                        {
                            _factory = ionic;
                            _name = "Ionic.Zlib";
                        }
                    }
                }

                name = _name;
                return _factory;
            }
        }

        private static bool Works(Func<Stream, Stream> factory)
        {
            try
            {
                var input = Encoding.ASCII.GetBytes(new string('k', 8192) + " VMP deflate probe");
                using (var output = new MemoryStream())
                {
                    using (var compressor = factory(output))
                    {
                        compressor.Write(input, 0, input.Length);
                    }

                    return output.Length > 0 && output.Length < input.Length;
                }
            }
            catch (Exception)
            {
                // DllNotFoundException / EntryPointNotFoundException when the native zlib is missing, etc.
                return false;
            }
        }

        private static Func<Stream, Stream> TryCreateIonicFactory()
        {
            try
            {
                var type = Type.GetType("Ionic.Zlib.DeflateStream, Ionic.Zip", false) ??
                           AppDomain.CurrentDomain.GetAssemblies()
                               .Select(a => a.GetType("Ionic.Zlib.DeflateStream", false))
                               .FirstOrDefault(t => t != null);
                if (type == null) return null;

                var modeType = type.Assembly.GetType("Ionic.Zlib.CompressionMode", false);
                if (modeType == null) return null;

                var compress = Enum.Parse(modeType, "Compress");
                var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Stream), modeType, typeof(bool) }, null);
                if (ctor == null) return null;

                return s => (Stream)ctor.Invoke(new object[] { s, compress, true });
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
