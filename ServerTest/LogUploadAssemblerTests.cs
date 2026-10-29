using LmpCommon.Enums;
using LmpCommon.LogUpload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Message.Base;
using Server.Server;
using Server.System.LogUpload;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ServerTest
{
    [TestClass]
    public class LogUploadAssemblerTests
    {
        private const int Chunk = 16 * 1024;
        private static readonly Regex StoredNamePattern = new Regex(@"^[A-Za-z0-9_-]{1,15}_\d{8}-\d{6}Z(-stall|-errors|-crash|-requested|-test)?(-\d+)?\.zip$");

        private string _root;
        private string _folder;
        private DateTime _now;
        private long _freeDisk;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "vmp-logupload-tests", Guid.NewGuid().ToString("N"));
            _folder = Path.Combine(_root, "ClientLogs");
            Directory.CreateDirectory(_root);
            _now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            _freeDisk = long.MaxValue;
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private LogUploadAssembler NewAssembler(int maxFiles = LogUploadLimits.FolderMaxFiles, long maxBytes = LogUploadLimits.FolderMaxBytes)
        {
            return new LogUploadAssembler(_folder, () => _now, _ => _freeDisk, maxFiles, maxBytes);
        }

        private static byte[] ZipLike(int size, int seed = 1)
        {
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            bytes[0] = (byte)'P';
            bytes[1] = (byte)'K';
            bytes[2] = 3;
            bytes[3] = 4;
            return bytes;
        }

        private static int ChunkCount(int total, int chunk = Chunk) => (total + chunk - 1) / chunk;

        private static byte[] Slice(byte[] data, int index, int chunk = Chunk)
        {
            var offset = index * chunk;
            var count = Math.Min(chunk, data.Length - offset);
            var result = new byte[count];
            Array.Copy(data, offset, result, 0, count);
            return result;
        }

        private static LogUploadDecision Begin(LogUploadAssembler a, object owner, string player, int uploadId, byte[] data, int chunk = Chunk,
            LogUploadReason reason = LogUploadReason.Manual)
        {
            return a.Begin(owner, player, true, uploadId, LogUploadFileKind.ClientLogsZip, data.Length, chunk, ChunkCount(data.Length, chunk), reason);
        }

        private static LogUploadDecision SendChunk(LogUploadAssembler a, object owner, string player, int uploadId, byte[] data, int index, int chunk = Chunk)
        {
            var slice = Slice(data, index, chunk);
            return a.AddChunk(owner, player, uploadId, index, ChunkCount(data.Length, chunk), data.Length, slice, slice.Length);
        }

        /// <summary>Begin plus every chunk; returns the last decision.</summary>
        private static LogUploadDecision Upload(LogUploadAssembler a, object owner, string player, int uploadId, byte[] data, int chunk = Chunk,
            LogUploadReason reason = LogUploadReason.Manual)
        {
            var begin = Begin(a, owner, player, uploadId, data, chunk, reason);
            if (begin.Outcome != LogUploadOutcome.Accepted) return begin;
            LogUploadDecision last = begin;
            for (var i = 0; i < ChunkCount(data.Length, chunk); i++)
            {
                last = SendChunk(a, owner, player, uploadId, data, i, chunk);
                if (last.Outcome != LogUploadOutcome.ChunkStored) break;
            }

            return last;
        }

        private string[] TempFiles() => Directory.Exists(_folder) ? Directory.GetFiles(_folder, LogUploadAssembler.TempFilePrefix + "*") : new string[0];

        private string[] Zips() => Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.zip") : new string[0];

        private void AssertRefused(LogUploadDecision d, LogUploadRefusalReason reason)
        {
            Assert.AreEqual(LogUploadOutcome.Refused, d.Outcome, d.Detail);
            Assert.AreEqual(reason, d.Reason, d.Detail);
            Assert.IsFalse(string.IsNullOrEmpty(d.Detail));
            Assert.IsTrue(d.Detail.Length <= LogUploadLimits.MaxReplyTextLength);
        }

        #region Happy path

        [TestMethod]
        public void HappyPathStoresTheExactBytesUnderAGeneratedName()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(100_000);

            var begin = Begin(a, owner, "Vlad", 7, data);
            Assert.AreEqual(LogUploadOutcome.Accepted, begin.Outcome);
            Assert.AreEqual(1, a.ActiveUploadCount);
            Assert.AreEqual(1, TempFiles().Length, "chunks are spooled to a temp file in the target folder");

            LogUploadDecision last = null;
            for (var i = 0; i < ChunkCount(data.Length); i++)
            {
                last = SendChunk(a, owner, "Vlad", 7, data, i);
                Assert.AreEqual(i < ChunkCount(data.Length) - 1 ? LogUploadOutcome.ChunkStored : LogUploadOutcome.Completed, last.Outcome);
            }

            Assert.AreEqual("Vlad_20261024-120000Z.zip", last.StoredName);
            Assert.AreEqual(data.Length, last.StoredBytes);
            Assert.AreEqual(7, last.UploadId);
            Assert.AreEqual(Path.Combine(Path.GetFullPath(_folder), last.StoredName), last.StoredPath);
            CollectionAssert.AreEqual(data, File.ReadAllBytes(last.StoredPath));
            Assert.AreEqual(0, TempFiles().Length);
            Assert.AreEqual(0, a.ActiveUploadCount);
            Assert.AreEqual(0, a.ActiveSpooledBytes);
        }

        [TestMethod]
        public void SingleChunkAndExactMultipleSizesWork()
        {
            var a = NewAssembler();
            var small = Upload(a, new object(), "One", 1, ZipLike(500));
            Assert.AreEqual(LogUploadOutcome.Completed, small.Outcome, small.Detail);

            _now = _now.AddMinutes(1);
            var exact = Upload(a, new object(), "Two", 2, ZipLike(Chunk * 3));
            Assert.AreEqual(LogUploadOutcome.Completed, exact.Outcome, exact.Detail);
            Assert.AreEqual(Chunk * 3, new FileInfo(exact.StoredPath).Length);
        }

        [TestMethod]
        public void TwoPlayersCanUploadInterleaved()
        {
            var a = NewAssembler();
            object ownerA = new object(), ownerB = new object();
            var dataA = ZipLike(40_000, 1);
            var dataB = ZipLike(70_000, 2);
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, ownerA, "Alice", 1, dataA).Outcome);
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, ownerB, "Bob", 2, dataB).Outcome);

            LogUploadDecision lastA = null, lastB = null;
            for (var i = 0; i < Math.Max(ChunkCount(dataA.Length), ChunkCount(dataB.Length)); i++)
            {
                if (i < ChunkCount(dataA.Length)) lastA = SendChunk(a, ownerA, "Alice", 1, dataA, i);
                if (i < ChunkCount(dataB.Length)) lastB = SendChunk(a, ownerB, "Bob", 2, dataB, i);
            }

            Assert.AreEqual(LogUploadOutcome.Completed, lastA.Outcome);
            Assert.AreEqual(LogUploadOutcome.Completed, lastB.Outcome);
            CollectionAssert.AreEqual(dataA, File.ReadAllBytes(lastA.StoredPath));
            CollectionAssert.AreEqual(dataB, File.ReadAllBytes(lastB.StoredPath));
        }

        #endregion

        #region Bad content

        [TestMethod]
        public void RejectsAFileWithoutZipMagicAndDeletesTheTempFile()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(50_000);
            data[0] = (byte)'M';
            data[1] = (byte)'Z';
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, owner, "Vlad", 1, data).Outcome);

            AssertRefused(SendChunk(a, owner, "Vlad", 1, data, 0), LogUploadRefusalReason.BadFile);
            Assert.AreEqual(0, TempFiles().Length);
            Assert.AreEqual(0, Zips().Length);
            Assert.AreEqual(0, a.ActiveUploadCount);
            Assert.AreEqual(LogUploadOutcome.Ignored, SendChunk(a, owner, "Vlad", 1, data, 1).Outcome, "later chunks of an aborted upload are ignored");
        }

        [TestMethod]
        public void RejectsAChunkOfTheWrongLength()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(50_000);
            Begin(a, owner, "Vlad", 1, data);

            var shortChunk = Slice(data, 0).Take(Chunk - 1).ToArray();
            AssertRefused(a.AddChunk(owner, "Vlad", 1, 0, ChunkCount(data.Length), data.Length, shortChunk, shortChunk.Length), LogUploadRefusalReason.BadFile);
            Assert.AreEqual(0, TempFiles().Length);
        }

        [TestMethod]
        public void RejectsALastChunkThatDoesNotAddUpToTheDeclaredSize()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(40_000);
            Begin(a, owner, "Vlad", 1, data);
            SendChunk(a, owner, "Vlad", 1, data, 0);
            SendChunk(a, owner, "Vlad", 1, data, 1);

            var tooLong = new byte[Chunk];
            AssertRefused(a.AddChunk(owner, "Vlad", 1, 2, 3, data.Length, tooLong, tooLong.Length), LogUploadRefusalReason.BadFile);
            Assert.AreEqual(0, Zips().Length);
            Assert.AreEqual(0, TempFiles().Length);
        }

        [TestMethod]
        public void RejectsACountLargerThanTheBuffer()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(40_000);
            Begin(a, owner, "Vlad", 1, data);
            var slice = Slice(data, 0);
            AssertRefused(a.AddChunk(owner, "Vlad", 1, 0, 3, data.Length, slice, slice.Length + 1), LogUploadRefusalReason.BadFile);
            Begin(a, owner, "Vlad2", 2, data);
            AssertRefused(a.AddChunk(owner, "Vlad2", 2, 0, 3, data.Length, null, 0), LogUploadRefusalReason.BadFile);
        }

        [TestMethod]
        public void RejectsAChunkWhoseHeaderDisagreesWithBegin()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(40_000);
            Begin(a, owner, "Vlad", 1, data);
            var slice = Slice(data, 0);
            AssertRefused(a.AddChunk(owner, "Vlad", 1, 0, 3, data.Length + 1, slice, slice.Length), LogUploadRefusalReason.BadFile);

            _now = _now.AddMinutes(1);
            Begin(a, owner, "Vlad", 2, data);
            AssertRefused(a.AddChunk(owner, "Vlad", 2, 0, 4, data.Length, slice, slice.Length), LogUploadRefusalReason.BadFile);
        }

        #endregion

        #region Chunk order

        [TestMethod]
        public void RejectsOutOfOrderChunks()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(60_000);
            Begin(a, owner, "Vlad", 1, data);
            Assert.AreEqual(LogUploadOutcome.ChunkStored, SendChunk(a, owner, "Vlad", 1, data, 0).Outcome);
            AssertRefused(SendChunk(a, owner, "Vlad", 1, data, 2), LogUploadRefusalReason.BadFile);
            Assert.AreEqual(0, TempFiles().Length);
        }

        [TestMethod]
        public void RejectsADuplicateChunk()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(60_000);
            Begin(a, owner, "Vlad", 1, data);
            SendChunk(a, owner, "Vlad", 1, data, 0);
            AssertRefused(SendChunk(a, owner, "Vlad", 1, data, 0), LogUploadRefusalReason.BadFile);
        }

        [TestMethod]
        public void RejectsAMissingFirstChunk()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(60_000);
            Begin(a, owner, "Vlad", 1, data);
            AssertRefused(SendChunk(a, owner, "Vlad", 1, data, 1), LogUploadRefusalReason.BadFile);
        }

        [TestMethod]
        public void RejectsANegativeOrPastTheEndChunkIndex()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            Begin(a, owner, "Vlad", 1, data);
            var slice = Slice(data, 0);
            AssertRefused(a.AddChunk(owner, "Vlad", 1, -1, 2, data.Length, slice, slice.Length), LogUploadRefusalReason.BadFile);

            _now = _now.AddMinutes(1);
            Begin(a, owner, "Vlad", 2, data);
            SendChunk(a, owner, "Vlad", 2, data, 0);
            SendChunk(a, owner, "Vlad", 2, data, 1);
            Assert.AreEqual(LogUploadOutcome.Ignored, a.AddChunk(owner, "Vlad", 2, 2, 2, data.Length, slice, slice.Length).Outcome,
                "the upload already completed; an extra chunk has nothing to attach to");
        }

        [TestMethod]
        public void RejectsAChunkForADifferentUploadId()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(40_000);
            Begin(a, owner, "Vlad", 1, data);
            var d = SendChunk(a, owner, "Vlad", 99, data, 0);
            AssertRefused(d, LogUploadRefusalReason.BadFile);
            Assert.AreEqual(1, d.UploadId, "the refusal names the upload that was aborted");
        }

        [DataTestMethod]
        [DataRow(40_000, Chunk, 2)]
        [DataRow(40_000, Chunk, 4)]
        [DataRow(40_000, Chunk, 0)]
        [DataRow(40_000, Chunk, -1)]
        [DataRow(40_000, 512, 79)]
        [DataRow(40_000, 128 * 1024, 1)]
        [DataRow(40_000, 0, 1)]
        [DataRow(10, Chunk, 1)]
        [DataRow(0, Chunk, 0)]
        [DataRow(-5, Chunk, 1)]
        public void RejectsBadBeginGeometry(int total, int chunkSize, int chunkCount)
        {
            var a = NewAssembler();
            var d = a.Begin(new object(), "Vlad", true, 1, LogUploadFileKind.ClientLogsZip, total, chunkSize, chunkCount);
            AssertRefused(d, LogUploadRefusalReason.BadFile);
            Assert.IsFalse(Directory.Exists(_folder), "a refused Begin creates nothing");
        }

        [TestMethod]
        public void RejectsAnUnknownFileKind()
        {
            var a = NewAssembler();
            AssertRefused(a.Begin(new object(), "Vlad", true, 1, (LogUploadFileKind)42, 40_000, Chunk, 3), LogUploadRefusalReason.BadFile);
        }

        #endregion

        #region Limits

        [TestMethod]
        public void RejectsOversizeUploads()
        {
            var a = NewAssembler();
            var total = LogUploadLimits.MaxTotalBytes + 1;
            AssertRefused(a.Begin(new object(), "Vlad", true, 1, LogUploadFileKind.ClientLogsZip, total, LogUploadLimits.MaxChunkBytes,
                ChunkCount(total, LogUploadLimits.MaxChunkBytes)), LogUploadRefusalReason.TooLarge);
            AssertRefused(a.Begin(new object(), "Vlad", true, 1, LogUploadFileKind.ClientLogsZip, int.MaxValue, LogUploadLimits.MaxChunkBytes,
                ChunkCount(int.MaxValue, LogUploadLimits.MaxChunkBytes)), LogUploadRefusalReason.TooLarge);
            Assert.IsFalse(Directory.Exists(_folder));

            // Exactly at the limit is fine.
            Assert.AreEqual(LogUploadOutcome.Accepted, a.Begin(new object(), "Vlad", true, 1, LogUploadFileKind.ClientLogsZip,
                LogUploadLimits.MaxTotalBytes, LogUploadLimits.MaxChunkBytes, ChunkCount(LogUploadLimits.MaxTotalBytes, LogUploadLimits.MaxChunkBytes)).Outcome);
        }

        [TestMethod]
        public void RefusesASecondConcurrentUploadFromTheSamePlayer()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(40_000);
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, owner, "Vlad", 1, data).Outcome);
            AssertRefused(Begin(a, owner, "Vlad", 2, data), LogUploadRefusalReason.Busy);
            AssertRefused(Begin(a, new object(), "VLAD", 3, data), LogUploadRefusalReason.Busy);
            Assert.AreEqual(1, a.ActiveUploadCount);
            Assert.AreEqual(1, TempFiles().Length);
        }

        [TestMethod]
        public void RefusesMoreConcurrentUploadsThanTheServerWideCap()
        {
            var a = NewAssembler();
            var data = ZipLike(40_000);
            for (var i = 0; i < LogUploadLimits.MaxConcurrentUploadsServerWide; i++)
                Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, new object(), "P" + i, i, data).Outcome);
            AssertRefused(Begin(a, new object(), "Late", 99, data), LogUploadRefusalReason.Busy);
        }

        [TestMethod]
        public void RateLimitsToThreeUploadsPerPlayerPerHour()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            for (var i = 0; i < LogUploadLimits.MaxUploadsPerPlayerPerHour; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", i + 1, data).Outcome);
                _now = _now.AddMinutes(1);
            }

            var refused = Begin(a, owner, "Vlad", 10, data);
            AssertRefused(refused, LogUploadRefusalReason.TooMany);
            Assert.IsTrue(refused.Detail.Contains("57 min"), refused.Detail);
            AssertRefused(Begin(a, new object(), "vLaD", 11, data), LogUploadRefusalReason.TooMany);

            _now = _now.AddMinutes(58);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 12, data).Outcome, "the oldest upload left the one-hour window");
        }

        [TestMethod]
        public void RateLimitsBackToBackUploads()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 1, data).Outcome);
            _now = _now.AddSeconds(10);
            AssertRefused(Begin(a, owner, "Vlad", 2, data), LogUploadRefusalReason.TooFrequent);
            _now = _now.AddSeconds(21);
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, owner, "Vlad", 3, data).Outcome);
        }

        [TestMethod]
        public void TheGapIsMeasuredFromTheEndOfALongUpload()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(Chunk * 4);
            Begin(a, owner, "Vlad", 1, data);
            for (var i = 0; i < 4; i++)
            {
                _now = _now.AddSeconds(25);
                SendChunk(a, owner, "Vlad", 1, data, i);
            }

            Assert.AreEqual(0, a.ActiveUploadCount, "completed 100 s after it started");
            _now = _now.AddSeconds(10);
            var refused = Begin(a, owner, "Vlad", 2, data);
            AssertRefused(refused, LogUploadRefusalReason.TooFrequent);
            StringAssert.Contains(refused.Detail, "20 s");
            _now = _now.AddSeconds(21);
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, owner, "Vlad", 3, data).Outcome);
        }

        [TestMethod]
        public void FailedAttemptsStillCountTowardsTheRateLimit()
        {
            var a = NewAssembler();
            var owner = new object();
            var bad = ZipLike(20_000);
            bad[0] = 0;
            for (var i = 0; i < LogUploadLimits.MaxUploadsPerPlayerPerHour; i++)
            {
                AssertRefused(Upload(a, owner, "Vlad", i + 1, bad), LogUploadRefusalReason.BadFile);
                _now = _now.AddMinutes(1);
            }

            AssertRefused(Begin(a, owner, "Vlad", 9, ZipLike(20_000)), LogUploadRefusalReason.TooMany);
        }

        [TestMethod]
        public void RateLimitsServerWide()
        {
            var a = NewAssembler();
            var data = ZipLike(5_000);
            for (var i = 0; i < LogUploadLimits.MaxUploadsPerHourServerWide; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "P" + i, i, data).Outcome);
                _now = _now.AddSeconds(1);
            }

            AssertRefused(Begin(a, new object(), "Another", 100, data), LogUploadRefusalReason.TooMany);
        }

        [TestMethod]
        public void RefusesWhenTheDiskIsNearlyFull()
        {
            var a = NewAssembler();
            _freeDisk = LogUploadLimits.MinFreeDiskBytes + 10_000;
            AssertRefused(Begin(a, new object(), "Vlad", 1, ZipLike(20_000)), LogUploadRefusalReason.Busy);
            _freeDisk = LogUploadLimits.MinFreeDiskBytes + 20_000;
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, new object(), "Vlad", 2, ZipLike(20_000)).Outcome);
        }

        #endregion

        #region Who may upload

        [TestMethod]
        public void RefusesUnauthenticatedSenders()
        {
            var a = NewAssembler();
            var data = ZipLike(20_000);
            AssertRefused(a.Begin(new object(), "Vlad", false, 1, LogUploadFileKind.ClientLogsZip, data.Length, Chunk, 2), LogUploadRefusalReason.NotAllowed);
            AssertRefused(a.Begin(null, "Vlad", true, 1, LogUploadFileKind.ClientLogsZip, data.Length, Chunk, 2), LogUploadRefusalReason.NotAllowed);
            Assert.IsFalse(Directory.Exists(_folder));
            Assert.AreEqual(0, a.ActiveUploadCount);
        }

        [TestMethod]
        public void IgnoresChunksFromAnotherConnectionOrWithoutAnUpload()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(40_000);
            Assert.AreEqual(LogUploadOutcome.Ignored, SendChunk(a, owner, "Vlad", 1, data, 0).Outcome);

            Begin(a, owner, "Vlad", 1, data);
            Assert.AreEqual(LogUploadOutcome.Ignored, SendChunk(a, new object(), "Vlad", 1, data, 0).Outcome);
            Assert.AreEqual(LogUploadOutcome.Ignored, SendChunk(a, null, "Vlad", 1, data, 0).Outcome);
            Assert.AreEqual(LogUploadOutcome.Ignored, a.Cancel(new object(), "Vlad", 1).Outcome);
            Assert.IsNull(a.AbortForDisconnect(new object(), "Vlad"));

            // The real owner's upload is untouched.
            for (var i = 0; i < 3; i++) SendChunk(a, owner, "Vlad", 1, data, i);
            Assert.AreEqual(1, Zips().Length);
        }

        #endregion

        #region Timeouts and cleanup

        [TestMethod]
        public void AnAbandonedUploadTimesOutAndItsBufferIsFreed()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(60_000);
            Begin(a, owner, "Vlad", 1, data);
            SendChunk(a, owner, "Vlad", 1, data, 0);
            Assert.AreEqual(Chunk, a.ActiveSpooledBytes);

            _now = _now.Add(LogUploadLimits.InactivityTimeout).AddSeconds(-1);
            Assert.AreEqual(0, a.SweepExpired().Count, "not idle long enough yet");

            _now = _now.AddSeconds(2);
            var expired = a.SweepExpired();
            Assert.AreEqual(1, expired.Count);
            AssertRefused(expired[0], LogUploadRefusalReason.TimedOut);
            Assert.AreSame(owner, expired[0].Owner, "the system replies to the connection that owned the upload");
            Assert.AreEqual(0, a.ActiveUploadCount);
            Assert.AreEqual(0, a.ActiveSpooledBytes);
            Assert.AreEqual(0, TempFiles().Length);
            Assert.AreEqual(LogUploadOutcome.Ignored, SendChunk(a, owner, "Vlad", 1, data, 1).Outcome);
        }

        [TestMethod]
        public void ASlowDripUploadIsCutOffAtTheMaximumDuration()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(LogUploadLimits.MinChunkBytes * 20);
            Begin(a, owner, "Vlad", 1, data, LogUploadLimits.MinChunkBytes);
            LogUploadDecision last = null;
            for (var i = 0; i < 20; i++)
            {
                last = SendChunk(a, owner, "Vlad", 1, data, i, LogUploadLimits.MinChunkBytes);
                if (last.Outcome == LogUploadOutcome.Refused) break;
                Assert.AreEqual(0, a.SweepExpired().Count);
                _now = _now.AddSeconds(100); // always inside the inactivity timeout
            }

            AssertRefused(last, LogUploadRefusalReason.TimedOut);
            Assert.AreEqual(0, TempFiles().Length);
            Assert.AreEqual(0, Zips().Length);
        }

        [TestMethod]
        public void CancelAndDisconnectDeleteThePartialFile()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(60_000);
            Begin(a, owner, "Vlad", 1, data);
            SendChunk(a, owner, "Vlad", 1, data, 0);
            Assert.AreEqual(LogUploadOutcome.Ignored, a.Cancel(owner, "Vlad", 2).Outcome, "wrong upload id");
            AssertRefused(a.Cancel(owner, "Vlad", 1), LogUploadRefusalReason.Cancelled);
            Assert.AreEqual(0, TempFiles().Length);

            _now = _now.AddMinutes(1);
            var other = new object();
            Begin(a, other, "Bob", 5, data);
            var dropped = a.AbortForDisconnect(other, "Bob");
            Assert.IsNotNull(dropped);
            Assert.AreEqual(5, dropped.UploadId);
            Assert.AreEqual(0, TempFiles().Length);
            Assert.AreEqual(0, a.ActiveUploadCount);
        }

        [TestMethod]
        public void StaleTempFilesFromAPreviousRunAreRemoved()
        {
            Directory.CreateDirectory(_folder);
            var stale = Path.Combine(_folder, LogUploadAssembler.TempFilePrefix + "crashed" + LogUploadAssembler.TempFileExtension);
            File.WriteAllText(stale, "partial");
            var keep = Path.Combine(_folder, "notes.txt");
            File.WriteAllText(keep, "host notes");

            var a = NewAssembler();
            Assert.AreEqual(LogUploadOutcome.Accepted, Begin(a, new object(), "Vlad", 1, ZipLike(20_000)).Outcome);
            Assert.IsFalse(File.Exists(stale));
            Assert.IsTrue(File.Exists(keep));
        }

        #endregion

        #region Automatic reports

        private string[] IndexLines()
        {
            var path = Path.Combine(_folder, LogUploadAssembler.IndexFileName);
            return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToArray() : new string[0];
        }

        [DataTestMethod]
        [DataRow(LogUploadReason.Manual, "Vlad_20261024-120000Z.zip")]
        [DataRow(LogUploadReason.Stall, "Vlad_20261024-120000Z-stall.zip")]
        [DataRow(LogUploadReason.Errors, "Vlad_20261024-120000Z-errors.zip")]
        [DataRow(LogUploadReason.Crash, "Vlad_20261024-120000Z-crash.zip")]
        [DataRow(LogUploadReason.Requested, "Vlad_20261024-120000Z-requested.zip")]
        [DataRow(LogUploadReason.Test, "Vlad_20261024-120000Z-test.zip")]
        public void TheStoredNameCarriesTheReason(LogUploadReason reason, string expected)
        {
            var a = NewAssembler();
            var done = Upload(a, new object(), "Vlad", 1, ZipLike(30_000), Chunk, reason);
            Assert.AreEqual(LogUploadOutcome.Completed, done.Outcome, done.Detail);
            Assert.AreEqual(expected, done.StoredName);
            Assert.AreEqual(reason, done.UploadReason);
            Assert.IsTrue(StoredNamePattern.IsMatch(done.StoredName), done.StoredName);
        }

        [TestMethod]
        public void EveryStoredUploadIsListedInTheIndex()
        {
            var a = NewAssembler();
            var first = Upload(a, new object(), "Vlad", 1, ZipLike(30_000), Chunk, LogUploadReason.Stall);
            _now = _now.AddMinutes(30);
            var second = Upload(a, new object(), "Vlad", 2, ZipLike(31_000));
            Assert.AreEqual(LogUploadOutcome.Completed, second.Outcome, second.Detail);

            var lines = IndexLines();
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains(lines[0], "\"file\":\"" + first.StoredName + "\"");
            StringAssert.Contains(lines[0], "\"player\":\"Vlad\"");
            StringAssert.Contains(lines[0], "\"utc\":\"2026-10-24T12:00:00Z\"");
            StringAssert.Contains(lines[0], "\"bytes\":30000");
            StringAssert.Contains(lines[0], "\"reason\":\"stall\"");
            StringAssert.Contains(lines[1], "\"file\":\"" + second.StoredName + "\"");
            StringAssert.Contains(lines[1], "\"reason\":\"manual\"");
        }

        [TestMethod]
        public void TheIndexIsNotTouchedByRefusedOrFailedUploadsAndSurvivesTheQuota()
        {
            var a = NewAssembler(maxFiles: 1, maxBytes: 1);
            var bad = ZipLike(20_000);
            bad[0] = 0;
            AssertRefused(Upload(a, new object(), "Vlad", 1, bad), LogUploadRefusalReason.BadFile);
            Assert.AreEqual(0, IndexLines().Length);

            _now = _now.AddMinutes(10);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "Vlad", 2, ZipLike(20_000)).Outcome);
            _now = _now.AddMinutes(10);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "Vlad", 3, ZipLike(20_000)).Outcome);
            Assert.AreEqual(1, Zips().Length, "the quota kept only the newest zip");
            Assert.AreEqual(2, IndexLines().Length, "the index is history and is not trimmed with the zips");
        }

        [TestMethod]
        public void AnUnknownReasonIsRefused()
        {
            var a = NewAssembler();
            AssertRefused(Begin(a, new object(), "Vlad", 1, ZipLike(20_000), Chunk, (LogUploadReason)99), LogUploadRefusalReason.BadFile);
            Assert.AreEqual(0, a.ActiveUploadCount);
            Assert.AreEqual(0, TempFiles().Length);
        }

        [TestMethod]
        public void TheHostCanTurnOffAutomaticReportsButNotManualOnes()
        {
            var a = NewAssembler();
            a.AutoUploadsEnabled = false;
            foreach (var reason in new[] { LogUploadReason.Stall, LogUploadReason.Errors, LogUploadReason.Crash, LogUploadReason.Test })
            {
                var refused = Begin(a, new object(), "Vlad", 1, ZipLike(20_000), Chunk, reason);
                AssertRefused(refused, LogUploadRefusalReason.NotAllowed);
                StringAssert.Contains(refused.Detail, "automatic");
            }

            Assert.AreEqual(0, a.ActiveUploadCount);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "Vlad", 2, ZipLike(20_000)).Outcome, "manual still works");
            a.AutoUploadsEnabled = true;
            _now = _now.AddMinutes(1);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "Vlad", 4, ZipLike(20_000), Chunk, LogUploadReason.Stall).Outcome);
        }

        [TestMethod]
        public void AutomaticReportsHaveTheirOwnHourlyBudgetAndLeaveTheManualOneAlone()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            for (var i = 0; i < LogUploadLimits.MaxAutoUploadsPerPlayerPerHour; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", i + 1, data, Chunk, LogUploadReason.Stall).Outcome);
                _now = _now.AddMinutes(1);
            }

            var refused = Begin(a, owner, "Vlad", 50, data, Chunk, LogUploadReason.Errors);
            AssertRefused(refused, LogUploadRefusalReason.TooMany);
            StringAssert.Contains(refused.Detail, "per hour");

            for (var i = 0; i < LogUploadLimits.MaxUploadsPerPlayerPerHour; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 100 + i, data).Outcome,
                    "the player's own three uploads per hour are still available");
                _now = _now.AddMinutes(1);
            }

            AssertRefused(Begin(a, owner, "Vlad", 200, data), LogUploadRefusalReason.TooMany);
        }

        [TestMethod]
        public void AutomaticReportsDoNotUseUpThePlayersManualBudget()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 1, data, Chunk, LogUploadReason.Crash).Outcome);
            _now = _now.AddMinutes(1);
            for (var i = 0; i < LogUploadLimits.MaxUploadsPerPlayerPerHour; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 10 + i, data).Outcome);
                _now = _now.AddMinutes(1);
            }
        }

        [TestMethod]
        public void AutomaticReportsAreCappedPerDayAndTheBudgetComesBack()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            // One every 40 minutes stays under the hourly cap but runs into the daily one.
            for (var i = 0; i < LogUploadLimits.MaxAutoUploadsPerPlayerPerDay; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", i + 1, data, Chunk, LogUploadReason.Errors).Outcome, "report " + i);
                _now = _now.AddMinutes(40);
            }

            var refused = Begin(a, owner, "Vlad", 90, data, Chunk, LogUploadReason.Errors);
            AssertRefused(refused, LogUploadRefusalReason.TooMany);
            StringAssert.Contains(refused.Detail, "per day");

            _now = _now.AddHours(24);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 91, data, Chunk, LogUploadReason.Errors).Outcome,
                "the 24 hour window has passed");
        }

        [TestMethod]
        public void AutomaticReportsAreLimitedPerPlayerNotServerWide()
        {
            var a = NewAssembler();
            var data = ZipLike(20_000);
            for (var p = 0; p < 3; p++)
            {
                for (var i = 0; i < LogUploadLimits.MaxAutoUploadsPerPlayerPerHour; i++)
                {
                    Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "P" + p, p * 10 + i + 1, data, Chunk, LogUploadReason.Stall).Outcome);
                    _now = _now.AddSeconds(30);
                }
            }
        }

        [TestMethod]
        public void AutomaticReportsAlsoCountTowardsTheServerWideHourlyCap()
        {
            var a = NewAssembler();
            var data = ZipLike(5_000);
            for (var i = 0; i < LogUploadLimits.MaxUploadsPerHourServerWide; i++)
            {
                Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, new object(), "Q" + i, i + 1, data, Chunk, LogUploadReason.Stall).Outcome);
                _now = _now.AddSeconds(1);
            }

            AssertRefused(Begin(a, new object(), "Another", 500, data, Chunk, LogUploadReason.Stall), LogUploadRefusalReason.TooMany);
        }

        [TestMethod]
        public void AutomaticReportsAreAlsoSpacedByTheMinimumGap()
        {
            var a = NewAssembler();
            var owner = new object();
            var data = ZipLike(20_000);
            Assert.AreEqual(LogUploadOutcome.Completed, Upload(a, owner, "Vlad", 1, data, Chunk, LogUploadReason.Stall).Outcome);
            _now = _now.AddSeconds(5);
            AssertRefused(Begin(a, owner, "Vlad", 2, data, Chunk, LogUploadReason.Errors), LogUploadRefusalReason.TooFrequent);
        }

        [TestMethod]
        public void ARefusedAutomaticReportLeavesNoTraceInTheFolder()
        {
            var a = NewAssembler();
            a.AutoUploadsEnabled = false;
            Begin(a, new object(), "Vlad", 1, ZipLike(20_000), Chunk, LogUploadReason.Crash);
            Assert.IsFalse(Directory.Exists(_folder) && Directory.GetFileSystemEntries(_folder).Length > 0, "nothing was created for a refused report");
        }

        #endregion

        #region Quota

        [TestMethod]
        public void QuotaKeepsTheNewestFilesByCount()
        {
            Directory.CreateDirectory(_folder);
            var baseTime = DateTime.UtcNow.AddDays(-2);
            var existing = LogUploadLimits.FolderMaxFiles + 5;
            for (var i = 0; i < existing; i++)
            {
                var path = Path.Combine(_folder, $"Old{i:00}_20261001-000000Z.zip");
                File.WriteAllBytes(path, new byte[10]);
                File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(i));
            }

            var notes = Path.Combine(_folder, "notes.txt");
            File.WriteAllText(notes, "not a zip, never deleted");

            var a = NewAssembler();
            var done = Upload(a, new object(), "Vlad", 1, ZipLike(20_000));
            Assert.AreEqual(LogUploadOutcome.Completed, done.Outcome);

            Assert.AreEqual(LogUploadLimits.FolderMaxFiles, Zips().Length);
            Assert.IsTrue(File.Exists(done.StoredPath), "the new upload is kept");
            Assert.IsTrue(File.Exists(notes));
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 6).Select(i => $"Old{i:00}_20261001-000000Z.zip").ToList(), done.DeletedForQuota,
                "the six oldest go (FolderMaxFiles + 5 old + 1 new - FolderMaxFiles kept)");
        }

        [TestMethod]
        public void QuotaKeepsTheNewestFilesByBytes()
        {
            Directory.CreateDirectory(_folder);
            var baseTime = DateTime.UtcNow.AddDays(-1);
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(_folder, $"Big{i}_20261001-000000Z.zip");
                File.WriteAllBytes(path, new byte[40_000]);
                File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(i));
            }

            var a = NewAssembler(maxBytes: 100_000);
            var done = Upload(a, new object(), "Vlad", 1, ZipLike(30_000));
            Assert.AreEqual(LogUploadOutcome.Completed, done.Outcome);

            // Newest first: new (30 KB) + Big4 (40 KB) = 70 KB; Big3 would make 110 KB > 100 KB, so Big3 and older go.
            CollectionAssert.AreEquivalent(new[] { done.StoredName, "Big4_20261001-000000Z.zip" }, Zips().Select(Path.GetFileName).ToArray());
        }

        [TestMethod]
        public void QuotaNeverDeletesTheUploadJustStored()
        {
            var a = NewAssembler(maxFiles: 1, maxBytes: 1);
            var done = Upload(a, new object(), "Vlad", 1, ZipLike(30_000));
            Assert.AreEqual(LogUploadOutcome.Completed, done.Outcome);
            Assert.IsTrue(File.Exists(done.StoredPath));
        }

        #endregion

        #region Names and paths

        [DataTestMethod]
        [DataRow("Jeb", "Jeb")]
        [DataRow("Alex_Jeb-2", "Alex_Jeb-2")]
        [DataRow("../../evil", "evil")]
        [DataRow("..\\..\\Windows\\System32", "WindowsSystem32")]
        [DataRow("C:\\Users\\x", "CUsersx")]
        [DataRow("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
        [DataRow("CON", "CON")]
        [DataRow("", "player")]
        [DataRow(null, "player")]
        [DataRow("....", "player")]
        [DataRow("名前", "player")]
        [DataRow("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmno")]
        [DataRow("name with spaces", "namewithspaces")]
        [DataRow("\u0000\r\nx", "x")]
        public void SanitizesPlayerNames(string input, string expected)
        {
            Assert.AreEqual(expected, LogUploadAssembler.SanitizePlayerName(input));
        }

        [DataTestMethod]
        [DataRow("../../evil")]
        [DataRow("..\\..\\..\\Windows\\win.ini")]
        [DataRow("C:\\Windows\\Temp\\x")]
        [DataRow("/etc/passwd")]
        [DataRow("\\\\server\\share\\x")]
        [DataRow("CON")]
        [DataRow("NUL.zip")]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("..")]
        [DataRow("名前")]
        [DataRow("averyveryverylongplayernamethatgoesonandon")]
        public void NoClientSuppliedTextEscapesTheFolder(string playerName)
        {
            // Own sandbox per data row: only ClientLogs and the stored zip may appear in it.
            var sandbox = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            var folder = Path.Combine(sandbox, "ClientLogs");
            var a = new LogUploadAssembler(folder, () => _now, _ => long.MaxValue);
            var done = Upload(a, new object(), playerName, 1, ZipLike(20_000));
            Assert.AreEqual(LogUploadOutcome.Completed, done.Outcome, done.Detail);

            Assert.AreEqual(Path.GetFullPath(folder), Path.GetDirectoryName(done.StoredPath));
            Assert.IsTrue(StoredNamePattern.IsMatch(done.StoredName), done.StoredName);
            Assert.IsTrue(StoredNamePattern.IsMatch(done.PlayerName + "_20261024-120000Z.zip"), "the logged player name is sanitized too");
            var after = Directory.GetFileSystemEntries(sandbox, "*", SearchOption.AllDirectories);
            CollectionAssert.AreEquivalent(
                new[] { Path.GetFullPath(folder), done.StoredPath, Path.Combine(Path.GetFullPath(folder), LogUploadAssembler.IndexFileName) },
                after.Select(Path.GetFullPath).ToArray(),
                "only the ClientLogs folder, the stored zip and the index were created");
        }

        [TestMethod]
        public void SameSecondUploadsGetDistinctNames()
        {
            var a = NewAssembler();
            var first = Upload(a, new object(), "Vlad", 1, ZipLike(10_000, 1));
            var second = Upload(a, new object(), "Vlad.", 2, ZipLike(10_000, 2)); // different player, same sanitized name
            Assert.AreEqual(LogUploadOutcome.Completed, second.Outcome, second.Detail);
            Assert.AreEqual("Vlad_20261024-120000Z.zip", first.StoredName);
            Assert.AreEqual("Vlad_20261024-120000Z-2.zip", second.StoredName);
            CollectionAssert.AreNotEqual(File.ReadAllBytes(first.StoredPath), File.ReadAllBytes(second.StoredPath));
        }

        #endregion

        [TestMethod]
        public void EveryClientMessageTypeHasAServerReader()
        {
            var field = typeof(MessageReceiver).GetField("HandlerDictionary", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field);
            var handlers = (IDictionary)field.GetValue(null);
            foreach (ClientMessageType type in Enum.GetValues(typeof(ClientMessageType)))
            {
                Assert.IsTrue(handlers.Contains(type), $"No server reader for {type}");
                Assert.IsInstanceOfType(handlers[type], typeof(ReaderBase));
            }
        }
    }
}
