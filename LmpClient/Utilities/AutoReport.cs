using LmpClient.Systems.LogUpload;
using LmpClient.Systems.SettingsSys;
using LmpCommon.LogUpload;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LmpClient.Utilities
{
    /// <summary>
    /// Decides when the client sends a diagnostics report to the host by itself, and starts it through
    /// <see cref="LogUploadSystem"/> (same authenticated connection, redaction and chunking as "Send logs to host").
    /// Triggers: the game really froze, a burst of errors, or the previous session ended without a clean exit. Reports are
    /// rare by design: at least 20 minutes apart and at most 8 a day (remembered across restarts), and the player can turn
    /// the whole thing off in the VMP options; the host can turn it off for the server (refused uploads are only logged).
    /// </summary>
    public static class AutoReport
    {
        private const float CheckIntervalSeconds = 5f;
        private const double LoadWindowSeconds = 25;
        private const float SevereStallSeconds = 8f;
        private const int RepeatedStalls = 3;
        private static readonly TimeSpan RepeatedStallWindow = TimeSpan.FromMinutes(5);
        private const int ErrorBurst = 300;
        private const int ExceptionBurst = 100;
        private const int MaxPerDay = 8;
        private const string TestVariable = "VMP_DIAG_TEST";

        private static float _nextCheck;
        private static List<DateTime> _sent;
        private static string _statePath;
        private static bool _testHandled;
        private static float _nextWaitingNote;

        public static void Start(string logsFolder)
        {
            _statePath = string.IsNullOrEmpty(logsFolder) ? null : Path.Combine(logsFolder, "auto-reports.txt");
        }

        /// <summary>Called once per frame from the main thread; does real work every few seconds.</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + CheckIntervalSeconds;

            try
            {
                Evaluate();
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[VMP]: Automatic report check failed: {e.Message}");
            }
        }

        private static void Evaluate()
        {
            // A report the host asked for (it may have arrived while the game was loading). It has its own conditions and none of the
            // limits below, so it is handled first; LogUploadSystem.TryStartPendingRequest explains why it starts or is dropped.
            var requestSystem = LogUploadSystem.Singleton;
            if (requestSystem != null && requestSystem.TryStartPendingRequest()) return;

            // Developer switch to test the whole pipeline: VMP_DIAG_TEST=stall|errors|crash|test (once per run, ignores the limits).
            var forced = ForcedReason();
            var waitingForTest = forced.HasValue && !_testHandled;

            if (!SettingsSystem.CurrentSettings.AutoSendDiagnostics)
            {
                NoteWaiting(waitingForTest, "automatic reports are turned off in the options");
                return;
            }

            var upload = LogUploadSystem.Singleton;
            if (upload == null || !upload.CanStartUpload)
            {
                NoteWaiting(waitingForTest, upload == null
                    ? "the upload system does not exist"
                    : $"cannot upload now (enabled={upload.Enabled}, network={MainSystem.NetworkState}, phase={upload.Phase})");
                return;
            }

            var scene = HighLogic.LoadedScene;
            if (scene == GameScenes.LOADING || scene == GameScenes.MAINMENU)
            {
                NoteWaiting(waitingForTest, $"scene is {scene}");
                return;
            }

            LogUploadReason reason;
            string trigger;

            if (waitingForTest)
            {
                _testHandled = true;
                reason = forced.Value;
                trigger = $"forced by the {TestVariable} environment variable";
                LunaLog.Log($"[VMP]: Sending a test diagnostics report ({reason})");
                upload.StartUpload(reason, trigger);
                return;
            }

            if (!MayReportNow()) return;

            if (SessionMarker.PreviousEndedUnexpectedly && !SessionMarker.CrashReportHandled)
            {
                SessionMarker.CrashReportHandled = true;
                reason = LogUploadReason.Crash;
                trigger = $"the previous session (started {SessionMarker.PreviousStartUtc}, build {SessionMarker.PreviousBuild}) did not exit cleanly";
            }
            else if (!TryStallTrigger(out reason, out trigger) && !TryErrorTrigger(out reason, out trigger))
            {
                return;
            }

            RecordSent();
            LunaLog.Log($"[VMP]: Sending an automatic diagnostics report to the host: {reason}, {trigger}");
            upload.StartUpload(reason, trigger);
        }

        /// <summary>Only while the developer test switch is set: say once in a while why the test report has not gone out yet.</summary>
        private static void NoteWaiting(bool waitingForTest, string why)
        {
            if (!waitingForTest || Time.unscaledTime < _nextWaitingNote) return;
            _nextWaitingNote = Time.unscaledTime + 30f;
            LunaLog.Log($"[VMP]: The {TestVariable} test report is waiting: {why}");
        }

        private static bool TryStallTrigger(out LogUploadReason reason, out string trigger)
        {
            reason = LogUploadReason.Stall;
            trigger = null;

            // A stall that starts within 25 s of a scene change is KSP loading the scene, not the game freezing.
            // Freezes while LOADING or in the main menu are KSP starting up (part compiling, textures), not gameplay.
            var stalls = ClientHealth.RecentStalls(RepeatedStallWindow, LoadWindowSeconds)
                .Where(s => s.Scene != GameScenes.LOADING && s.Scene != GameScenes.MAINMENU).ToList();
            var severe = stalls.FirstOrDefault(s => s.Seconds >= SevereStallSeconds);
            if (severe != null)
            {
                trigger = $"a {severe.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s freeze in {severe.Scene}";
                return true;
            }

            if (stalls.Count >= RepeatedStalls)
            {
                trigger = $"{stalls.Count} freezes of 1.5 s or more in 5 minutes ({stalls.Sum(s => s.Seconds).ToString("0", CultureInfo.InvariantCulture)} s in total)";
                return true;
            }

            return false;
        }

        private static bool TryErrorTrigger(out LogUploadReason reason, out string trigger)
        {
            reason = LogUploadReason.Errors;
            trigger = null;
            if (ClientHealth.SecondsSinceSceneChange < LoadWindowSeconds) return false;

            var errors = ClientHealth.TriggerErrorsThisMinute;
            var exceptions = ClientHealth.TriggerExceptionsThisMinute;
            if (errors >= ErrorBurst || exceptions >= ExceptionBurst)
            {
                trigger = $"{errors} errors and {exceptions} exceptions in the last minute";
                return true;
            }

            return false;
        }

        private static LogUploadReason? ForcedReason()
        {
            string value;
            try
            {
                value = Environment.GetEnvironmentVariable(TestVariable);
            }
            catch (Exception)
            {
                return null;
            }

            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "stall": return LogUploadReason.Stall;
                case "errors": return LogUploadReason.Errors;
                case "crash": return LogUploadReason.Crash;
                case "test": return LogUploadReason.Test;
                default: return null;
            }
        }

        #region Rate limit (survives restarts)

        private static bool MayReportNow()
        {
            LoadSent();
            var now = DateTime.UtcNow;
            _sent.RemoveAll(t => now - t >= TimeSpan.FromHours(24));
            if (_sent.Count >= MaxPerDay) return false;
            return _sent.Count == 0 || now - _sent.Max() >= LogUploadLimits.ClientMinTimeBetweenAutoReports;
        }

        private static void RecordSent()
        {
            LoadSent();
            _sent.Add(DateTime.UtcNow);
            try
            {
                if (!string.IsNullOrEmpty(_statePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_statePath));
                    File.WriteAllLines(_statePath, _sent.Select(t => t.Ticks.ToString(CultureInfo.InvariantCulture)).ToArray());
                }
            }
            catch (Exception)
            {
                // The limit still holds for this run.
            }
        }

        private static void LoadSent()
        {
            if (_sent != null) return;
            _sent = new List<DateTime>();
            try
            {
                if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath)) return;
                foreach (var line in File.ReadAllLines(_statePath))
                {
                    if (long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks)
                    {
                        _sent.Add(new DateTime(ticks, DateTimeKind.Utc));
                    }
                }
            }
            catch (Exception)
            {
                // Start with an empty history.
            }
        }

        #endregion
    }
}
