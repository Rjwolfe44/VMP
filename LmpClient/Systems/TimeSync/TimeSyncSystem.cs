using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Extensions;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.ShareContracts;
using LmpClient.Systems.Warp;
using LmpCommon.Sync;
using LmpCommon.Time;
using System;
using System.Globalization;
using UniLinq;
using UnityEngine;
using UnityEngine.Profiling;

namespace LmpClient.Systems.TimeSync
{
    /// <inheritdoc />
    /// <summary>
    /// This system syncs the game time with the UTC time.
    /// Your game time may shift, for example when you computer hangs or there are lots of vessels etc.
    /// Those "micro hangs" should be corrected so all the players are in the same time as the UTC
    /// </summary>
    public class TimeSyncSystem : System<TimeSyncSystem>
    {
        #region Fields & properties

        #region Public

        public TimeSyncEvents TimerSyncEvents { get; } = new TimeSyncEvents();

        public static long ServerStartTime { get; set; }

        private static double _universalTime;

        /// <summary>
        /// Thread safe way of accessing Planetarium.GetUniversalTime()
        /// </summary>
        public static double UniversalTime
        {
            get => MainSystem.IsUnityThread ? Planetarium.GetUniversalTime() : _universalTime;
            private set => _universalTime = value;
        }

        /// <summary>
        /// Gets the server clock in seconds.
        /// </summary>
        public static double ServerClockSec => TimeUtil.TicksToSeconds(LunaNetworkTime.UtcNow.Ticks - ServerStartTime);

        /// <summary>
        /// Gets the current time error between the server time and the game time
        /// </summary>
        public static double CurrentErrorSec => UniversalTime - WarpSystem.Singleton.CurrentSubspaceTime;

        /// <summary>Number of "Step clock sync" jumps since the last <see cref="TakeClockStats"/> (for the health line).</summary>
        private static int _stepSyncsWindow;

        /// <summary>Clock error (ms) seen by the last time check while time sync was running; NaN when it is not running.</summary>
        private static double _lastErrorMs = double.NaN;

        /// <summary>Returns the step syncs since the last call (and resets them) plus the last clock error in ms (NaN when time sync is not running).</summary>
        public static int TakeClockStats(out double lastErrorMs)
        {
            lastErrorMs = _lastErrorMs;
            var steps = _stepSyncsWindow;
            _stepSyncsWindow = 0;
            return steps;
        }

        #endregion

        #region Constants

        /// <summary>
        /// If the time between UTC and game is greater than this, the game time will be fixed using the phisics clock (makes game go faster/slower)
        /// </summary>
        private const int MinPhysicsClockMsError = 25;
        /// <summary>
        /// Minimum speed that the game can go
        /// </summary>
        private const float MinPhysicsClockRate = 0.85f;
        /// <summary>
        /// Max speed that the game can go. If you put this number too high the game will lag a lot.
        /// </summary>
        private const float MaxPhysicsClockRate = 1.20f;
        /// <summary>
        /// Limit at which we won't fix the time with the GAME timescale
        /// </summary>
        private const int MaxPhysicsClockMsError = 3500;
        /// <summary>Smoothing of the real frame time for the clock catch-up (per frame).</summary>
        private const float FrameEmaAlpha = 0.1f;
        /// <summary>Longer frames (load hitches) are counted as this long so one freeze does not dominate the average.</summary>
        private const float MaxFrameSampleSec = 0.2f;
        /// <summary>After the catch-up was undone because frames got physics-bound, wait this long before raising again.</summary>
        private const float CatchUpRetrySeconds = 30f;

        #endregion

        #region Private

        private static bool CurrentlyWarping => WarpSystem.Singleton.CurrentSubspace == -1;

        private float _frameEma;
        private GameScenes _catchUpScene;
        private bool _catchUpRaised;
        private float _catchUpCap;
        private float _catchUpBlockedUntil;

        private static bool CanSyncTime
        {
            get
            {
                switch (HighLogic.LoadedScene)
                {
                    case GameScenes.TRACKSTATION:
                    case GameScenes.FLIGHT:
                    case GameScenes.SPACECENTER:
                    case GameScenes.EDITOR:
                        return true;
                    default:
                        return false;
                }
            }
        }

        #endregion

        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(TimeSyncSystem);

        protected override void OnEnabled()
        {
            base.OnEnabled();

            //Refresh it at TimingManager.TimingStage.Precalc so it's updated JUST after KSP updates it's time
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Precalc, UpdateUniversalTime);

            //Uncomment to check if the TimingManager.TimingStage.Precalc is the correct time. All the operations should give "0"
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, ()=> LunaLog.Log($"ObscenelyEarly {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Early, () => LunaLog.Log($"Early {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Precalc, () => LunaLog.Log($"Precalc {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Earlyish, () => LunaLog.Log($"Earlyish {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Normal, () => LunaLog.Log($"Normal {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.FashionablyLate, () => LunaLog.Log($"FashionablyLate {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.FlightIntegrator, () => LunaLog.Log($"FlightIntegrator {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Late, () => LunaLog.Log($"Late {Planetarium.GetUniversalTime() - UniversalTime}"));
            //TimingManager.FixedUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, () => LunaLog.Log($"BetterLateThanNever {Planetarium.GetUniversalTime() - UniversalTime}"));

            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.Precalc, CheckGameTime);

            SpectateEvent.onStartSpectating.Add(TimerSyncEvents.OnStartSpectating);

            _frameEma = 0;
            _catchUpScene = HighLogic.LoadedScene;
            SetupRoutine(new RoutineDefinition(0, RoutineExecution.Update, UpdateClockCatchUp));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            TimingManager.FixedUpdateRemove(TimingManager.TimingStage.Precalc, UpdateUniversalTime);
            TimingManager.FixedUpdateRemove(TimingManager.TimingStage.Precalc, CheckGameTime);

            SpectateEvent.onStartSpectating.Remove(TimerSyncEvents.OnStartSpectating);
            ServerStartTime = 0;
            _lastErrorMs = double.NaN;
            RestoreCatchUpCap("time sync stopped");
        }

        #endregion

        #region Update methods

        /// <summary>
        /// Routine that checks our time against the server time and adjust it if needed.
        /// We only adjust the GAME time. And we will only do if the time error is between <see cref="MinPhysicsClockMsError"/> and <see cref="MaxPhysicsClockMsError"/>
        /// We cannot do it with more than <see cref="MaxPhysicsClockMsError"/> as then we would need a lot of time to catch up with the time error.
        /// For greater errors we just fix the time with the StepClock
        /// </summary>
        private void CheckGameTime()
        {
            Profiler.BeginSample(nameof(CheckGameTime));

            if (Enabled && !CurrentlyWarping && CanSyncTime && !WarpSystem.Singleton.WaitingSubspaceIdFromServer)
            {
                var targetTime = WarpSystem.Singleton.CurrentSubspaceTime;
                var currentError = TimeUtil.SecondsToMilliseconds(CurrentErrorSec);
                _lastErrorMs = currentError;

                if (Math.Abs(currentError) < MinPhysicsClockMsError)
                {
                    Time.timeScale = 1;
                }
                if (Math.Abs(currentError) > MinPhysicsClockMsError && Math.Abs(currentError) < MaxPhysicsClockMsError)
                {
                    //Time error is not so big so we can fix it adjusting the physics time
                    SkewClock();
                }
                else if (Math.Abs(currentError) > MaxPhysicsClockMsError)
                {
                    // |error| is in milliseconds. Step sync is normal after pauses, hitches, or subspace catch-up when
                    // drift exceeds MaxPhysicsClockMsError (physics nudge only works for smaller errors).
                    LunaLog.Log(
                        $"[VMP] Step clock sync: UT {UniversalTime:F3} -> {targetTime:F3} s (|clock error| {Math.Abs(currentError):F0} ms > {MaxPhysicsClockMsError} ms nudge range; not a fault).");
                    SetGameTime(targetTime);
                    _stepSyncsWindow++;
                }
            }
            else
            {
                _lastErrorMs = double.NaN;
            }

            Profiler.EndSample();
        }

        /// <summary>
        /// Clock catch-up (see <see cref="ClockCatchUpPolicy"/>): at a low frame rate KSP's PHYSICS_FRAME_DT_LIMIT
        /// (Time.maximumDeltaTime, 0.04 s by default) caps game time per frame, so the game falls behind the server and
        /// <see cref="CheckGameTime"/> keeps stepping the clock (a hitch). While the cap is what holds the game back, raise it a
        /// little (at most 0.08 s) and put it back once caught up. Time.timeScale skew and the step sync are unchanged.
        /// </summary>
        private void UpdateClockCatchUp()
        {
            var dt = Mathf.Min(Time.unscaledDeltaTime, MaxFrameSampleSec);
            _frameEma = _frameEma <= 0 ? dt : _frameEma + FrameEmaAlpha * (dt - _frameEma);

            var scene = HighLogic.LoadedScene;
            var sceneChanged = scene != _catchUpScene;
            _catchUpScene = scene;

            var active = SettingsSystem.CurrentSettings.ClockCatchUp && !sceneChanged && !CurrentlyWarping && CanSyncTime &&
                         !WarpSystem.Singleton.WaitingSubspaceIdFromServer && TimeWarp.CurrentRateIndex == 0 &&
                         !(HighLogic.LoadedSceneIsFlight && FlightDriver.Pause);
            var errorSec = active ? CurrentErrorSec : 0;
            var baseline = GameSettings.PHYSICS_FRAME_DT_LIMIT;
            var current = Time.maximumDeltaTime;
            // While raised, decide from our own value, so a reset of the cap by the game is simply re-applied below.
            var decision = ClockCatchUpPolicy.Decide(errorSec, _frameEma, _catchUpRaised ? _catchUpCap : current, baseline, active);

            if (decision.Reason == ClockCatchUpReason.BaselineHigh) return;
            // Only ever undo our own raise: a cap someone else set is left alone.
            if (!_catchUpRaised && decision.Reason != ClockCatchUpReason.Raise) return;
            if (decision.Reason == ClockCatchUpReason.Raise && Time.unscaledTime < _catchUpBlockedUntil) return;

            if (!Mathf.Approximately(decision.Cap, current))
                Time.maximumDeltaTime = decision.Cap;

            var raisedNow = decision.Cap > baseline + 1e-4f;
            if (!Mathf.Approximately(decision.Cap, _catchUpCap) || raisedNow != _catchUpRaised)
            {
                if (raisedNow)
                {
                    LunaLog.Log(string.Format(CultureInfo.InvariantCulture,
                        "[VMP] Clock catch-up: max physics step per frame {0:F3} -> {1:F3} s (game {2:F1} s behind, frame {3:F0} ms)",
                        _catchUpRaised ? _catchUpCap : current, decision.Cap, -errorSec, _frameEma * 1000));
                }
                else
                {
                    LunaLog.Log(string.Format(CultureInfo.InvariantCulture,
                        "[VMP] Clock catch-up: max physics step per frame restored to {0:F3} s ({1})", decision.Cap, DescribeRestore(decision.Reason, errorSec)));
                    // Physics-bound frames: do not try again straight away, or raising and restoring would alternate.
                    if (decision.Reason == ClockCatchUpReason.FrameTooSlow)
                        _catchUpBlockedUntil = Time.unscaledTime + CatchUpRetrySeconds;
                }
            }

            _catchUpCap = decision.Cap;
            _catchUpRaised = raisedNow;
        }

        private string DescribeRestore(ClockCatchUpReason reason, double errorSec)
        {
            switch (reason)
            {
                case ClockCatchUpReason.CaughtUp: return string.Format(CultureInfo.InvariantCulture, "caught up, {0:F2} s behind", -errorSec);
                case ClockCatchUpReason.Ahead: return "game ahead of the server";
                case ClockCatchUpReason.FrameTooSlow:
                    return string.Format(CultureInfo.InvariantCulture, "frame {0:F0} ms, physics-bound; retry in {1:F0} s", _frameEma * 1000, CatchUpRetrySeconds);
                default: return "time sync paused: warp, pause, scene change or option off";
            }
        }

        private void RestoreCatchUpCap(string why)
        {
            if (!_catchUpRaised) return;
            _catchUpRaised = false;
            var baseline = GameSettings.PHYSICS_FRAME_DT_LIMIT;
            Time.maximumDeltaTime = baseline;
            _catchUpCap = baseline;
            LunaLog.Log(string.Format(CultureInfo.InvariantCulture, "[VMP] Clock catch-up: max physics step per frame restored to {0:F3} s ({1})", baseline, why));
        }

        #endregion

        #region FixedUpdateMethods

        /// <summary>
        /// Updates the UniversalTime with the game time on every fixed update
        /// </summary>
        private static void UpdateUniversalTime()
        {
            UniversalTime = Planetarium.GetUniversalTime();
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Forces a time sync against the server time
        /// </summary>
        public void ForceTimeSync()
        {
            if (Enabled && !CurrentlyWarping && CanSyncTime && !WarpSystem.Singleton.WaitingSubspaceIdFromServer)
            {
                var targetTime = WarpSystem.Singleton.CurrentSubspaceTime;
                LunaLog.LogWarning($"FORCING a time sync from: {UniversalTime} to: {targetTime}. Error: {TimeUtil.SecondsToMilliseconds(CurrentErrorSec)}");
                SetGameTime(targetTime);
            }
        }


        /// <summary>
        /// Call this method to set a new time using the planetarium
        /// </summary>
        public void SetGameTime(double targetTick)
        {
            ShareContractsSystem.Singleton?.GuardStockContractGenerationAroundLargeUniversalTimeJump(targetTick);

            if (HighLogic.LoadedSceneIsFlight)
            {
                //As we are syncing to a new game time, we must advance all the ship positions and put them in the correct orbit "epoch"
                var vesselsToUpdate = LockSystem.LockQuery.GetAllUnloadedUpdateLocks(SettingsSystem.CurrentSettings.PlayerName)
                    .Select(l => FlightGlobals.FindVessel(l.VesselId))
                    .Where(v => v != null)
                    .ToList();

                foreach (var vessel in vesselsToUpdate)
                {
                    // One vessel that fails to move must not abort the time change: this runs while handling a warp
                    // subspace message, and an exception here disconnects the player from the server.
                    try
                    {
                        vessel.AdvanceShipPosition(targetTick);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.LogError($"[VMP] Could not advance vessel {vessel.id} to the new game time: {ex}");
                    }
                }
            }

            Planetarium.SetUniversalTime(targetTick);
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Here we adjust the GAME timescale and make the game goes faster or slower
        /// </summary>
        private static void SkewClock()
        {
            var timeWarpRate = (float)Math.Pow(2, -CurrentErrorSec);

            //Set the physics warp rate between the max and min allowed
            Time.timeScale = Mathf.Clamp(timeWarpRate, MinPhysicsClockRate, MaxPhysicsClockRate);
        }

        #endregion
    }
}
