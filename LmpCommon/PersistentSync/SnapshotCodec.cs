using CachedQuickLz;
using System;

namespace LmpCommon.PersistentSync
{
    /// <summary>How a PersistentSync snapshot payload travels on the wire. The byte is the wire value.</summary>
    public static class SnapshotEncodings
    {
        /// <summary>The domain's payload as is.</summary>
        public const byte Raw = 0;

        /// <summary>QuickLZ (CachedQuickLz), the same codec vessels and scenarios already use.</summary>
        public const byte QuickLz = 1;

        /// <summary>Zstandard level 3.</summary>
        public const byte Zstd = 2;

        /// <summary>Zstandard with the client's previously received snapshot of the same domain as the dictionary, so a re-send costs about what changed.</summary>
        public const byte ZstdDictionary = 3;

        /// <summary>Capability bits a client reports in its snapshot requests (<see cref="Raw"/> is always understood).</summary>
        public const byte CapQuickLz = 1;
        public const byte CapZstd = 2;
        public const byte CapZstdDictionary = 4;

        public static string Name(byte encoding)
        {
            switch (encoding)
            {
                case Raw: return "raw";
                case QuickLz: return "quicklz";
                case Zstd: return "zstd";
                case ZstdDictionary: return "zstd+dict";
                default: return "unknown(" + encoding + ")";
            }
        }
    }

    /// <summary>A full raw snapshot payload kept so the next one can be sent or read relative to it.</summary>
    public sealed class SnapshotBase
    {
        public long Revision;

        /// <summary>Exactly the payload bytes (no spare capacity: the whole array is used as the dictionary).</summary>
        public byte[] Payload;

        public uint Checksum;

        public int NumBytes => Payload.Length;

        public static SnapshotBase Create(long revision, byte[] payload, int numBytes)
        {
            var copy = new byte[numBytes];
            Buffer.BlockCopy(payload, 0, copy, 0, numBytes);
            return new SnapshotBase { Revision = revision, Payload = copy, Checksum = SnapshotCodec.Checksum(copy, numBytes) };
        }
    }

    /// <summary>A snapshot payload as it goes on the wire.</summary>
    public sealed class EncodedSnapshot
    {
        public byte Encoding;

        /// <summary>The bytes to send; may be longer than <see cref="Length"/>.</summary>
        public byte[] Data;

        public int Length;
        public int RawLength;

        /// <summary>Only for <see cref="SnapshotEncodings.ZstdDictionary"/>: which snapshot the receiver must hold, and its checksum.</summary>
        public long BaseRevision;
        public uint BaseChecksum;
    }

    /// <summary>
    /// Encodes PersistentSync snapshot payloads (server) and decodes them (client).
    /// <para>
    /// Snapshots are text-like node trees that compress 5 to 10 times, and the server re-sends a domain's whole snapshot
    /// to a client after every small change (the Contracts snapshot is 131 KB raw). A re-send relative to the snapshot
    /// the client already received compresses to a few hundred bytes. The base is identified by revision and checksum, so a
    /// receiver that does not hold exactly that snapshot refuses the message and asks for a full one instead of applying
    /// anything wrong.
    /// </para>
    /// </summary>
    public static class SnapshotCodec
    {
        /// <summary>Payloads smaller than this go out raw, and are not kept as a base: the saving is not worth the work.</summary>
        public const int MinCompressBytes = 512;

        /// <summary>A decoded payload larger than this is treated as corrupt. The largest real snapshot is about 150 KB.</summary>
        public const int MaxRawBytes = 16 * 1024 * 1024;

        private static readonly object QuickLzLock = new object();

        /// <summary>Capability bits this process can decode (what a client reports to the server).</summary>
        public static byte LocalDecodeCapabilities
        {
            get
            {
                var caps = SnapshotEncodings.CapQuickLz;
                if (ZstdBackend.CanDecompress)
                {
                    caps |= SnapshotEncodings.CapZstd | SnapshotEncodings.CapZstdDictionary;
                }

                return caps;
            }
        }

        /// <summary>32 bit FNV-1a over the first <paramref name="length"/> bytes.</summary>
        public static uint Checksum(byte[] data, int length)
        {
            var hash = 2166136261u;
            for (var i = 0; i < length; i++)
            {
                hash = (hash ^ data[i]) * 16777619u;
            }

            return hash;
        }

        /// <summary>
        /// Picks the smallest encoding the receiver (<paramref name="receiverCapabilities"/>) understands. Never throws;
        /// anything that fails or does not shrink the payload falls back to raw.
        /// </summary>
        public static EncodedSnapshot Encode(byte[] raw, int numBytes, byte receiverCapabilities, SnapshotBase receiverBase)
        {
            var result = new EncodedSnapshot { Encoding = SnapshotEncodings.Raw, Data = raw, Length = numBytes, RawLength = numBytes };
            if (raw == null || numBytes < MinCompressBytes || receiverCapabilities == 0)
            {
                return result;
            }

            try
            {
                if ((receiverCapabilities & SnapshotEncodings.CapZstd) != 0 && ZstdBackend.CanCompress)
                {
                    TryZstd(raw, numBytes, receiverCapabilities, receiverBase, result);
                }
                else if ((receiverCapabilities & SnapshotEncodings.CapQuickLz) != 0)
                {
                    TryQuickLz(raw, numBytes, result);
                }
            }
            catch (Exception)
            {
                return new EncodedSnapshot { Encoding = SnapshotEncodings.Raw, Data = raw, Length = numBytes, RawLength = numBytes };
            }

            return result;
        }

        private static void TryZstd(byte[] raw, int numBytes, byte receiverCapabilities, SnapshotBase receiverBase, EncodedSnapshot result)
        {
            byte[] best = null;
            var bestLength = int.MaxValue;
            byte bestEncoding = SnapshotEncodings.Raw;

            if (receiverBase != null && (receiverCapabilities & SnapshotEncodings.CapZstdDictionary) != 0 && receiverBase.NumBytes >= MinCompressBytes &&
                ZstdBackend.TryCompress(raw, numBytes, receiverBase.Payload, out var withBase, out var withBaseLength))
            {
                best = withBase;
                bestLength = withBaseLength;
                bestEncoding = SnapshotEncodings.ZstdDictionary;
            }

            // A relative encoding that already shrank the payload by 4x or more is not worth a second attempt.
            if (best == null || bestLength * 4 > numBytes)
            {
                if (ZstdBackend.TryCompress(raw, numBytes, null, out var plain, out var plainLength) && plainLength < bestLength)
                {
                    best = plain;
                    bestLength = plainLength;
                    bestEncoding = SnapshotEncodings.Zstd;
                }
            }

            if (best == null || bestLength >= numBytes) return;

            result.Encoding = bestEncoding;
            result.Data = best;
            result.Length = bestLength;
            if (bestEncoding == SnapshotEncodings.ZstdDictionary)
            {
                result.BaseRevision = receiverBase.Revision;
                result.BaseChecksum = receiverBase.Checksum;
            }
        }

        private static void TryQuickLz(byte[] raw, int numBytes, EncodedSnapshot result)
        {
            // CachedQlz recycles the array it is given into its pool, so it must get a private copy.
            var copy = new byte[numBytes];
            Buffer.BlockCopy(raw, 0, copy, 0, numBytes);
            var length = numBytes;
            lock (QuickLzLock)
            {
                CachedQlz.Compress(ref copy, ref length);
            }

            if (length >= numBytes) return;

            result.Encoding = SnapshotEncodings.QuickLz;
            result.Data = copy;
            result.Length = length;
        }

        /// <summary>
        /// Restores the raw payload. Returns false (with a reason) for anything the receiver cannot reproduce exactly:
        /// an encoding it does not understand, a missing or different base, a corrupt frame or a wrong size.
        /// </summary>
        public static bool TryDecode(byte encoding, byte[] data, int length, int rawLength, long baseRevision, uint baseChecksum,
            SnapshotBase localBase, out byte[] raw, out string error)
        {
            raw = null;
            error = null;

            if (rawLength < 0 || rawLength > MaxRawBytes)
            {
                error = "implausible raw length " + rawLength;
                return false;
            }

            try
            {
                switch (encoding)
                {
                    case SnapshotEncodings.Raw:
                        raw = new byte[length];
                        Buffer.BlockCopy(data, 0, raw, 0, length);
                        return true;

                    case SnapshotEncodings.QuickLz:
                        return TryDecodeQuickLz(data, length, rawLength, out raw, out error);

                    case SnapshotEncodings.Zstd:
                        if (!ZstdBackend.TryDecompress(data, length, null, rawLength, out raw))
                        {
                            error = "zstd frame did not decode" + SuffixFor(ZstdBackend.Error);
                            return false;
                        }

                        return true;

                    case SnapshotEncodings.ZstdDictionary:
                        if (localBase == null)
                        {
                            error = "no base snapshot held (need revision " + baseRevision + ")";
                            return false;
                        }

                        if (localBase.Revision != baseRevision || localBase.Checksum != baseChecksum)
                        {
                            error = "base mismatch: sender used revision " + baseRevision + ", receiver holds " + localBase.Revision;
                            return false;
                        }

                        if (!ZstdBackend.TryDecompress(data, length, localBase.Payload, rawLength, out raw))
                        {
                            error = "zstd dictionary frame did not decode" + SuffixFor(ZstdBackend.Error);
                            return false;
                        }

                        return true;

                    default:
                        error = "unknown encoding " + encoding;
                        return false;
                }
            }
            catch (Exception e)
            {
                raw = null;
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static bool TryDecodeQuickLz(byte[] data, int length, int rawLength, out byte[] raw, out string error)
        {
            raw = null;
            error = null;

            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            int decoded;
            lock (QuickLzLock)
            {
                CachedQlz.Decompress(ref copy, out decoded);
            }

            if (decoded != rawLength)
            {
                error = "quicklz size mismatch: " + decoded + " != " + rawLength;
                return false;
            }

            raw = new byte[decoded];
            Buffer.BlockCopy(copy, 0, raw, 0, decoded);
            return true;
        }

        private static string SuffixFor(string backendError)
        {
            return string.IsNullOrEmpty(backendError) ? string.Empty : " (" + backendError + ")";
        }
    }
}
