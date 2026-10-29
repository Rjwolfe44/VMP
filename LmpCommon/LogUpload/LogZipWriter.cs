using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LmpCommon.LogUpload
{
    /// <summary>
    /// Minimal PKZIP writer (stored or deflated entries, no zip64) with no dependency beyond a deflate stream factory.
    /// KSP's Mono runtime ships no System.IO.Compression.dll (so no ZipArchive), and the mod must not add third-party
    /// DLLs; DeflateStream itself lives in System.dll. Entries are streamed: nothing is held in memory.
    /// </summary>
    public sealed class LogZipWriter : IDisposable
    {
        private const uint LocalHeaderSignature = 0x04034b50;
        private const uint CentralHeaderSignature = 0x02014b50;
        private const uint EndOfCentralDirectorySignature = 0x06054b50;
        private const ushort VersionNeeded = 20;
        private const ushort MethodStored = 0;
        private const ushort MethodDeflated = 8;

        private sealed class Entry
        {
            public byte[] NameBytes;
            public ushort Method;
            public ushort DosTime;
            public ushort DosDate;
            public uint Crc;
            public long CompressedSize;
            public long UncompressedSize;
            public long HeaderOffset;
        }

        private readonly Stream _output;
        private readonly Func<Stream, Stream> _deflateFactory;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _finished;

        /// <param name="output">Seekable, writable stream positioned where the zip starts (normally a new file).</param>
        /// <param name="deflateFactory">Wraps a stream in a raw-deflate compressor; null stores entries uncompressed.</param>
        public LogZipWriter(Stream output, Func<Stream, Stream> deflateFactory)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("Zip output must be seekable and writable", nameof(output));
            _output = output;
            _deflateFactory = deflateFactory;
        }

        public long Position => _output.Position;

        public int EntryCount => _entries.Count;

        /// <summary>True when <paramref name="name"/> is a safe relative entry name: ASCII letters, digits, '.', '_', '-' and '/' separators, no "..".</summary>
        public static bool IsValidEntryName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 200 || name[0] == '/' || name[name.Length - 1] == '/') return false;
            foreach (var c in name)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' || c == '/';
                if (!ok) return false;
            }

            foreach (var part in name.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..") return false;
            }

            return true;
        }

        /// <summary>
        /// Adds one entry; <paramref name="writeContent"/> writes the uncompressed bytes into the stream it is given
        /// (and must not close it).
        /// </summary>
        public void AddEntry(string name, DateTime modified, Action<Stream> writeContent)
        {
            if (_finished) throw new InvalidOperationException("Zip already finished");
            if (!IsValidEntryName(name)) throw new ArgumentException($"Invalid zip entry name '{name}'", nameof(name));
            if (!_names.Add(name)) throw new ArgumentException($"Duplicate zip entry name '{name}'", nameof(name));
            if (writeContent == null) throw new ArgumentNullException(nameof(writeContent));

            var entry = new Entry
            {
                NameBytes = Encoding.ASCII.GetBytes(name),
                Method = _deflateFactory != null ? MethodDeflated : MethodStored,
                HeaderOffset = _output.Position,
            };
            ToDosDateTime(modified, out entry.DosTime, out entry.DosDate);

            // Sizes and CRC are patched in once the data is written.
            WriteLocalHeader(entry);
            var dataStart = _output.Position;

            var crc = new Crc32();
            var target = new NonClosingStream(_output);
            if (entry.Method == MethodDeflated)
            {
                var compressor = _deflateFactory(target);
                try
                {
                    using (var counter = new CrcCountingStream(compressor, crc))
                    {
                        writeContent(counter);
                        entry.UncompressedSize = counter.BytesWritten;
                    }
                }
                finally
                {
                    // Flushes the final deflate block into the zip file.
                    compressor.Dispose();
                }
            }
            else
            {
                using (var counter = new CrcCountingStream(target, crc))
                {
                    writeContent(counter);
                    entry.UncompressedSize = counter.BytesWritten;
                }
            }

            entry.Crc = crc.Value;
            entry.CompressedSize = _output.Position - dataStart;
            if (entry.UncompressedSize >= uint.MaxValue || entry.CompressedSize >= uint.MaxValue || entry.HeaderOffset >= uint.MaxValue)
                throw new InvalidOperationException("Zip entry too large (zip64 is not supported)");

            var end = _output.Position;
            _output.Position = entry.HeaderOffset + 14;
            var writer = new BinaryWriter(target);
            writer.Write(entry.Crc);
            writer.Write((uint)entry.CompressedSize);
            writer.Write((uint)entry.UncompressedSize);
            writer.Flush();
            _output.Position = end;

            _entries.Add(entry);
        }

        /// <summary>Writes the central directory. Call once after the last entry.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;

            var writer = new BinaryWriter(new NonClosingStream(_output));
            var centralStart = _output.Position;
            foreach (var entry in _entries)
            {
                writer.Write(CentralHeaderSignature);
                writer.Write((ushort)20); // made by: MS-DOS, spec 2.0
                writer.Write(VersionNeeded);
                writer.Write((ushort)0); // flags
                writer.Write(entry.Method);
                writer.Write(entry.DosTime);
                writer.Write(entry.DosDate);
                writer.Write(entry.Crc);
                writer.Write((uint)entry.CompressedSize);
                writer.Write((uint)entry.UncompressedSize);
                writer.Write((ushort)entry.NameBytes.Length);
                writer.Write((ushort)0); // extra
                writer.Write((ushort)0); // comment
                writer.Write((ushort)0); // disk number start
                writer.Write((ushort)0); // internal attributes
                writer.Write((uint)0); // external attributes
                writer.Write((uint)entry.HeaderOffset);
                writer.Write(entry.NameBytes);
            }

            var centralSize = _output.Position - centralStart;
            if (_entries.Count > ushort.MaxValue || centralStart >= uint.MaxValue)
                throw new InvalidOperationException("Zip too large (zip64 is not supported)");

            writer.Write(EndOfCentralDirectorySignature);
            writer.Write((ushort)0); // this disk
            writer.Write((ushort)0); // disk with central directory
            writer.Write((ushort)_entries.Count);
            writer.Write((ushort)_entries.Count);
            writer.Write((uint)centralSize);
            writer.Write((uint)centralStart);
            writer.Write((ushort)0); // comment length
            writer.Flush();
            _output.Flush();
        }

        public void Dispose()
        {
            // The caller owns the output stream.
        }

        private void WriteLocalHeader(Entry entry)
        {
            var writer = new BinaryWriter(new NonClosingStream(_output));
            writer.Write(LocalHeaderSignature);
            writer.Write(VersionNeeded);
            writer.Write((ushort)0); // flags
            writer.Write(entry.Method);
            writer.Write(entry.DosTime);
            writer.Write(entry.DosDate);
            writer.Write((uint)0); // crc (patched)
            writer.Write((uint)0); // compressed size (patched)
            writer.Write((uint)0); // uncompressed size (patched)
            writer.Write((ushort)entry.NameBytes.Length);
            writer.Write((ushort)0); // extra
            writer.Write(entry.NameBytes);
            writer.Flush();
        }

        private static void ToDosDateTime(DateTime value, out ushort dosTime, out ushort dosDate)
        {
            if (value.Year < 1980 || value.Year > 2107) value = new DateTime(1980, 1, 1);
            dosTime = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
            dosDate = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
        }

        /// <summary>Forwards writes but ignores Close/Dispose, so a compressor cannot close the zip file.</summary>
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _inner;
            public NonClosingStream(Stream inner) { _inner = inner; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override void WriteByte(byte value) => _inner.WriteByte(value);
            protected override void Dispose(bool disposing) { /* the zip writer owns the output */ }
        }

        /// <summary>Counts and CRCs the uncompressed bytes on their way into the compressor (or the file when stored).</summary>
        private sealed class CrcCountingStream : Stream
        {
            private readonly Stream _inner;
            private readonly Crc32 _crc;
            public long BytesWritten { get; private set; }
            public CrcCountingStream(Stream inner, Crc32 crc) { _inner = inner; _crc = crc; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                _crc.Update(buffer, offset, count);
                BytesWritten += count;
                _inner.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing) { /* the compressor is disposed by the zip writer */ }
        }
    }

    /// <summary>CRC-32 (IEEE 802.3, the one zip uses).</summary>
    public sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _state = 0xFFFFFFFFu;

        public uint Value => _state ^ 0xFFFFFFFFu;

        public void Update(byte[] buffer, int offset, int count)
        {
            var state = _state;
            for (var i = offset; i < offset + count; i++)
            {
                state = Table[(state ^ buffer[i]) & 0xFF] ^ (state >> 8);
            }

            _state = state;
        }

        public static uint Compute(byte[] buffer)
        {
            var crc = new Crc32();
            crc.Update(buffer, 0, buffer.Length);
            return crc.Value;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
