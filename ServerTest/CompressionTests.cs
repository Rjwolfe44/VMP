using LmpCommon;
using LmpCommon.Compression;
using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ServerTest
{
    /// <summary>The message codec: zstd for payloads, QuickLZ and raw still readable, bounded decoding, the policy and the cache.</summary>
    [TestClass]
    public class CompressionTests
    {
        private static readonly object Gate = new object();

        private static CompressionConfig FixedServer(int level, int minSize = 160)
        {
            return new CompressionConfig { Mode = CompressionMode.Fixed, ServerLevel = level, MinSizeBytes = minSize };
        }

        /// <summary>Runs <paramref name="action"/> with the codec set up as a server (or client) and puts everything back afterwards.</summary>
        private static void With(CompressionSide side, ICompressionConfig config, Action action)
        {
            var oldSide = MessageCodec.Side;
            var oldConfig = MessageCodec.Config;
            MessageCodec.Policy.Reset();
            MessageCodec.Cache.Clear();
            try
            {
                MessageCodec.Configure(side, config);
                action();
            }
            finally
            {
                MessageCodec.Configure(oldSide, oldConfig);
                MessageCodec.Policy.Reset();
                MessageCodec.Cache.Clear();
            }
        }

        private static byte[] Compressible(int size, int seed)
        {
            // Looks like a vessel file: many similar part blocks with a few changing numbers.
            var random = new Random(seed);
            var builder = new System.Text.StringBuilder();
            while (builder.Length < size)
            {
                builder.Append("PART\n{\n\tpart = fuelTank_").Append(random.Next(0, 40)).Append("\n\tpos = ").Append(random.Next(-9, 9)).Append(",")
                    .Append(random.Next(-9, 9)).Append(",0\n\tattN = top, Null\n\tmass = ").Append(random.Next(1, 90) / 10.0).Append("\n\tMODULE\n\t{\n\t\tname = ModuleEngines\n\t}\n}\n");
            }

            return System.Text.Encoding.ASCII.GetBytes(builder.ToString()).Take(size).ToArray();
        }

        private static byte[] RandomBytes(int size, int seed)
        {
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            if (size > 0) bytes[0] = 0x41; // never looks like a compressed payload by accident
            return bytes;
        }

        private static byte[] Prefix(byte[] data, int length, int spare = 0)
        {
            var copy = new byte[length + spare];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return copy;
        }

        private static byte[] Decode(byte[] payload, int length, out int decodedLength, string name = "FuzzMsgData")
        {
            var buffer = Prefix(payload, length, 7); // message buffers are reused and longer than the payload
            Common.ThreadSafeDecompress(Gate, ref buffer, length, out decodedLength, name);
            return buffer;
        }

        private static void AssertSame(byte[] expected, byte[] actual, int actualLength, string what)
        {
            Assert.AreEqual(expected.Length, actualLength, what + ": length");
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i]) Assert.Fail(what + ": byte " + i + " differs");
            }
        }

        #region Round trips

        [TestMethod]
        public void ZstdCanCompressAndDecompressInThisProcess()
        {
            Assert.IsTrue(ZstdBackend.CanCompress, ZstdBackend.Error);
            Assert.IsTrue(ZstdBackend.CanDecompress, ZstdBackend.Error);
        }

        [TestMethod]
        public void RoundTrip_EveryLevelAndSize_RandomAndCompressible()
        {
            var sizes = new[] { 0, 1, 100, 159, 160, 161, 1000, 4096, 65535, 65536, 200000 };
            var levels = new[] { 1, 3, 5, 9, 15, 19 };
            foreach (var level in levels)
            {
                With(CompressionSide.Server, FixedServer(level), () =>
                {
                    foreach (var size in sizes)
                    {
                        foreach (var compressible in new[] { true, false })
                        {
                            var original = compressible ? Compressible(size, size + level) : RandomBytes(size, size + level);
                            var data = Prefix(original, size, 5);
                            var count = size;
                            Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");

                            var what = $"level {level}, {size} bytes, {(compressible ? "text" : "random")}";
                            if (size < 160 || !compressible)
                            {
                                Assert.AreEqual(size, count, what + ": too small or incompressible payloads go out raw");
                                Assert.IsFalse(ZstdBackend.IsFrame(data, count), what);
                            }
                            else
                            {
                                Assert.IsTrue(count < size - CompressionLimits.MinGainBytes, what + ": " + count);
                                Assert.IsTrue(ZstdBackend.IsFrame(data, count), what + ": zstd frame");
                            }

                            int decoded;
                            var back = Decode(data, count, out decoded);
                            AssertSame(original, back, decoded, what);
                        }
                    }
                });
            }
        }

        [TestMethod]
        public void RoundTrip_RandomSizesUpToTwoMegabytes()
        {
            var random = new Random(20261023);
            With(CompressionSide.Server, new CompressionConfig { Mode = CompressionMode.Adaptive }, () =>
            {
                for (var i = 0; i < 40; i++)
                {
                    var size = (int)Math.Min(2 * 1024 * 1024, Math.Exp(random.NextDouble() * Math.Log(2 * 1024 * 1024)));
                    var original = i % 3 == 0 ? RandomBytes(size, i) : Compressible(size, i);
                    var data = (byte[])original.Clone();
                    var count = size;
                    Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                    int decoded;
                    AssertSame(original, Decode(data, count, out decoded), decoded, "payload " + i + " of " + size + " bytes");
                }
            });
        }

        [TestMethod]
        public void RoundTrip_TwoMegabytesAtTheLevelsTheServerUsesForBigPayloads()
        {
            foreach (var level in new[] { 1, 3, 5 })
            {
                With(CompressionSide.Server, FixedServer(level), () =>
                {
                    var original = Compressible(2 * 1024 * 1024, level);
                    var data = (byte[])original.Clone();
                    var count = original.Length;
                    Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                    Assert.IsTrue(count < original.Length / 3, "level " + level + ": " + count);
                    int decoded;
                    AssertSame(original, Decode(data, count, out decoded), decoded, "level " + level);
                });
            }
        }

        [TestMethod]
        public void CompressingAnAlreadyCompressedPayloadChangesNothing()
        {
            With(CompressionSide.Server, FixedServer(9), () =>
            {
                var original = Compressible(5000, 1);
                var data = (byte[])original.Clone();
                var count = original.Length;
                Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                var once = data;
                var onceCount = count;
                Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                Assert.AreSame(once, data, "a message serialized once per recipient is compressed once");
                Assert.AreEqual(onceCount, count);
            });
        }

        [TestMethod]
        public void PayloadsBelowTheMinimumSizeOrWithoutGainAreSentRaw()
        {
            With(CompressionSide.Server, FixedServer(9, minSize: 1000), () =>
            {
                var small = Compressible(999, 3);
                var data = (byte[])small.Clone();
                var count = small.Length;
                Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                Assert.AreEqual(999, count);
                CollectionAssert.AreEqual(small, data);

                var exactlyMinimum = Compressible(1000, 3);
                data = (byte[])exactlyMinimum.Clone();
                count = 1000;
                Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                Assert.IsTrue(count < 1000, "a payload of exactly the minimum size is compressed");
            });

            With(CompressionSide.Server, FixedServer(9), () =>
            {
                var noise = RandomBytes(5000, 4);
                var data = (byte[])noise.Clone();
                var count = noise.Length;
                Common.ThreadSafeCompress(Gate, ref data, ref count, "FuzzMsgData");
                Assert.AreEqual(5000, count, "random bytes do not shrink by 8 bytes, so they go out as they are");
                CollectionAssert.AreEqual(noise, data);
            });
        }

        #endregion

        #region Mixed decoding

        [TestMethod]
        public void MixedDecode_ZstdQuickLzAndRawAreToldApartByTheirFirstBytes()
        {
            var original = Compressible(30000, 5);

            byte[] zstd;
            int zstdLength;
            Assert.IsTrue(ZstdBackend.TryCompressAtLevel(original, original.Length, 5, out zstd, out zstdLength));
            Assert.IsTrue(ZstdBackend.IsFrame(zstd, zstdLength));

            byte[] quickLz;
            int quickLzLength;
            Assert.IsTrue(MessageCodec.TryQuickLzCompress(original, original.Length, out quickLz, out quickLzLength));
            Assert.IsTrue(quickLzLength < original.Length);
            Assert.IsFalse(ZstdBackend.IsFrame(quickLz, quickLzLength));
            Assert.IsTrue(MessageCodec.IsCompressedPayload(quickLz, quickLzLength));

            int decoded;
            AssertSame(original, Decode(zstd, zstdLength, out decoded), decoded, "zstd");
            AssertSame(original, Decode(quickLz, quickLzLength, out decoded), decoded, "quicklz");
            AssertSame(original, Decode(original, original.Length, out decoded), decoded, "raw");

            Assert.AreEqual(0, Decode(new byte[0], 0, out decoded).Length - 7);
            Assert.AreEqual(0, decoded, "an empty payload stays empty");
        }

        [TestMethod]
        public void MixedDecode_IsCountedUnderTheMessageTypeWithTheRightCodec()
        {
            var name = "MixedStats" + Guid.NewGuid().ToString("N");
            var original = Compressible(30000, 6);
            byte[] zstd;
            int zstdLength;
            ZstdBackend.TryCompressAtLevel(original, original.Length, 5, out zstd, out zstdLength);
            byte[] quickLz;
            int quickLzLength;
            MessageCodec.TryQuickLzCompress(original, original.Length, out quickLz, out quickLzLength);

            int decoded;
            Decode(zstd, zstdLength, out decoded, name);
            Decode(quickLz, quickLzLength, out decoded, name);
            Decode(original, original.Length, out decoded, name);

            var row = VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == name);
            Assert.AreEqual(2, row.Messages, "the raw payload is not a compression");
            Assert.AreEqual(60000, row.RawBytes);
            Assert.AreEqual(zstdLength + quickLzLength, row.WireBytes);
            Assert.AreEqual("zstd+quicklz", row.Codec);
            Assert.AreEqual(0.0, row.AvgLevel, "decodings have no level");
        }

        #endregion

        #region Corrupt, truncated and hostile frames

        private static byte[] Frame(byte[] original, int level = 5)
        {
            byte[] buffer;
            int length;
            Assert.IsTrue(ZstdBackend.TryCompressAtLevel(original, original.Length, level, out buffer, out length));
            return Prefix(buffer, length);
        }

        private static void AssertBadPayload(byte[] payload, string what, string messagePart = null)
        {
            var buffer = Prefix(payload, payload.Length);
            try
            {
                int decoded;
                Common.ThreadSafeDecompress(Gate, ref buffer, payload.Length, out decoded);
            }
            catch (InvalidOperationException e)
            {
                if (messagePart != null) StringAssert.Contains(e.Message, messagePart, what);
                return;
            }

            Assert.Fail(what + ": expected an InvalidOperationException");
        }

        [TestMethod]
        public void TruncatedFramesAreACleanError()
        {
            var frame = Frame(Compressible(20000, 7));
            foreach (var cut in new[] { 4, 5, 6, 9, 20, frame.Length / 2, frame.Length - 1 })
            {
                AssertBadPayload(frame.Take(cut).ToArray(), "frame cut to " + cut + " bytes");
            }
        }

        [TestMethod]
        public void CorruptedFramesNeverEndInAnythingButACleanErrorOrACompleteResult()
        {
            var original = Compressible(20000, 8);
            var frame = Frame(original);
            var random = new Random(77);
            var errors = 0;
            for (var i = 0; i < 300; i++)
            {
                var broken = (byte[])frame.Clone();
                broken[random.Next(0, broken.Length)] ^= (byte)(1 << random.Next(0, 8));
                var buffer = Prefix(broken, broken.Length);
                try
                {
                    int decoded;
                    Common.ThreadSafeDecompress(Gate, ref buffer, broken.Length, out decoded);
                    Assert.IsTrue(decoded >= 0 && decoded <= CompressionLimits.MaxDecodedBytes);
                }
                catch (InvalidOperationException)
                {
                    errors++;
                }
            }

            Assert.IsTrue(errors > 30, "most flipped bits are noticed (" + errors + ")");

            var noise = new byte[2000];
            new Random(5).NextBytes(noise);
            noise[0] = 0x28; noise[1] = 0xB5; noise[2] = 0x2F; noise[3] = 0xFD;
            AssertBadPayload(noise, "random bytes after the magic");
            AssertBadPayload(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "the magic alone");
            AssertBadPayload(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0xFF }, "reserved bits set");
        }

        private static byte[] Header(byte descriptor, params byte[] rest)
        {
            return new byte[] { 0x28, 0xB5, 0x2F, 0xFD, descriptor }.Concat(rest).ToArray();
        }

        [TestMethod]
        public void ADeclaredSizeAboveTheLimitIsRefusedBeforeAnythingIsAllocated()
        {
            // Single segment, 8 byte content size = 64 MB + 1, then junk.
            var size = BitConverter.GetBytes((long)CompressionLimits.MaxDecodedBytes + 1);
            var payload = Header(0xE0, size).Concat(new byte[40]).ToArray();

            var before = GC.GetAllocatedBytesForCurrentThread();
            AssertBadPayload(payload, "64 MB + 1", "above the limit");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(allocated < 1024 * 1024, "allocated " + allocated);

            // The largest possible size, too.
            AssertBadPayload(Header(0xE0, BitConverter.GetBytes(long.MaxValue)).Concat(new byte[40]).ToArray(), "long.MaxValue", "above the limit");
            AssertBadPayload(Header(0xE0, BitConverter.GetBytes(ulong.MaxValue)).Concat(new byte[40]).ToArray(), "ulong.MaxValue", "above the limit");
        }

        [TestMethod]
        public void ADeclaredSizeAtTheLimitWithTinyJunkDoesNotAllocateTheDeclaredSize()
        {
            var size = BitConverter.GetBytes((long)CompressionLimits.MaxDecodedBytes);
            var payload = Header(0xE0, size).Concat(new byte[40]).ToArray();
            var before = GC.GetAllocatedBytesForCurrentThread();
            AssertBadPayload(payload, "64 MB declared, junk follows");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(allocated < 8 * 1024 * 1024, "allocated " + allocated + " bytes for a 40 byte frame");
        }

        [TestMethod]
        public void ALegitimateFrameThatExpandsALotIsDecodedWithoutAskingForTheWholeBufferUpFront()
        {
            var zeros = new byte[8 * 1024 * 1024];
            var frame = Frame(zeros, 1);
            Assert.IsTrue(frame.Length < 1000, "frame " + frame.Length);
            int decoded;
            var back = Decode(frame, frame.Length, out decoded);
            Assert.AreEqual(zeros.Length, decoded);
            Assert.IsTrue(back.Take(decoded).All(b => b == 0));
        }

        [TestMethod]
        public void AFrameOfExactlyTheLimitIsAccepted()
        {
            var zeros = new byte[CompressionLimits.MaxDecodedBytes];
            var frame = Frame(zeros, 1);
            int decoded;
            Decode(frame, frame.Length, out decoded);
            Assert.AreEqual(CompressionLimits.MaxDecodedBytes, decoded);
        }

        [TestMethod]
        public void AFrameThatNeedsADictionaryIsACleanErrorNamingIt()
        {
            // Single segment, dictionary id in one byte (7), content size in one byte (5).
            AssertBadPayload(Header(0x21, 7, 5).Concat(new byte[20]).ToArray(), "dictionary frame", "dictionary 7");
        }

        [TestMethod]
        public void TheFrameHeaderIsReadWithoutDecoding()
        {
            var original = Compressible(70000, 9);
            var frame = Frame(original);
            long size;
            long dictionary;
            Assert.IsTrue(ZstdBackend.TryReadFrameHeader(frame, frame.Length, out size, out dictionary));
            Assert.AreEqual(70000, size, "the compressor writes the content size");
            Assert.AreEqual(0, dictionary);

            Assert.IsFalse(ZstdBackend.TryReadFrameHeader(frame, 3, out _, out _));
            Assert.IsFalse(ZstdBackend.TryReadFrameHeader(new byte[10], 10, out _, out _));
        }

        /// <summary>Hand-made frame without a content size: raw block(s) of the given bytes.</summary>
        private static byte[] UnknownSizeFrame(params byte[][] rawBlocks)
        {
            var bytes = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x00 }; // no content size, not single segment, window descriptor 0
            for (var i = 0; i < rawBlocks.Length; i++)
            {
                var header = (uint)(rawBlocks[i].Length << 3) | (i == rawBlocks.Length - 1 ? 1u : 0u); // raw block
                bytes.Add((byte)header);
                bytes.Add((byte)(header >> 8));
                bytes.Add((byte)(header >> 16));
                bytes.AddRange(rawBlocks[i]);
            }

            return bytes.ToArray();
        }

        [TestMethod]
        public void AFrameWithoutAContentSizeIsDecodedAndBounded()
        {
            var block = Compressible(5000, 10);
            var frame = UnknownSizeFrame(block, block);
            long size;
            long dictionary;
            Assert.IsTrue(ZstdBackend.TryReadFrameHeader(frame, frame.Length, out size, out dictionary));
            Assert.AreEqual(-1, size);

            int decoded;
            var back = Decode(frame, frame.Length, out decoded);
            AssertSame(block.Concat(block).ToArray(), back, decoded, "unknown size frame");

            // 600 run-length blocks of 128 KB each: 4 bytes on the wire per block, 78 MB decoded, no size declared.
            var bytes = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x00 };
            for (var i = 0; i < 600; i++)
            {
                var header = (uint)(131072 << 3) | (1u << 1) | (i == 599 ? 1u : 0u); // RLE block
                bytes.Add((byte)header);
                bytes.Add((byte)(header >> 8));
                bytes.Add((byte)(header >> 16));
                bytes.Add(0x41);
            }

            AssertBadPayload(bytes.ToArray(), "a frame that expands past the limit without declaring it", "limit");
        }

        [TestMethod]
        public void AnImpossibleLengthIsACleanError()
        {
            var buffer = new byte[10];
            Assert.ThrowsException<InvalidOperationException>(() => Common.ThreadSafeDecompress(Gate, ref buffer, 11, out _));
            Assert.ThrowsException<InvalidOperationException>(() => Common.ThreadSafeDecompress(Gate, ref buffer, -1, out _));
        }

        [TestMethod]
        public void ACorruptQuickLzPayloadIsACleanError()
        {
            var original = Compressible(20000, 11);
            byte[] quickLz;
            int length;
            Assert.IsTrue(MessageCodec.TryQuickLzCompress(original, original.Length, out quickLz, out length));

            // Declares 1 GB.
            var hostile = Prefix(quickLz, length);
            Assert.IsTrue((hostile[0] & 2) != 0, "a payload of this size has the long header");
            BitConverter.GetBytes(1_000_000_000).CopyTo(hostile, 5);
            AssertBadPayload(hostile, "QuickLZ declaring 1 GB", "limit");

            // Cut short: either a clean error or a (wrong but bounded) result, never another exception type.
            var cut = Prefix(quickLz, length / 2);
            try
            {
                int decoded;
                var buffer = Prefix(cut, cut.Length);
                Common.ThreadSafeDecompress(Gate, ref buffer, cut.Length, out decoded);
            }
            catch (InvalidOperationException)
            {
            }
        }

        #endregion

        #region Concurrency

        [TestMethod]
        public void ManyThreadsCompressAndDecompressAtTheSameTime()
        {
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            With(CompressionSide.Server, new CompressionConfig { Mode = CompressionMode.Adaptive }, () =>
            {
                Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, thread =>
                {
                    var random = new Random(thread);
                    for (var i = 0; i < 60; i++)
                    {
                        try
                        {
                            var size = random.Next(100, 60000);
                            var original = random.Next(4) == 0 ? RandomBytes(size, thread * 1000 + i) : Compressible(size, thread % 5);
                            var data = (byte[])original.Clone();
                            var count = size;
                            var gate = new object();
                            Common.ThreadSafeCompress(gate, ref data, ref count, "Concurrent" + (thread % 3));
                            var buffer = Prefix(data, count, 3);
                            int decoded;
                            Common.ThreadSafeDecompress(gate, ref buffer, count, out decoded, "Concurrent" + (thread % 3));
                            if (decoded != original.Length || !original.SequenceEqual(buffer.Take(decoded))) failures.Add("thread " + thread + " item " + i + ": wrong content");
                        }
                        catch (Exception e)
                        {
                            failures.Add("thread " + thread + " item " + i + ": " + e.Message);
                        }
                    }
                });
            });

            Assert.AreEqual(0, failures.Count, string.Join("\n", failures.Take(5)));
        }

        #endregion

        #region Policy (fake timings)

        private sealed class FakeClock
        {
            public long Ms;
            public Func<long> Now => () => Ms;
        }

        private static CompressionConfig Adaptive(int serverLevel = 9, int clientLevel = 5, int maxAdaptive = 15, int minSize = 160)
        {
            return new CompressionConfig { Mode = CompressionMode.Adaptive, ServerLevel = serverLevel, ClientLevel = clientLevel, MaxAdaptiveLevel = maxAdaptive, MinSizeBytes = minSize };
        }

        [TestMethod]
        public void Policy_BelowTheMinimumSizeNothingIsCompressed()
        {
            var policy = new CompressionPolicy();
            var config = Adaptive(minSize: 160);
            Assert.AreEqual(0, policy.ChooseLevel(CompressionSide.Server, 0, "VesselProtoMsgData", config));
            Assert.AreEqual(0, policy.ChooseLevel(CompressionSide.Server, 159, "VesselProtoMsgData", config));
            Assert.IsTrue(policy.ChooseLevel(CompressionSide.Server, 160, "VesselProtoMsgData", config) > 0);
            Assert.AreEqual(0, policy.ChooseLevel(CompressionSide.Client, 159, "VesselProtoMsgData", config));
            Assert.AreEqual(0, policy.ChooseLevel(CompressionSide.Server, 1000, "x", Adaptive(minSize: 5000)));
            Assert.IsTrue(policy.ChooseLevel(CompressionSide.Server, 1, "x", Adaptive(minSize: 0)) > 0, "a minimum of 0 compresses everything");
        }

        [TestMethod]
        public void Policy_FixedModeUsesTheServerLevelEverywhere_AndClampsSillyValues()
        {
            var policy = new CompressionPolicy();
            var config = new CompressionConfig { Mode = CompressionMode.Fixed, ServerLevel = 12 };
            foreach (var size in new[] { 200, 5000, 100000, 3000000, 20000000 })
            {
                Assert.AreEqual(12, policy.ChooseLevel(CompressionSide.Server, size, "x", config), "size " + size);
            }

            config.ServerLevel = 99;
            Assert.AreEqual(19, policy.ChooseLevel(CompressionSide.Server, 5000, "x", config));
            config.ServerLevel = -4;
            Assert.AreEqual(1, policy.ChooseLevel(CompressionSide.Server, 5000, "x", config));
        }

        [TestMethod]
        public void Policy_AdaptiveServerTable_SmallPayloadsGetMoreEffortBigOnesLess()
        {
            var policy = new CompressionPolicy();
            var config = Adaptive();
            Func<int, int> level = size => policy.ChooseLevel(CompressionSide.Server, size, "x", config);
            Assert.AreEqual(15, level(4 * 1024));
            Assert.AreEqual(15, level(50 * 1024));
            Assert.AreEqual(15, level(256 * 1024));
            Assert.AreEqual(9, level(256 * 1024 + 1));
            Assert.AreEqual(9, level(800 * 1024));
            Assert.AreEqual(4, level(3 * 1024 * 1024));
            Assert.AreEqual(1, level(10 * 1024 * 1024));

            var capped = Adaptive(maxAdaptive: 10);
            Assert.AreEqual(10, policy.ChooseLevel(CompressionSide.Server, 4 * 1024, "x", capped), "never above MaxAdaptiveLevel");
            Assert.AreEqual(9, policy.ChooseLevel(CompressionSide.Server, 800 * 1024, "x", capped), "a cap above the table does not matter");
            var high = Adaptive(serverLevel: 19, maxAdaptive: 19);
            Assert.AreEqual(19, policy.ChooseLevel(CompressionSide.Server, 4 * 1024, "x", high));
            var low = Adaptive(serverLevel: 1);
            Assert.AreEqual(1, policy.ChooseLevel(CompressionSide.Server, 10 * 1024 * 1024, "x", low), "never below 1");
        }

        [TestMethod]
        public void Policy_AdaptiveClientTable_StaysUnderTheClientLevel()
        {
            var policy = new CompressionPolicy();
            var config = Adaptive(clientLevel: 5);
            Func<int, int> level = size => policy.ChooseLevel(CompressionSide.Client, size, "x", config);
            Assert.AreEqual(5, level(4 * 1024));
            Assert.AreEqual(4, level(50 * 1024));
            Assert.AreEqual(3, level(200 * 1024));
            Assert.AreEqual(2, level(800 * 1024));
            Assert.AreEqual(1, level(3 * 1024 * 1024));
            Assert.AreEqual(1, level(10 * 1024 * 1024));
        }

        [TestMethod]
        public void Policy_ClientIsBoundedWhateverTheServerSaysAndWhateverTheMode()
        {
            var policy = new CompressionPolicy();
            foreach (var mode in new[] { CompressionMode.Adaptive, CompressionMode.Fixed })
            {
                foreach (var hostile in new[] { 9, 10, 19, 99, int.MaxValue })
                {
                    var config = new CompressionConfig { Mode = mode, ClientLevel = hostile, ServerLevel = hostile, MaxAdaptiveLevel = hostile };
                    foreach (var size in new[] { 200, 4096, 70000, 1000000, 4 * 1024 * 1024, 4 * 1024 * 1024 + 1, 30000000 })
                    {
                        var level = policy.ChooseLevel(CompressionSide.Client, size, "x", config);
                        Assert.IsTrue(level >= 1 && level <= 9, $"{mode} {hostile} {size}: {level}");
                        if (size > CompressionLimits.LargePayloadBytes) Assert.IsTrue(level <= 3, $"{mode} {hostile} {size}: {level} is above 3 for a payload over 4 MB");
                    }
                }
            }

            var low = new CompressionConfig { ClientLevel = int.MinValue };
            Assert.AreEqual(1, policy.ChooseLevel(CompressionSide.Client, 4096, "x", low));
        }

        [TestMethod]
        public void Policy_SlowCompressionsLowerTheLevel_AtMostOncePerFiveSeconds()
        {
            var clock = new FakeClock();
            var policy = new CompressionPolicy(clock.Now);
            var config = Adaptive();
            Func<int> level = () => policy.ChooseLevel(CompressionSide.Server, 500 * 1024, "x", config);
            Assert.AreEqual(9, level());

            // Two slow samples are not enough, the third (average above 25 ms) lowers the level by one.
            policy.Report(CompressionSide.Server, 9, 50 * 1024, 40000);
            policy.Report(CompressionSide.Server, 9, 50 * 1024, 40000);
            Assert.AreEqual(0, policy.Adjustment(CompressionSide.Server));
            policy.Report(CompressionSide.Server, 9, 50 * 1024, 40000);
            Assert.AreEqual(-1, policy.Adjustment(CompressionSide.Server));
            Assert.AreEqual(8, level());

            // Hysteresis: more slow samples inside five seconds change nothing, after five seconds one more step.
            clock.Ms += 4000;
            policy.Report(CompressionSide.Server, 8, 50 * 1024, 40000);
            Assert.AreEqual(-1, policy.Adjustment(CompressionSide.Server));
            clock.Ms += 1000;
            policy.Report(CompressionSide.Server, 8, 50 * 1024, 40000);
            Assert.AreEqual(-2, policy.Adjustment(CompressionSide.Server));
            Assert.AreEqual(7, level());

            // It never goes below level 1.
            for (var i = 0; i < 40; i++)
            {
                clock.Ms += 5000;
                policy.Report(CompressionSide.Server, 1, 50 * 1024, 90000);
            }

            Assert.AreEqual(1, level());
            Assert.AreEqual(CompressionPolicy.MinAdjustment, policy.Adjustment(CompressionSide.Server));
        }

        [TestMethod]
        public void Policy_FastCompressionsRaiseTheLevelAgain_OnlyWhileBelowTheCap()
        {
            var clock = new FakeClock();
            var policy = new CompressionPolicy(clock.Now);
            var config = Adaptive(maxAdaptive: 11);
            Func<int> level = () => policy.ChooseLevel(CompressionSide.Server, 500 * 1024, "x", config);

            for (var i = 0; i < 3; i++) policy.Report(CompressionSide.Server, 9, 50 * 1024, 60000);
            Assert.AreEqual(8, level());
            clock.Ms += 5000;
            policy.Report(CompressionSide.Server, 8, 50 * 1024, 60000);
            Assert.AreEqual(7, level());

            // Fast again (the average has to fall under a third of the budget first).
            for (var i = 0; i < 30; i++)
            {
                clock.Ms += 5000;
                policy.Report(CompressionSide.Server, level(), 50 * 1024, 1000);
            }

            Assert.AreEqual(11, level(), "raised step by step up to the cap");
            var adjustment = policy.Adjustment(CompressionSide.Server);
            for (var i = 0; i < 10; i++)
            {
                clock.Ms += 5000;
                policy.Report(CompressionSide.Server, level(), 50 * 1024, 1000);
            }

            Assert.AreEqual(adjustment, policy.Adjustment(CompressionSide.Server), "at the cap the adjustment does not wind up");
            Assert.IsTrue(policy.Adjustment(CompressionSide.Server) <= CompressionPolicy.MaxAdjustment);
        }

        [TestMethod]
        public void Policy_ClientUsesItsOwnBudgetAndItsOwnState()
        {
            var clock = new FakeClock();
            var policy = new CompressionPolicy(clock.Now);
            var config = Adaptive(clientLevel: 5);
            for (var i = 0; i < 3; i++) policy.Report(CompressionSide.Client, 4, 10 * 1024, 4000);
            Assert.AreEqual(-1, policy.Adjustment(CompressionSide.Client), "4 ms is over the client budget of 3 ms");
            Assert.AreEqual(0, policy.Adjustment(CompressionSide.Server), "and 4 ms is nothing for the server");
            Assert.AreEqual(4, policy.ChooseLevel(CompressionSide.Client, 4096, "x", config));
        }

        [TestMethod]
        public void Policy_MeasuredSpeedOfALevelLowersItForPayloadsThatWouldTakeTooLong()
        {
            var policy = new CompressionPolicy(new FakeClock().Now);
            var config = Adaptive();
            // 100 KB at level 15 took 40 ms: 400 microseconds per KB.
            policy.Report(CompressionSide.Server, 15, 100 * 1024, 40000);
            Assert.AreEqual(400.0, policy.MicrosPerKbAtLevel(CompressionSide.Server, 15), 0.001);
            Assert.AreEqual(14, policy.ChooseLevel(CompressionSide.Server, 100 * 1024, "x", config), "level 15 would take 40 ms, over the 25 ms budget");
            Assert.AreEqual(15, policy.ChooseLevel(CompressionSide.Server, 30 * 1024, "x", config), "a small payload at level 15 is still within the budget (12 ms)");
            Assert.IsTrue(policy.AvgMicrosPerKb(CompressionSide.Server) > 0);
        }

        [TestMethod]
        public void Policy_BigPayloadsDoNotFeedThePerMessageAverage()
        {
            var clock = new FakeClock();
            var policy = new CompressionPolicy(clock.Now);
            for (var i = 0; i < 10; i++)
            {
                clock.Ms += 5000;
                policy.Report(CompressionSide.Server, 3, 3 * 1024 * 1024, 400000);
            }

            Assert.AreEqual(0, policy.Adjustment(CompressionSide.Server));
            Assert.IsTrue(policy.MicrosPerKbAtLevel(CompressionSide.Server, 3) > 0, "but the per-level speed is still measured");
        }

        [TestMethod]
        public void Policy_IgnoresNonsenseReports_AndEffectiveLevelFollowsTheFeedback()
        {
            var clock = new FakeClock();
            var policy = new CompressionPolicy(clock.Now);
            var config = Adaptive();
            policy.Report(CompressionSide.Server, 0, 1000, 1);
            policy.Report(CompressionSide.Server, 99, 1000, 1);
            policy.Report(CompressionSide.Server, 5, 0, 1);
            policy.Report(CompressionSide.Server, 5, 1000, -1);
            policy.Report(CompressionSide.Server, 5, 1000, double.NaN);
            Assert.AreEqual(0.0, policy.AvgMicrosPerKb(CompressionSide.Server));

            Assert.AreEqual(15, policy.EffectiveLevelNow(CompressionSide.Server, config));
            for (var i = 0; i < 3; i++) policy.Report(CompressionSide.Server, 9, 30 * 1024, 90000);
            Assert.AreEqual(14, policy.EffectiveLevelNow(CompressionSide.Server, config));
            Assert.AreEqual(12, policy.EffectiveLevelNow(CompressionSide.Server, new CompressionConfig { Mode = CompressionMode.Fixed, ServerLevel = 12 }));

            policy.Reset();
            Assert.AreEqual(15, policy.EffectiveLevelNow(CompressionSide.Server, config));
        }

        #endregion

        #region Client side bounds

        [TestMethod]
        public void ClientConfig_TakesTheServersValuesButBoundsThem()
        {
            var config = new CompressionConfig();
            config.ApplyFromServer(7, 300);
            Assert.AreEqual(7, config.ClientLevel);
            Assert.AreEqual(300, config.MinSizeBytes);

            config.ApplyFromServer(int.MaxValue, int.MaxValue);
            Assert.AreEqual(9, config.ClientLevel, "never above level 9");
            Assert.AreEqual(65536, config.MinSizeBytes);

            config.ApplyFromServer(1, 0);
            Assert.AreEqual(1, config.ClientLevel);
            Assert.AreEqual(0, config.MinSizeBytes);

            // A server that sent nothing (older server): the safe defaults.
            config.ApplyFromServer(0, -1);
            Assert.AreEqual(5, config.ClientLevel);
            Assert.AreEqual(160, config.MinSizeBytes);
            config.ApplyFromServer(int.MinValue, int.MinValue);
            Assert.AreEqual(5, config.ClientLevel);
            Assert.AreEqual(160, config.MinSizeBytes);
        }

        [TestMethod]
        public void AClientFollowingAHostileServerNeverCompressesAboveLevelNineOrThreeForHugePayloads()
        {
            var name = "HostileServer" + Guid.NewGuid().ToString("N");
            var config = new CompressionConfig();
            config.ApplyFromServer(int.MaxValue, 0);
            With(CompressionSide.Client, config, () =>
            {
                var big = Compressible(5 * 1024 * 1024, 12);
                var count = big.Length;
                Common.ThreadSafeCompress(Gate, ref big, ref count, name + "Big");
                var small = Compressible(8000, 12);
                var smallCount = small.Length;
                Common.ThreadSafeCompress(Gate, ref small, ref smallCount, name + "Small");

                var rows = VmpTrafficStats.GetMessageCompressionTotals();
                Assert.IsTrue(rows.Single(r => r.Type == name + "Big").AvgLevel <= 3, "over 4 MB: level 3 at most");
                Assert.IsTrue(rows.Single(r => r.Type == name + "Small").AvgLevel <= 9);
            });
        }

        [TestMethod]
        public void AClientThatCannotCompressWithZstdFallsBackToQuickLz_AndTheServerStillReadsIt()
        {
            // Simulated by a QuickLZ payload: what a KSP client with the old Unsafe.dll sends.
            var original = Compressible(40000, 13);
            byte[] quickLz;
            int length;
            Assert.IsTrue(MessageCodec.TryQuickLzCompress(original, original.Length, out quickLz, out length));
            With(CompressionSide.Server, Adaptive(), () =>
            {
                int decoded;
                AssertSame(original, Decode(quickLz, length, out decoded), decoded, "quicklz from a client");
            });
        }

        #endregion

        #region Admin API shape

        [TestMethod]
        public void TheCompressionBlockOfTheNetworkApiHasTheDocumentedShape()
        {
            var config = new CompressionConfig { Mode = CompressionMode.Fixed, ServerLevel = 11, ClientLevel = 4 };
            var info = new Server.Admin.NetworkInfo { Compression = Server.Admin.AdminApiMapper.ToCompression(config, 11, 1840, 1020, 21.46) };
            var json = System.Text.Json.JsonDocument.Parse(Server.Admin.AdminApiResponse.Ok(info).Json).RootElement.GetProperty("compression");

            Assert.AreEqual("Fixed", json.GetProperty("mode").GetString());
            Assert.AreEqual(11, json.GetProperty("serverLevel").GetInt32());
            Assert.AreEqual(11, json.GetProperty("effectiveLevelNow").GetInt32());
            Assert.AreEqual(4, json.GetProperty("clientLevel").GetInt32());
            Assert.AreEqual(1840, json.GetProperty("cacheHits").GetInt64());
            Assert.AreEqual(1020, json.GetProperty("cacheMisses").GetInt64());
            Assert.AreEqual(21.5, json.GetProperty("avgCompressMicrosPerKb").GetDouble(), 0.0001);
            Assert.AreEqual(7, json.EnumerateObject().Count(), "exactly the documented fields");
        }

        [TestMethod]
        public void TheRealBackendReportsTheLiveSettingsAndTheCodecState()
        {
            var backend = new Server.Admin.ServerAdminApiBackend();
            With(CompressionSide.Server, new CompressionConfig { Mode = CompressionMode.Adaptive, ServerLevel = 9, ClientLevel = 5 }, () =>
            {
                var network = backend.GetNetwork();
                Assert.IsNotNull(network.Compression);
                Assert.AreEqual("Adaptive", network.Compression.Mode);
                Assert.AreEqual(15, network.Compression.EffectiveLevelNow, "a typical payload gets level 15 with the defaults");
                Assert.AreEqual(5, network.Compression.ClientLevel);
                Assert.IsTrue(network.Compression.CacheHits >= 0 && network.Compression.CacheMisses >= 0);
            });
        }

        #endregion

        #region Cache

        [TestMethod]
        public void Cache_HitMissAndAPrivateCopyOnEveryHit()
        {
            var cache = new CompressedPayloadCache(1024 * 1024);
            byte[] copy;
            Assert.IsFalse(cache.TryGet("Vessel", 1, 100, 9, out copy));

            cache.Add("Vessel", 1, 100, 9, new byte[] { 1, 2, 3, 4 });
            Assert.IsTrue(cache.TryGet("Vessel", 1, 100, 9, out copy));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, copy);

            copy[0] = 99; // a recycled message overwrites its array
            byte[] again;
            Assert.IsTrue(cache.TryGet("Vessel", 1, 100, 9, out again));
            Assert.AreEqual(1, again[0], "the cached bytes are not shared with the caller");
            Assert.AreNotSame(copy, again);

            Assert.IsFalse(cache.TryGet("Vessel", 1, 100, 8, out _), "another level is another entry");
            Assert.IsFalse(cache.TryGet("Kerbal", 1, 100, 9, out _), "another type is another entry");
            Assert.IsFalse(cache.TryGet("Vessel", 2, 100, 9, out _), "another content hash is another entry");
            Assert.IsFalse(cache.TryGet("Vessel", 1, 101, 9, out _), "another length is another entry");
            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(4 + CompressedPayloadCache.EntryOverheadBytes, cache.Bytes);
        }

        [TestMethod]
        public void Cache_EvictsTheLeastRecentlyUsedEntriesAndStaysUnderItsCap()
        {
            var entryCost = 1000 + CompressedPayloadCache.EntryOverheadBytes;
            var cache = new CompressedPayloadCache(entryCost * 4);
            for (ulong i = 1; i <= 4; i++) cache.Add("T", i, 5000, 9, new byte[1000]);
            Assert.AreEqual(4, cache.Count);
            Assert.AreEqual(0, cache.Evictions);

            Assert.IsTrue(cache.TryGet("T", 1, 5000, 9, out _), "touch entry 1: it is now the newest");
            cache.Add("T", 5, 5000, 9, new byte[1000]);
            Assert.AreEqual(4, cache.Count);
            Assert.AreEqual(1, cache.Evictions);
            Assert.IsFalse(cache.TryGet("T", 2, 5000, 9, out _), "entry 2 was the oldest untouched one");
            Assert.IsTrue(cache.TryGet("T", 1, 5000, 9, out _));
            Assert.IsTrue(cache.TryGet("T", 5, 5000, 9, out _));

            for (ulong i = 10; i < 200; i++) cache.Add("T", i, 5000, 9, new byte[1000]);
            Assert.IsTrue(cache.Bytes <= cache.MaxBytes, "bytes " + cache.Bytes);
            Assert.AreEqual(4, cache.Count);
        }

        [TestMethod]
        public void Cache_SkipsEntriesThatAloneAreMoreThanAQuarterAndReplacesSameKeys()
        {
            var cache = new CompressedPayloadCache(10000);
            cache.Add("T", 1, 10, 9, new byte[3000]);
            Assert.AreEqual(0, cache.Count, "3000 of 10000 is too big an entry");
            cache.Add("T", 1, 10, 9, new byte[100]);
            cache.Add("T", 1, 10, 9, new byte[200]);
            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(200 + CompressedPayloadCache.EntryOverheadBytes, cache.Bytes, "the replaced entry is not counted twice");
            cache.Add("T", 2, 10, 9, null);
            cache.Add("T", 3, 10, 9, new byte[0]);
            Assert.AreEqual(1, cache.Count);
        }

        [TestMethod]
        public void Cache_InvalidateByTypeAndClear()
        {
            var cache = new CompressedPayloadCache(1024 * 1024);
            cache.Add("Vessel", 1, 10, 9, new byte[10]);
            cache.Add("Vessel", 2, 10, 9, new byte[10]);
            cache.Add("Kerbal", 1, 10, 9, new byte[10]);
            Assert.AreEqual(2, cache.Invalidate("Vessel"));
            Assert.AreEqual(1, cache.Count);
            Assert.IsTrue(cache.TryGet("Kerbal", 1, 10, 9, out _));
            Assert.AreEqual(0, cache.Invalidate("Nothing"));
            cache.Clear();
            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(0, cache.Bytes);
        }

        [TestMethod]
        public void Cache_AChangedContentHasADifferentHash()
        {
            var data = Compressible(1000, 14);
            var hash = CompressedPayloadCache.HashOf(data, data.Length);
            Assert.AreEqual(hash, CompressedPayloadCache.HashOf((byte[])data.Clone(), data.Length));
            for (var i = 0; i < data.Length; i += 37)
            {
                var changed = (byte[])data.Clone();
                changed[i] ^= 1;
                Assert.AreNotEqual(hash, CompressedPayloadCache.HashOf(changed, changed.Length), "byte " + i);
            }

            Assert.AreNotEqual(CompressedPayloadCache.HashOf(new byte[100], 100), CompressedPayloadCache.HashOf(new byte[101], 101), "trailing zeros count");
            Assert.AreEqual(CompressedPayloadCache.HashOf(data, 500), CompressedPayloadCache.HashOf(Prefix(data, 500, 20), 500), "only the first bytes are hashed");
        }

        [TestMethod]
        public void TheServerCompressesTheSamePayloadOnceAndCountsHitsAndMisses()
        {
            var name = "CacheStats" + Guid.NewGuid().ToString("N");
            With(CompressionSide.Server, FixedServer(9), () =>
            {
                var original = Compressible(30000, 15);
                var hits = VmpTrafficStats.CacheHits;
                var misses = VmpTrafficStats.CacheMisses;

                var first = (byte[])original.Clone();
                var firstCount = first.Length;
                Common.ThreadSafeCompress(Gate, ref first, ref firstCount, name);
                Assert.AreEqual(misses + 1, VmpTrafficStats.CacheMisses);
                Assert.AreEqual(hits, VmpTrafficStats.CacheHits);
                Assert.AreEqual(1, MessageCodec.Cache.Count);

                var second = (byte[])original.Clone();
                var secondCount = second.Length;
                Common.ThreadSafeCompress(Gate, ref second, ref secondCount, name);
                Assert.AreEqual(hits + 1, VmpTrafficStats.CacheHits, "the second send did not compress again");
                Assert.AreEqual(firstCount, secondCount);
                Assert.IsTrue(first.Take(firstCount).SequenceEqual(second.Take(secondCount)));
                Assert.AreNotSame(first, second, "every message gets its own array");

                // A recycled message overwrites its array; the cache must not be affected.
                Array.Clear(second, 0, second.Length);
                var third = (byte[])original.Clone();
                var thirdCount = third.Length;
                Common.ThreadSafeCompress(Gate, ref third, ref thirdCount, name);
                int decoded;
                AssertSame(original, Decode(third, thirdCount, out decoded, name), decoded, "third send");

                // Changed content: a miss and a different result.
                var changed = (byte[])original.Clone();
                changed[1234] ^= 0x55;
                var changedCount = changed.Length;
                Common.ThreadSafeCompress(Gate, ref changed, ref changedCount, name);
                Assert.AreEqual(misses + 2, VmpTrafficStats.CacheMisses);
                AssertSame(original.Select((b, i) => i == 1234 ? (byte)(b ^ 0x55) : b).ToArray(), Decode(changed, changedCount, out decoded, name), decoded, "changed content");

                var row = VmpTrafficStats.GetMessageCompressionTotals().Single(r => r.Type == name);
                Assert.AreEqual(9.0, row.AvgLevel, 0.0001, "cache hits count with the level they were stored at");
                Assert.AreEqual("zstd", row.Codec.Split('+')[0]);
            });
        }

        [TestMethod]
        public void AnotherLevelIsAnotherCacheEntry_AndAClientDoesNotUseTheCache()
        {
            var name = "CacheLevel" + Guid.NewGuid().ToString("N");
            var original = Compressible(30000, 16);
            var config = FixedServer(5);
            With(CompressionSide.Server, config, () =>
            {
                var a = (byte[])original.Clone();
                var count = a.Length;
                Common.ThreadSafeCompress(Gate, ref a, ref count, name);
                config.ServerLevel = 9; // changed live
                var misses = VmpTrafficStats.CacheMisses;
                var b = (byte[])original.Clone();
                count = b.Length;
                Common.ThreadSafeCompress(Gate, ref b, ref count, name);
                Assert.AreEqual(misses + 1, VmpTrafficStats.CacheMisses);
                Assert.AreEqual(2, MessageCodec.Cache.Count);
            });

            With(CompressionSide.Client, new CompressionConfig(), () =>
            {
                var hits = VmpTrafficStats.CacheHits;
                var misses = VmpTrafficStats.CacheMisses;
                for (var i = 0; i < 2; i++)
                {
                    var c = (byte[])original.Clone();
                    var count = c.Length;
                    Common.ThreadSafeCompress(Gate, ref c, ref count, name);
                }

                Assert.AreEqual(hits, VmpTrafficStats.CacheHits);
                Assert.AreEqual(misses, VmpTrafficStats.CacheMisses);
                Assert.AreEqual(0, MessageCodec.Cache.Count);
            });
        }

        #endregion
    }
}
