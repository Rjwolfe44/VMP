using LmpCommon.PersistentSync;
using LmpCommon.Stats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerTest
{
    [TestClass]
    public class AdminTrafficWindowTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        private static TrafficSample Sample(int minute, long sentBytes, long sentMessages = 0, long receivedBytes = 0, int extraSeconds = 0)
        {
            var sample = new TrafficSample { TimeUtc = T0.AddMinutes(minute).AddSeconds(extraSeconds) };
            if (sentBytes > 0 || sentMessages > 0) sample.Sent["PositionMsgData"] = new[] { sentMessages, sentBytes };
            if (receivedBytes > 0) sample.Received["FlightStateMsgData"] = new[] { receivedBytes / 100, receivedBytes };
            return sample;
        }

        [TestMethod]
        public void FirstSample_OnlySetsTheBaseline()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(Sample(0, 0));

            Assert.AreEqual(0, ring.Count);
            Assert.IsNull(ring.Latest());
            Assert.IsNull(ring.Combine(10));
            Assert.AreEqual(T0, ring.LastSampleUtc);
        }

        [TestMethod]
        public void OneWindow_HasTheRightTotalsAndRates()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(Sample(0, 0));
            ring.Add(Sample(1, sentBytes: 60000, sentMessages: 600, receivedBytes: 3000));

            var window = ring.Latest();
            Assert.AreEqual(60.0, window.Seconds, 0.0001);
            Assert.AreEqual(T0, window.StartUtc);
            Assert.AreEqual(T0.AddMinutes(1), window.EndUtc);
            Assert.AreEqual(60000, window.SentBytes);
            Assert.AreEqual(600, window.SentMessages);
            Assert.AreEqual(1000.0, window.SentBytesPerSecond, 0.0001);
            Assert.AreEqual(3000, window.ReceivedBytes);
            Assert.AreEqual(50.0, window.ReceivedBytesPerSecond, 0.0001);
            Assert.AreEqual("PositionMsgData", window.Sent.Single().Name);
        }

        [TestMethod]
        public void Windows_AreDifferencesNotRunningTotals()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(Sample(0, 0));
            ring.Add(Sample(1, 1000, 10));
            ring.Add(Sample(2, 1500, 15));
            ring.Add(Sample(3, 1500, 15));

            var windows = ring.Windows();
            CollectionAssert.AreEqual(new long[] { 1000, 500, 0 }, windows.Select(w => w.SentBytes).ToArray());
            Assert.AreEqual(0, windows[2].Sent.Count, "an idle minute has no rows");
            Assert.AreEqual(0.0, windows[2].SentBytesPerSecond, 0.0001);
        }

        [TestMethod]
        public void Ring_DropsTheOldestWindows_AndCombineSumsWhatIsLeft()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(Sample(0, 0));
            for (var minute = 1; minute <= 13; minute++) ring.Add(Sample(minute, minute * 1000L, minute));

            Assert.AreEqual(10, ring.Count);
            var windows = ring.Windows();
            Assert.AreEqual(10, windows.Count);
            Assert.AreEqual(T0.AddMinutes(4), windows[0].EndUtc, "windows 1-3 rolled out");
            Assert.AreEqual(T0.AddMinutes(13), windows[9].EndUtc);
            Assert.AreEqual(T0.AddMinutes(13), ring.Latest().EndUtc);

            var tenMinutes = ring.Combine(10);
            Assert.AreEqual(10000, tenMinutes.SentBytes, "1000 bytes in each of the ten windows left");
            Assert.AreEqual(600.0, tenMinutes.Seconds, 0.0001);
            Assert.AreEqual(T0.AddMinutes(3), tenMinutes.StartUtc);
            Assert.AreEqual(T0.AddMinutes(13), tenMinutes.EndUtc);
            Assert.AreEqual(1000.0 / 60.0 * 10 / 10, tenMinutes.SentBytesPerSecond, 0.0001);

            var threeMinutes = ring.Combine(3);
            Assert.AreEqual(3000, threeMinutes.SentBytes);
            Assert.AreEqual(180.0, threeMinutes.Seconds, 0.0001);

            Assert.AreEqual(10000, ring.Combine(500).SentBytes, "asking for more than there is just gives everything");
        }

        [TestMethod]
        public void Combine_MergesTypesAcrossWindows_BiggestFirst()
        {
            var ring = new TrafficWindowRing(10);
            ring.Add(new TrafficSample { TimeUtc = T0 });

            var a = new TrafficSample { TimeUtc = T0.AddMinutes(1) };
            a.Sent["AMsgData"] = new long[] { 5, 500 };
            a.Sent["BMsgData"] = new long[] { 1, 9000 };
            ring.Add(a);

            var b = new TrafficSample { TimeUtc = T0.AddMinutes(2) };
            b.Sent["AMsgData"] = new long[] { 5 + 7, 500 + 700 };
            b.Sent["BMsgData"] = new long[] { 1, 9000 };
            b.Sent["CMsgData"] = new long[] { 2, 20 };
            ring.Add(b);

            var sum = ring.Combine(10);
            CollectionAssert.AreEqual(new[] { "BMsgData", "AMsgData", "CMsgData" }, sum.Sent.Select(r => r.Name).ToArray());
            Assert.AreEqual(9000, sum.Sent[0].Bytes);
            Assert.AreEqual(1200, sum.Sent[1].Bytes);
            Assert.AreEqual(12, sum.Sent[1].Messages);
            Assert.AreEqual(20, sum.Sent[2].Bytes);
        }

        [TestMethod]
        public void SnapshotCompressionTable_ComesFromTheDifferences()
        {
            var ring = new TrafficWindowRing(10);
            var start = new TrafficSample { TimeUtc = T0 };
            start.Snapshots[PersistentSyncDomainId.Contracts] = new long[] { 2, 100000, 20000, 1, 0, 1, 0 };
            ring.Add(start);

            var end = new TrafficSample { TimeUtc = T0.AddMinutes(1) };
            // Domain seen before: 10 more snapshots, 1,000,000 raw -> 50,000 on the wire, 9 with a dictionary and 1 plain zstd.
            end.Snapshots[PersistentSyncDomainId.Contracts] = new long[] { 12, 1100000, 70000, 1, 0, 2, 9 };
            // Domain that was not there in the first sample.
            end.Snapshots[PersistentSyncDomainId.Funds] = new long[] { 3, 300, 300, 3, 0, 0, 0 };
            ring.Add(end);

            var window = ring.Latest();
            Assert.AreEqual(2, window.Snapshots.Count);
            var contracts = window.Snapshots[0];
            Assert.AreEqual(PersistentSyncDomainId.Contracts, contracts.Domain, "biggest raw size first");
            Assert.AreEqual(10, contracts.Snapshots);
            Assert.AreEqual(1000000, contracts.RawBytes);
            Assert.AreEqual(50000, contracts.WireBytes);
            Assert.AreEqual(20.0, contracts.Ratio, 0.0001);
            Assert.AreEqual(0, contracts.Raw);
            Assert.AreEqual(1, contracts.Zstd);
            Assert.AreEqual(9, contracts.ZstdDictionary);

            var funds = window.Snapshots[1];
            Assert.AreEqual(1.0, funds.Ratio, 0.0001);
            Assert.AreEqual(3, funds.Raw);
        }

        [TestMethod]
        public void CountersThatGoBackwardsCountAsZero_AndOddIntervalsKeepTheirLength()
        {
            var ring = new TrafficWindowRing(3);
            ring.Add(Sample(0, 5000, 50));
            ring.Add(Sample(1, 100, 1, extraSeconds: 30));

            var window = ring.Latest();
            Assert.AreEqual(90.0, window.Seconds, 0.0001, "the real length is reported, not a nominal minute");
            Assert.AreEqual(0, window.SentBytes);
            Assert.AreEqual(0, window.Sent.Count);
        }

        [TestMethod]
        public void SinceStart_UsesAZeroBaseline()
        {
            var zero = new TrafficSample { TimeUtc = T0 };
            var now = Sample(5, 5000, 50, 700);
            var total = TrafficWindowRing.Delta(zero, now);

            Assert.AreEqual(300.0, total.Seconds, 0.0001);
            Assert.AreEqual(5000, total.SentBytes);
            Assert.AreEqual(700, total.ReceivedBytes);
            Assert.AreEqual(16.6667, total.SentBytesPerSecond, 0.001);
        }

        [TestMethod]
        public void StaticStats_KeepTheirOldApi_AndExposeAReport()
        {
            var type = "AdminTest" + Guid.NewGuid().ToString("N") + "MsgData";
            VmpTrafficStats.RecordSent(type, 120);
            VmpTrafficStats.RecordSent(type, 80);
            VmpTrafficStats.RecordReceived(type, 30);

            var sent = VmpTrafficStats.GetSentTotals().Single(r => r.Name == type);
            Assert.AreEqual(2, sent.Messages);
            Assert.AreEqual(200, sent.Bytes);
            Assert.AreEqual(30, VmpTrafficStats.GetReceivedTotals().Single(r => r.Name == type).Bytes);

            var report = VmpTrafficStats.GetReport(DateTime.UtcNow);
            var row = report.SinceStart.Sent.Single(r => r.Name == type);
            Assert.AreEqual(200, row.Bytes);
            Assert.AreEqual(2, row.Messages);
            Assert.IsTrue(report.SinceStart.Seconds >= 0);
            Assert.AreEqual(VmpTrafficStats.StartedUtc, report.StartedUtc);

            var summary = VmpTrafficStats.TakeWindowSummary();
            if (summary != null) StringAssert.StartsWith(summary, "[VMP Traffic ");

            var sample = VmpTrafficStats.CaptureSample(DateTime.UtcNow);
            CollectionAssert.AreEqual(new long[] { 2, 200 }, sample.Sent[type]);
        }

        [TestMethod]
        public void NetworkJson_HasRatesRatiosAndAtMost25Types()
        {
            var ring = new TrafficWindowRing(10);
            var first = new TrafficSample { TimeUtc = T0 };
            ring.Add(first);
            var second = new TrafficSample { TimeUtc = T0.AddMinutes(1) };
            for (var i = 0; i < 30; i++) second.Sent["Type" + i + "MsgData"] = new long[] { 1, 100 + i };
            second.Received["InMsgData"] = new long[] { 6, 600 };
            second.Snapshots[PersistentSyncDomainId.Contracts] = new long[] { 4, 4000, 400, 0, 0, 0, 4 };
            ring.Add(second);

            var report = new TrafficReport
            {
                NowUtc = T0.AddMinutes(1),
                StartedUtc = T0,
                WindowCount = ring.Count,
                LastMinute = ring.Latest(),
                LastTenMinutes = ring.Combine(10),
                SinceStart = TrafficWindowRing.Delta(first, second),
            };

            var network = AdminApiMapper.ToNetwork(report);
            Assert.AreEqual(1, network.WindowsRecorded);
            Assert.AreEqual(60.0, network.UptimeSeconds, 0.0001);
            Assert.AreEqual(25, network.LastMinute.Out.Types.Count);
            Assert.AreEqual("Type29MsgData", network.LastMinute.Out.Types[0].Type, "biggest first");
            Assert.AreEqual(30, network.Totals.SentMessages);
            Assert.AreEqual(600, network.Totals.ReceivedBytes);
            Assert.AreEqual(10.0, network.LastMinute.In.BytesPerSecond, 0.0001);
            Assert.AreEqual(10.0, network.SnapshotCompression[0].Ratio, 0.0001);
            Assert.AreEqual("Contracts", network.SnapshotCompression[0].Domain);
            Assert.AreEqual(4, network.SnapshotCompression[0].Encodings.ZstdDictionary);

            var empty = AdminApiMapper.ToNetwork(new TrafficReport { NowUtc = T0, StartedUtc = T0, SinceStart = new TrafficWindow() });
            Assert.IsNull(empty.LastMinute);
            Assert.IsNull(empty.LastTenMinutes);
        }
    }
}
