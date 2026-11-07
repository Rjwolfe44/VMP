using LmpCommon.LogUpload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace LmpCommonTest
{
    /// <summary>The pure client-side planning of log uploads: chunk arithmetic, pacing and the retry schedule.</summary>
    [TestClass]
    public class LogUploadClientPlanTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 6, 23, 0, 0, DateTimeKind.Utc);

        #region Chunk plan

        [DataTestMethod]
        [DataRow(5_700_000, 64 * 1024, 87)]
        [DataRow(5_700_000, 16 * 1024, 348)]
        [DataRow(65536, 65536, 1)]
        [DataRow(65537, 65536, 2)]
        [DataRow(22, 65536, 1)]
        public void TheChunksCoverTheFileExactlyAndOnlyTheLastOneIsShort(int total, int chunk, int expectedCount)
        {
            Assert.AreEqual(expectedCount, LogUploadChunkPlan.ChunkCount(total, chunk));
            long sum = 0;
            for (var i = 0; i < expectedCount; i++)
            {
                var length = LogUploadChunkPlan.ChunkLength(total, chunk, i);
                Assert.IsTrue(length > 0 && length <= chunk);
                if (i < expectedCount - 1) Assert.AreEqual(chunk, length, "every chunk but the last is full");
                sum += length;
            }

            Assert.AreEqual(total, sum);
            Assert.AreEqual(0, LogUploadChunkPlan.ChunkLength(total, chunk, expectedCount), "past the end");
            Assert.AreEqual(0, LogUploadChunkPlan.ChunkLength(total, chunk, -1));
        }

        [TestMethod]
        public void TheClientChunkSizeIsAcceptedByEveryServerThatAcceptsOneToSixtyFourKilobytes()
        {
            // The server checks chunk size in [MinChunkBytes, MaxChunkBytes] in Begin, so a new client talks to an old server unchanged.
            Assert.IsTrue(LogUploadLimits.ClientChunkBytes >= LogUploadLimits.MinChunkBytes && LogUploadLimits.ClientChunkBytes <= LogUploadLimits.MaxChunkBytes);
            foreach (var total in new[] { LogUploadLimits.MinTotalBytes, 1000, 70_000, 5_700_000, LogUploadLimits.MaxTotalBytes })
            {
                var count = LogUploadChunkPlan.ChunkCount(total, LogUploadLimits.ClientChunkBytes);
                Assert.IsTrue(LogUploadChunkPlan.IsAcceptable(total, LogUploadLimits.ClientChunkBytes, count), "total " + total);
            }
        }

        [TestMethod]
        public void PlansTheServerWouldRefuseAreRecognised()
        {
            Assert.IsFalse(LogUploadChunkPlan.IsAcceptable(LogUploadLimits.MaxTotalBytes + 1, 65536, 400));
            Assert.IsFalse(LogUploadChunkPlan.IsAcceptable(100_000, 512, LogUploadChunkPlan.ChunkCount(100_000, 512)));
            Assert.IsFalse(LogUploadChunkPlan.IsAcceptable(100_000, 65537, 2));
            Assert.IsFalse(LogUploadChunkPlan.IsAcceptable(100_000, 65536, 3), "chunk count must match the size");
            Assert.IsFalse(LogUploadChunkPlan.IsAcceptable(21, 65536, 1), "too small to be a zip");
        }

        #endregion

        #region Pacing

        private const int Chunk = 64 * 1024;
        private const int Rate = 256 * 1024;
        private const int Window = 64;

        [TestMethod]
        public void TheFirstChunkGoesAtOnceAndTheRateCapSpacesTheNext()
        {
            var pacer = new LogUploadPacer(Chunk, Rate);
            Assert.IsTrue(pacer.TrySend(0, 0, Window));
            Assert.IsFalse(pacer.TrySend(0, 0, Window), "no credit left");
            Assert.IsFalse(pacer.TrySend(249, 0, Window));
            Assert.IsTrue(pacer.TrySend(250, 0, Window), "64 KB at 256 KB/s is one chunk per 250 ms");
        }

        [TestMethod]
        public void ASlowLinkSlowsTheUploadDownBecauseTheWindowMustMostlyBeAcknowledged()
        {
            var pacer = new LogUploadPacer(Chunk, Rate);
            Assert.IsFalse(pacer.TrySend(10_000, Window / 4 + 1, Window), "plenty of credit, but the previous chunk is still in flight");
            Assert.IsTrue(pacer.TrySend(10_000, Window / 4, Window));
        }

        [TestMethod]
        public void AThreadThatWokeUpLateAfterAFreezeCatchesUpALittleButNeverFloods()
        {
            var pacer = new LogUploadPacer(Chunk, Rate, burstChunks: 2);
            Assert.IsTrue(pacer.TrySend(0, 0, Window));
            // The game froze for a minute.
            var sent = 0;
            for (var i = 0; i < 10; i++)
            {
                if (pacer.TrySend(60_000, 0, Window)) sent++;
            }

            Assert.AreEqual(2, sent, "the burst is capped at two chunks");
        }

        [TestMethod]
        public void NoConnectionNeverSends()
        {
            var pacer = new LogUploadPacer(Chunk, Rate);
            Assert.IsFalse(pacer.TrySend(100_000, -1, 0));
        }

        [TestMethod]
        public void OverALongRunTheRateStaysUnderTheCap()
        {
            var pacer = new LogUploadPacer(Chunk, Rate);
            long bytes = 0;
            for (long ms = 0; ms <= 20_000; ms += 10)
            {
                if (pacer.TrySend(ms, 0, Window)) bytes += Chunk;
            }

            Assert.IsTrue(bytes <= 20L * Rate + Chunk * 2, "sent " + bytes);
            Assert.IsTrue(bytes >= 18L * Rate, "and it does use the available rate: " + bytes);
        }

        #endregion

        #region Retry schedule

        [DataTestMethod]
        [DataRow(LogUploadRefusalReason.TimedOut, true)]
        [DataRow(LogUploadRefusalReason.Busy, true)]
        [DataRow(LogUploadRefusalReason.TooFrequent, true)]
        [DataRow(LogUploadRefusalReason.TooLarge, false)]
        [DataRow(LogUploadRefusalReason.TooMany, false)]
        [DataRow(LogUploadRefusalReason.BadFile, false)]
        [DataRow(LogUploadRefusalReason.NotAllowed, false)]
        [DataRow(LogUploadRefusalReason.Cancelled, false)]
        [DataRow(LogUploadRefusalReason.ServerError, false)]
        [DataRow(LogUploadRefusalReason.None, false)]
        public void OnlyTransientRefusalsAreRetried(LogUploadRefusalReason reason, bool expected)
        {
            Assert.AreEqual(expected, LogUploadRetry.IsTransient(reason));
        }

        [TestMethod]
        public void ARetryComesAfterTwoMinutesAndAnotherAfterTenThenItGivesUp()
        {
            var plan = new LogUploadRetryPlan();
            Assert.IsFalse(plan.Pending);

            Assert.AreEqual(TimeSpan.FromMinutes(2), plan.OnAttemptFailed(T0, true));
            Assert.IsTrue(plan.Pending);
            Assert.IsFalse(plan.IsDue(T0.AddSeconds(119)));
            Assert.IsTrue(plan.IsDue(T0.AddSeconds(120)));
            plan.MarkStarted();
            Assert.IsFalse(plan.Pending);
            Assert.AreEqual(1, plan.RetriesStarted);

            var later = T0.AddMinutes(5);
            Assert.AreEqual(TimeSpan.FromMinutes(10), plan.OnAttemptFailed(later, true));
            Assert.IsTrue(plan.IsDue(later.AddMinutes(10)));
            plan.MarkStarted();
            Assert.AreEqual(2, plan.RetriesStarted);

            Assert.IsNull(plan.OnAttemptFailed(later.AddMinutes(20), true), "two retries are the limit");
            Assert.IsFalse(plan.Pending);
        }

        [TestMethod]
        public void AFailureThatIsNotTransientNeverSchedulesAndAClickStartsOver()
        {
            var plan = new LogUploadRetryPlan();
            Assert.IsNull(plan.OnAttemptFailed(T0, false));
            Assert.IsFalse(plan.Pending);

            plan.OnAttemptFailed(T0, true);
            plan.MarkStarted();
            plan.OnAttemptFailed(T0, true);
            plan.Reset();
            Assert.IsFalse(plan.Pending);
            Assert.AreEqual(0, plan.RetriesStarted);
            Assert.AreEqual(TimeSpan.FromMinutes(2), plan.OnAttemptFailed(T0, true), "a new upload gets the full schedule again");
        }

        [TestMethod]
        public void ARetryThatCouldNotStartForAWhileIsDropped()
        {
            var plan = new LogUploadRetryPlan();
            plan.OnAttemptFailed(T0, true);
            var due = T0.AddMinutes(2);
            Assert.IsFalse(plan.HasExpired(due.AddMinutes(14)));
            Assert.IsTrue(plan.HasExpired(due.AddMinutes(16)));
            Assert.AreEqual(TimeSpan.FromSeconds(30), plan.TimeLeft(T0.AddSeconds(90)));
            Assert.AreEqual(TimeSpan.Zero, plan.TimeLeft(due.AddMinutes(1)));
        }

        [TestMethod]
        public void ARetryIsDroppedWhenAutomaticReportsWereTurnedOffOrTheServerChanged()
        {
            const string a = "203.0.113.5:6702";
            const string b = "203.0.113.9:6702";
            Assert.IsNull(LogUploadRetry.DropReason(true, true, a, a), "everything as before");
            Assert.IsNull(LogUploadRetry.DropReason(false, false, a, a), "a manual upload does not depend on the automatic switch");
            StringAssert.Contains(LogUploadRetry.DropReason(true, false, a, a), "turned off");
            StringAssert.Contains(LogUploadRetry.DropReason(false, true, a, b), "different server");
            StringAssert.Contains(LogUploadRetry.DropReason(true, true, a, string.Empty), "different server");
        }

        [TestMethod]
        public void ALostConnectionWhileWaitingForTheConfirmationIsNotRetried()
        {
            Assert.IsFalse(LogUploadRetry.CanRetryAfterConnectionLoss(waitingForConfirmation: true), "the server may already have stored it");
            Assert.IsTrue(LogUploadRetry.CanRetryAfterConnectionLoss(waitingForConfirmation: false));
        }

        [TestMethod]
        public void TheDelayIsWrittenInPlainWords()
        {
            Assert.AreEqual("2 minutes", LogUploadRetry.FormatDelay(TimeSpan.FromMinutes(2)));
            Assert.AreEqual("1 minute", LogUploadRetry.FormatDelay(TimeSpan.FromSeconds(60)));
            Assert.AreEqual("2 minutes", LogUploadRetry.FormatDelay(TimeSpan.FromSeconds(61)));
            Assert.AreEqual("45 seconds", LogUploadRetry.FormatDelay(TimeSpan.FromSeconds(45)));
            Assert.AreEqual("1 second", LogUploadRetry.FormatDelay(TimeSpan.Zero));
            Assert.AreEqual(new[] { 2.0, 10.0 }.Length, LogUploadRetry.Delays.Length);
            Assert.AreEqual(2, LogUploadRetry.MaxRetries);
            Assert.IsTrue(LogUploadRetry.Delays.Select(d => d.TotalMinutes).SequenceEqual(new[] { 2.0, 10.0 }));
        }

        #endregion
    }
}
