using LmpCommon.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class ClockCatchUpPolicyTests
    {
        private const float Baseline = 0.04f;
        private const float Tolerance = 1e-5f;

        private static ClockCatchUpDecision Decide(double errorSec, float frameSec, float cap, float baseline = Baseline, bool active = true) =>
            ClockCatchUpPolicy.Decide(errorSec, frameSec, cap, baseline, active);

        [TestMethod]
        public void RaisesWhenBehindAndCapBinding()
        {
            // The friend's case: 52 ms frames, 40 ms cap, 1.8 s behind.
            var d = Decide(-1.8, 0.052f, Baseline);
            Assert.AreEqual(ClockCatchUpReason.Raise, d.Reason);
            Assert.AreEqual(0.065f, d.Cap, Tolerance, "frame + 10 ms headroom, rounded up to 5 ms");
            Assert.IsTrue(d.Cap > 0.052f, "the raised cap must exceed the frame time or the game still falls behind");
        }

        [TestMethod]
        public void DoesNotRaiseWhenFramesTooSlowOrCapNotBinding()
        {
            Assert.AreEqual(ClockCatchUpReason.FrameTooSlow, Decide(-2.0, 0.095f, Baseline).Reason);
            Assert.AreEqual(Baseline, Decide(-2.0, 0.095f, Baseline).Cap, Tolerance);

            // 30 ms frames under a 40 ms cap: the game already runs at real time, the cap is not what holds it back.
            var notBinding = Decide(-2.0, 0.030f, Baseline);
            Assert.AreEqual(ClockCatchUpReason.NotBinding, notBinding.Reason);
            Assert.AreEqual(Baseline, notBinding.Cap, Tolerance);

            // A raised cap is dropped when frames get physics-bound (death spiral guard).
            var spiral = Decide(-2.0, 0.091f, 0.08f);
            Assert.AreEqual(ClockCatchUpReason.FrameTooSlow, spiral.Reason);
            Assert.AreEqual(Baseline, spiral.Cap, Tolerance);
        }

        [TestMethod]
        public void HysteresisHoldsRaisedCapBetweenThresholdsAndNeverRaisesInsideThem()
        {
            // Not raised yet, 0.2 s behind (between restore 0.1 and raise 0.3): stays at baseline.
            var notYet = Decide(-0.2, 0.052f, Baseline);
            Assert.AreEqual(Baseline, notYet.Cap, Tolerance);
            Assert.AreNotEqual(ClockCatchUpReason.Raise, notYet.Reason);

            // Raised to 0.065, the error wanders around the raise threshold and down to just above restore: no change.
            foreach (var error in new[] { -0.31, -0.29, -0.31, -0.2, -0.11 })
            {
                var d = Decide(error, 0.052f, 0.065f);
                Assert.AreEqual(0.065f, d.Cap, Tolerance, $"error {error}");
                Assert.AreEqual(ClockCatchUpReason.Hold, d.Reason, $"error {error}");
            }

            // A frame-time wobble inside the same 5 ms step does not move the cap either.
            Assert.AreEqual(0.065f, Decide(-1.0, 0.054f, 0.065f).Cap, Tolerance);
            // But a heavier frame that makes the raised cap binding again raises it one more step.
            Assert.AreEqual(0.070f, Decide(-1.0, 0.059f, 0.065f).Cap, Tolerance);
        }

        [TestMethod]
        public void RestoresWhenCaughtUpAheadOrInactive()
        {
            Assert.AreEqual(ClockCatchUpReason.CaughtUp, Decide(-0.09, 0.052f, 0.065f).Reason);
            Assert.AreEqual(Baseline, Decide(-0.09, 0.052f, 0.065f).Cap, Tolerance);

            Assert.AreEqual(ClockCatchUpReason.Ahead, Decide(0.5, 0.052f, 0.065f).Reason);
            Assert.AreEqual(Baseline, Decide(0.5, 0.052f, 0.065f).Cap, Tolerance);

            Assert.AreEqual(ClockCatchUpReason.Inactive, Decide(-2.0, 0.052f, 0.065f, active: false).Reason);
            Assert.AreEqual(Baseline, Decide(-2.0, 0.052f, 0.065f, active: false).Cap, Tolerance);
        }

        [TestMethod]
        public void ClampsToMaxAndNeverGoesBelowBaseline()
        {
            var high = Decide(-3.0, 0.085f, 0.075f);
            Assert.AreEqual(ClockCatchUpPolicy.MaxCapSec, high.Cap, Tolerance);

            // Already at the max: hold, never above it.
            var atMax = Decide(-3.0, 0.085f, ClockCatchUpPolicy.MaxCapSec);
            Assert.AreEqual(ClockCatchUpPolicy.MaxCapSec, atMax.Cap, Tolerance);
            Assert.AreEqual(ClockCatchUpReason.Hold, atMax.Reason);

            // A player with a 0.06 baseline and 55 ms frames: never below their own setting.
            var custom = Decide(-1.0, 0.055f, 0.06f, baseline: 0.06f);
            Assert.IsTrue(custom.Cap >= 0.06f - Tolerance);
            Assert.AreEqual(0.065f, custom.Cap, Tolerance);

            // Sweep: whatever the inputs, the cap stays within [baseline, max].
            foreach (var error in new[] { -5.0, -1.0, -0.35, -0.2, -0.05, 0.0, 1.0 })
            foreach (var frame in new[] { 0.01f, 0.037f, 0.05f, 0.075f, 0.089f, 0.2f })
            foreach (var cap in new[] { Baseline, 0.05f, 0.065f, 0.08f })
            {
                var d = Decide(error, frame, cap);
                Assert.IsTrue(d.Cap >= Baseline - Tolerance && d.Cap <= ClockCatchUpPolicy.MaxCapSec + Tolerance,
                    $"error {error} frame {frame} cap {cap} -> {d.Cap}");
            }
        }

        [TestMethod]
        public void BaselineAtOrAboveMaxIsNeverTouched()
        {
            foreach (var error in new[] { -3.0, -0.5, 0.0, 2.0 })
            {
                var d = Decide(error, 0.07f, 0.1f, baseline: 0.1f);
                Assert.AreEqual(ClockCatchUpReason.BaselineHigh, d.Reason);
                Assert.AreEqual(0.1f, d.Cap, Tolerance);
            }

            var exact = Decide(-3.0, 0.075f, 0.08f, baseline: 0.08f);
            Assert.AreEqual(ClockCatchUpReason.BaselineHigh, exact.Reason);
            Assert.AreEqual(0.08f, exact.Cap, Tolerance);
        }
    }
}
