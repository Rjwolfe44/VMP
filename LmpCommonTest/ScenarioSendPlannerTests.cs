using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LmpCommon.Scenario;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    /// <summary>
    /// The client's scenario upload (LmpClient ScenarioSystem) used static lists shared by every send, while sends run on
    /// background tasks and are started from several places. These tests pin the replacement: each send owns its batch,
    /// "already sent" is recorded only after sending, and only one send runs at a time.
    /// </summary>
    [TestClass]
    public class ScenarioSendPlannerTests
    {
        private static KeyValuePair<string, byte[]> Module(string name, string text) =>
            new KeyValuePair<string, byte[]>(name, Encoding.UTF8.GetBytes(name + ":" + text));

        [TestMethod]
        public void OverlappingSends_NeverPairANameWithAnotherModulesBytes()
        {
            //Many sends at once, each with its own modules, as a 30 s send and a flush overlapping on a big universe.
            //With lists shared between sends this mixes names and bytes or throws "collection was modified".
            var hashes = new ConcurrentDictionary<string, string>();
            var errors = new ConcurrentBag<string>();
            Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var modules = Enumerable.Range(0, 5).Select(m => Module("M" + (i * 5 + m), "v" + i)).ToList();
                var batch = ScenarioSendPlanner.Plan(modules, hashes);
                for (var k = 0; k < batch.Count; k++)
                {
                    if (!Encoding.UTF8.GetString(batch.Data[k]).StartsWith(batch.Names[k] + ":"))
                        errors.Add(batch.Names[k]);
                }
                if (batch.Count != 5)
                    errors.Add("count " + batch.Count);
            });
            Assert.AreEqual(0, errors.Count, string.Join(", ", errors.Take(5)));
        }

        [TestMethod]
        public void UnchangedModulesAreSkipped_AndOnlyACommittedSendCounts()
        {
            var hashes = new Dictionary<string, string>();
            var first = ScenarioSendPlanner.Plan(new[] { Module("Funding", "1"), Module("KK", "a") }, hashes);
            Assert.AreEqual(2, first.Count);
            Assert.AreEqual(0, hashes.Count, "Planning alone must not mark anything as sent.");

            //The send failed (exception before the sender): nothing committed, so the next send carries both again.
            var retry = ScenarioSendPlanner.Plan(new[] { Module("Funding", "1"), Module("KK", "a") }, hashes);
            Assert.AreEqual(2, retry.Count);
            ScenarioSendPlanner.Commit(retry, hashes);

            var next = ScenarioSendPlanner.Plan(new[] { Module("Funding", "1"), Module("KK", "b") }, hashes);
            CollectionAssert.AreEqual(new[] { "KK" }, next.Names);
        }

        [TestMethod]
        public void EmptyDataIsReportedAndSkipped()
        {
            var reported = new List<string>();
            var batch = ScenarioSendPlanner.Plan(new[] { new KeyValuePair<string, byte[]>("Empty", new byte[0]), Module("Ok", "1") }, null, reported.Add);
            CollectionAssert.AreEqual(new[] { "Ok" }, batch.Names);
            CollectionAssert.AreEqual(new[] { "Empty" }, reported);
        }

        [TestMethod]
        public void OnlyOneSendAtATime_AFullSendAskedMeanwhileRunsAtTheNextTick()
        {
            var c = new ScenarioSendCoordinator();
            Assert.IsTrue(c.TryBeginFull());
            Assert.IsFalse(c.TryBeginFull(), "A second full send while one runs must not start.");
            Assert.IsTrue(c.FullPending);
            Assert.AreEqual(ScenarioSendCoordinator.TickAction.None, c.Tick(() => true));

            c.End();
            Assert.AreEqual(ScenarioSendCoordinator.TickAction.Full, c.Tick(() => true), "The waiting full send goes first.");
            Assert.IsTrue(c.IsBusy);
            c.End();
            Assert.IsFalse(c.FullPending);
        }

        [TestMethod]
        public void SingleModuleFlush_IsNotTakenWhileASendRuns_AndIsRetried()
        {
            var c = new ScenarioSendCoordinator();
            var requested = true;
            var taken = 0;
            bool Take()
            {
                if (!requested) return false;
                requested = false;
                taken++;
                return true;
            }

            Assert.IsTrue(c.TryBeginFull());
            Assert.AreEqual(ScenarioSendCoordinator.TickAction.None, c.Tick(Take));
            Assert.AreEqual(0, taken, "The flush request must stay pending while another send runs.");

            c.End();
            Assert.AreEqual(ScenarioSendCoordinator.TickAction.SingleModule, c.Tick(Take));
            Assert.AreEqual(1, taken);
            c.End();

            Assert.AreEqual(ScenarioSendCoordinator.TickAction.None, c.Tick(Take));
            Assert.IsFalse(c.IsBusy, "A tick with nothing to do releases the send slot.");
        }

        [TestMethod]
        public void ConcurrentBegins_OnlyOneWins()
        {
            var c = new ScenarioSendCoordinator();
            var winners = 0;
            using (var start = new ManualResetEventSlim(false))
            {
                var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    if (c.TryBeginFull())
                        Interlocked.Increment(ref winners);
                })).ToArray();
                start.Set();
                Task.WaitAll(tasks);
            }
            Assert.AreEqual(1, winners);
        }
    }
}
