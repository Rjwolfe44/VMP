using System;

namespace LmpCommon.Sync
{
    /// <summary>Why <see cref="ClockCatchUpPolicy.Decide"/> chose its cap.</summary>
    public enum ClockCatchUpReason
    {
        /// <summary>The player's own cap is already at or above <see cref="ClockCatchUpPolicy.MaxCapSec"/>: never touched.</summary>
        BaselineHigh,
        /// <summary>Time sync is not running (warp, pause, scene without time sync, scene change, option off).</summary>
        Inactive,
        /// <summary>The game is ahead of the server clock.</summary>
        Ahead,
        /// <summary>The game is less than <see cref="ClockCatchUpPolicy.RestoreBehindSec"/> behind.</summary>
        CaughtUp,
        /// <summary>Frames are so slow that physics dominates; more physics per frame would make it worse.</summary>
        FrameTooSlow,
        /// <summary>The cap is not what limits the game's speed, so raising it would not help.</summary>
        NotBinding,
        /// <summary>Raise the cap.</summary>
        Raise,
        /// <summary>Keep the raised cap (between the restore and raise thresholds, or already at the needed value).</summary>
        Hold,
    }

    public struct ClockCatchUpDecision
    {
        public float Cap;
        public ClockCatchUpReason Reason;

        public ClockCatchUpDecision(float cap, ClockCatchUpReason reason)
        {
            Cap = cap;
            Reason = reason;
        }
    }

    /// <summary>
    /// Decides Unity's Time.maximumDeltaTime (KSP setting PHYSICS_FRAME_DT_LIMIT, 0.04 s by default) so a client whose frame
    /// rate is low can catch up with the server clock. With 52 ms frames and a 40 ms cap the game only advances 40 ms per frame
    /// (77% of real time), so it falls behind until the step clock sync jumps it forward (a visible hitch). Time.timeScale
    /// skew cannot help while the cap is the limit. Pure: no Unity, no state; the client calls it once per frame.
    /// </summary>
    public static class ClockCatchUpPolicy
    {
        /// <summary>Never raise the cap above this (and never touch a player's cap that is already this high).</summary>
        public const float MaxCapSec = 0.08f;
        /// <summary>Raise only when the game is further behind the server than this.</summary>
        public const double RaiseBehindSec = 0.3;
        /// <summary>Restore the baseline once the game is less than this behind (hysteresis with <see cref="RaiseBehindSec"/>).</summary>
        public const double RestoreBehindSec = 0.1;
        /// <summary>The cap counts as binding when the smoothed frame time is above this fraction of it.</summary>
        public const float BindingFraction = 0.9f;
        /// <summary>Death-spiral guard: at or above this smoothed frame time the cap is not raised (and a raised cap is restored).</summary>
        public const float SpiralGuardFrameSec = 0.09f;
        /// <summary>Headroom over the frame time for the raised cap.</summary>
        public const float HeadroomSec = 0.01f;
        /// <summary>Raised caps are rounded up to this step so a wobbling frame time does not change the cap every frame.</summary>
        public const float StepSec = 0.005f;

        private const float Epsilon = 1e-4f;

        /// <param name="clockErrorSec">Game time minus server time; negative = the game is behind.</param>
        /// <param name="smoothedFrameSec">Smoothed unscaled (real) frame time.</param>
        /// <param name="currentCap">Time.maximumDeltaTime now.</param>
        /// <param name="baselineCap">The player's own cap (GameSettings.PHYSICS_FRAME_DT_LIMIT).</param>
        /// <param name="syncActive">False while warping, paused, on a scene change or in a scene without time sync.</param>
        public static ClockCatchUpDecision Decide(double clockErrorSec, float smoothedFrameSec, float currentCap, float baselineCap, bool syncActive)
        {
            if (baselineCap >= MaxCapSec - Epsilon) return new ClockCatchUpDecision(currentCap, ClockCatchUpReason.BaselineHigh);
            if (!syncActive) return new ClockCatchUpDecision(baselineCap, ClockCatchUpReason.Inactive);
            if (clockErrorSec > 0) return new ClockCatchUpDecision(baselineCap, ClockCatchUpReason.Ahead);

            var behind = -clockErrorSec;
            if (behind < RestoreBehindSec) return new ClockCatchUpDecision(baselineCap, ClockCatchUpReason.CaughtUp);
            if (smoothedFrameSec >= SpiralGuardFrameSec) return new ClockCatchUpDecision(baselineCap, ClockCatchUpReason.FrameTooSlow);

            var raised = currentCap > baselineCap + Epsilon;
            if (behind > RaiseBehindSec && smoothedFrameSec > BindingFraction * currentCap)
            {
                var target = Clamp(RoundUp(smoothedFrameSec + HeadroomSec), baselineCap, MaxCapSec);
                if (target > currentCap + Epsilon) return new ClockCatchUpDecision(target, ClockCatchUpReason.Raise);
                return new ClockCatchUpDecision(raised ? Math.Min(currentCap, MaxCapSec) : baselineCap, ClockCatchUpReason.Hold);
            }

            return raised
                ? new ClockCatchUpDecision(Math.Min(currentCap, MaxCapSec), ClockCatchUpReason.Hold)
                : new ClockCatchUpDecision(baselineCap, ClockCatchUpReason.NotBinding);
        }

        private static float RoundUp(float seconds) => (float)(Math.Ceiling(seconds / StepSec - 1e-3) * StepSec);

        private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
    }
}
