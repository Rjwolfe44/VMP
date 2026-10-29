using LmpCommon.LogUpload;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LmpClient.Utilities
{
    /// <summary>
    /// What the client knows about its own health, for the log and for automatic reports to the host.
    /// <list type="bullet">
    /// <item>One "[VMP]: Health 60s ..." line per minute in KSP.log (frame rate, stalls, errors, garbage collections, memory),
    /// also appended as JSON to GameData/VladMultiplayer/logs/health.jsonl so it travels in every log bundle.</item>
    /// <item>A counter of errors/exceptions by signature (from Unity's log callback, any thread).</item>
    /// <item>The recent frame stalls, for <see cref="AutoReport"/> and the report summary.</item>
    /// </list>
    /// Everything here must stay cheap and must never throw into the game.
    /// </summary>
    public static class ClientHealth
    {
        private const float WindowSeconds = 60f;
        private const int MaxFrameSamples = 30000;
        private const int MaxSignatures = 400;
        private const string OtherSignature = "(other)";
        private const int SignatureBudgetPerWindow = 2000;
        private const int MaxStalls = 60;
        private const int MaxRows = 30;
        private const long HealthFileMaxBytes = 256 * 1024;
        private const int HealthFileKeepLines = 600;

        public sealed class StallEvent
        {
            public DateTime Utc;
            public float Seconds;
            public GameScenes Scene;
            public double SecondsSinceSceneChange;
        }

        private static readonly object StateLock = new object();
        private static readonly List<float> FrameTimes = new List<float>(8192);
        private static readonly List<StallEvent> Stalls = new List<StallEvent>();
        private static readonly LinkedList<string> Rows = new LinkedList<string>();
        private static readonly ConcurrentDictionary<string, int> Signatures = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        // Written from the log callback thread.
        private static int _errorsWindow;
        private static int _exceptionsWindow;
        // What AutoReport looks at: not known noise, and counted from the last scene change (a scene load is full of errors).
        private static int _triggerErrorsWindow;
        private static int _triggerExceptionsWindow;
        private static int _signatureBudget;
        private static int _totalErrors;
        private static int _totalExceptions;

        private static float _windowStart;
        private static int _frames;
        private static double _sumDt;
        private static float _maxDt;
        private static int _stallsWindow;
        private static int _sceneChangesWindow;
        private static GameScenes _lastScene = GameScenes.LOADING;
        private static float _lastSceneChangeTime;
        private static int _gc0, _gc1, _gc2;
        private static uint _lastPageFaults;
        private static bool _started;
        private static string _healthFilePath;
        private static DateTime _sessionStartUtc;

        /// <summary>Seconds (real time) since the scene last changed; scene loads cause long frames that are not a problem.</summary>
        public static double SecondsSinceSceneChange => Time.unscaledTime - _lastSceneChangeTime;

        /// <summary>Errors in the current minute that are not known scene-load noise (textures, materials).</summary>
        public static int TriggerErrorsThisMinute => Volatile.Read(ref _triggerErrorsWindow);

        public static int TriggerExceptionsThisMinute => Volatile.Read(ref _triggerExceptionsWindow);

        public static int TotalErrors => Volatile.Read(ref _totalErrors);

        public static int TotalExceptions => Volatile.Read(ref _totalExceptions);

        public static void Start(string logsFolder)
        {
            if (_started) return;
            _started = true;

            try
            {
                _sessionStartUtc = DateTime.UtcNow;
                _windowStart = Time.unscaledTime;
                _lastSceneChangeTime = Time.unscaledTime;
                _gc0 = GC.CollectionCount(0);
                _gc1 = GC.CollectionCount(1);
                _gc2 = GC.CollectionCount(2);
                _healthFilePath = string.IsNullOrEmpty(logsFolder) ? null : Path.Combine(logsFolder, "health.jsonl");
                Application.logMessageReceivedThreaded += OnLogMessage;
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[VMP]: Health monitor could not start: {e.Message}");
            }
        }

        /// <summary>Called once per frame from the main thread.</summary>
        public static void OnFrame()
        {
            if (!_started) return;

            try
            {
                var dt = Time.unscaledDeltaTime;
                _frames++;
                _sumDt += dt;
                if (dt > _maxDt) _maxDt = dt;
                if (FrameTimes.Count < MaxFrameSamples) FrameTimes.Add(dt);

                var scene = HighLogic.LoadedScene;
                if (scene != _lastScene)
                {
                    _lastScene = scene;
                    _sceneChangesWindow++;
                    _lastSceneChangeTime = Time.unscaledTime;
                    Interlocked.Exchange(ref _triggerErrorsWindow, 0);
                    Interlocked.Exchange(ref _triggerExceptionsWindow, 0);
                }

                if (Time.unscaledTime - _windowStart >= WindowSeconds)
                {
                    FlushWindow();
                }
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[VMP]: Health monitor error: {e.Message}");
            }
        }

        /// <summary>Records a frame that took longer than <see cref="StallWatch.FrameStallSeconds"/>.</summary>
        public static void NoteFrameStall(float seconds)
        {
            lock (StateLock)
            {
                _stallsWindow++;
                Stalls.Add(new StallEvent
                {
                    Utc = DateTime.UtcNow,
                    Seconds = seconds,
                    Scene = HighLogic.LoadedScene,
                    SecondsSinceSceneChange = SecondsSinceSceneChange,
                });
                if (Stalls.Count > MaxStalls) Stalls.RemoveAt(0);
            }
        }

        /// <summary>Stalls from the last <paramref name="window"/> that did not start right after a scene change.</summary>
        public static List<StallEvent> RecentStalls(TimeSpan window, double minSecondsAfterSceneChange)
        {
            var since = DateTime.UtcNow - window;
            lock (StateLock)
            {
                return Stalls.Where(s => s.Utc >= since && s.SecondsSinceSceneChange >= minSecondsAfterSceneChange).ToList();
            }
        }

        #region Error counting (log callback thread)

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // Must never log from here (that would recurse) and must stay cheap: a flood is tens of thousands of lines.
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;

            if (type == LogType.Exception)
            {
                Interlocked.Increment(ref _exceptionsWindow);
                Interlocked.Increment(ref _totalExceptions);
            }
            else
            {
                Interlocked.Increment(ref _errorsWindow);
                Interlocked.Increment(ref _totalErrors);
            }

            if (!IsKnownNoise(condition))
            {
                if (type == LogType.Exception)
                    Interlocked.Increment(ref _triggerExceptionsWindow);
                else
                    Interlocked.Increment(ref _triggerErrorsWindow);
            }

            if (Interlocked.Increment(ref _signatureBudget) > SignatureBudgetPerWindow) return;

            var signature = LogSignature.Normalize(condition);
            if (signature.Length == 0) signature = "(empty)";
            if (Signatures.Count >= MaxSignatures && !Signatures.ContainsKey(signature)) signature = OtherSignature;
            Signatures.AddOrUpdate(signature, 1, (_, count) => count + 1);
        }

        /// <summary>High-volume messages that scene loads always produce and that mean nothing for a freeze.</summary>
        private static bool IsKnownNoise(string condition)
        {
            if (string.IsNullOrEmpty(condition)) return false;
            return condition.StartsWith("Material doesn't have a texture property", StringComparison.Ordinal) ||
                   condition.StartsWith("Texture '", StringComparison.Ordinal);
        }

        #endregion

        #region Minute window

        private static void FlushWindow()
        {
            var elapsed = Math.Max(0.001, Time.unscaledTime - _windowStart);
            var frames = Math.Max(1, _frames);
            var avgFps = frames / Math.Max(0.001, _sumDt);
            var minFps = _maxDt > 0 ? 1.0 / _maxDt : 0;
            var p1Fps = 0.0;
            if (FrameTimes.Count > 0)
            {
                FrameTimes.Sort();
                var index = Math.Min(FrameTimes.Count - 1, (int)(0.99 * (FrameTimes.Count - 1)));
                p1Fps = FrameTimes[index] > 0 ? 1.0 / FrameTimes[index] : 0;
            }

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            var memory = StallWatch.GetMemory();
            var pageFaultsDelta = memory.HasProcess && _lastPageFaults != 0 && memory.PageFaults >= _lastPageFaults ? memory.PageFaults - _lastPageFaults : 0;
            if (memory.HasProcess) _lastPageFaults = memory.PageFaults;

            int stalls;
            int sceneChanges;
            lock (StateLock)
            {
                stalls = _stallsWindow;
                sceneChanges = _sceneChangesWindow;
                _stallsWindow = 0;
                _sceneChangesWindow = 0;
            }

            var errors = Interlocked.Exchange(ref _errorsWindow, 0);
            var exceptions = Interlocked.Exchange(ref _exceptionsWindow, 0);
            Interlocked.Exchange(ref _triggerErrorsWindow, 0);
            Interlocked.Exchange(ref _triggerExceptionsWindow, 0);
            Interlocked.Exchange(ref _signatureBudget, 0);

            var inv = CultureInfo.InvariantCulture;
            var line = string.Format(inv,
                "[VMP]: Health {0:F0}s scene={1} fps={2:F0}/{3:F0}/{4:F0} stalls={5} maxFrameMs={6:F0} errors={7} exceptions={8} " +
                "gc0={9} gc1={10} gc2={11} managedMB={12} workingSetMB={13} privateMB={14} ramFreeMB={15} pageFaultsDelta={16} sceneChanges={17}",
                elapsed, _lastScene, avgFps, minFps, p1Fps, stalls, _maxDt * 1000, errors, exceptions,
                gc0 - _gc0, gc1 - _gc1, gc2 - _gc2, memory.ManagedHeapMb, memory.WorkingSetMb, memory.PrivateMb, memory.RamFreeMb,
                pageFaultsDelta, sceneChanges);
            LunaLog.Log(line);

            var json = string.Format(inv,
                "{{\"t\":\"{0:yyyy-MM-dd'T'HH:mm:ss'Z'}\",\"scene\":\"{1}\",\"fps\":[{2:F0},{3:F0},{4:F0}],\"stalls\":{5},\"maxFrameMs\":{6:F0}," +
                "\"errors\":{7},\"exceptions\":{8},\"gc\":[{9},{10},{11}],\"managedMB\":{12},\"workingSetMB\":{13},\"privateMB\":{14}," +
                "\"ramFreeMB\":{15},\"pageFaultsDelta\":{16},\"sceneChanges\":{17}}}",
                DateTime.UtcNow, _lastScene, avgFps, minFps, p1Fps, stalls, _maxDt * 1000, errors, exceptions,
                gc0 - _gc0, gc1 - _gc1, gc2 - _gc2, memory.ManagedHeapMb, memory.WorkingSetMb, memory.PrivateMb, memory.RamFreeMb,
                pageFaultsDelta, sceneChanges);
            lock (StateLock)
            {
                Rows.AddLast(line.Substring("[VMP]: ".Length));
                while (Rows.Count > MaxRows) Rows.RemoveFirst();
            }

            AppendHealthFile(json);

            _gc0 = gc0;
            _gc1 = gc1;
            _gc2 = gc2;
            _windowStart = Time.unscaledTime;
            _frames = 0;
            _sumDt = 0;
            _maxDt = 0;
            FrameTimes.Clear();
        }

        private static void AppendHealthFile(string jsonLine)
        {
            if (string.IsNullOrEmpty(_healthFilePath)) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_healthFilePath));
                File.AppendAllText(_healthFilePath, jsonLine + "\n", new UTF8Encoding(false));
                if (new FileInfo(_healthFilePath).Length > HealthFileMaxBytes)
                {
                    var lines = File.ReadAllLines(_healthFilePath);
                    File.WriteAllLines(_healthFilePath, lines.Skip(Math.Max(0, lines.Length - HealthFileKeepLines)).ToArray(), new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // The file is a convenience; the same numbers are in KSP.log.
            }
        }

        #endregion

        #region Summary for reports

        /// <summary>
        /// A plain-text digest for the top of a report: machine, memory now, the last health rows, recent stalls and the
        /// most frequent errors. Built on the main thread.
        /// </summary>
        public static string BuildSummary()
        {
            var sb = new StringBuilder();
            try
            {
                var memory = StallWatch.GetMemory();
                var inv = CultureInfo.InvariantCulture;
                sb.Append("Session started (UTC): ").Append(_sessionStartUtc.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\n');
                sb.Append("Memory now: ").Append(StallWatch.MemoryText(memory)).Append('\n');
                sb.Append("GC collections this session: gen0 ").Append(GC.CollectionCount(0)).Append(", gen1 ").Append(GC.CollectionCount(1))
                    .Append(", gen2 ").Append(GC.CollectionCount(2)).Append('\n');
                sb.Append("Errors this session: ").Append(TotalErrors).Append(" errors, ").Append(TotalExceptions).Append(" exceptions\n");

                lock (StateLock)
                {
                    sb.Append("\nRecent frame stalls (>= ").Append(StallWatch.FrameStallSeconds.ToString("0.0", inv)).Append(" s):\n");
                    if (Stalls.Count == 0) sb.Append("  none\n");
                    foreach (var stall in Stalls.Skip(Math.Max(0, Stalls.Count - 15)))
                    {
                        sb.Append("  ").Append(stall.Utc.ToString("HH:mm:ss", inv)).Append("Z  ").Append(stall.Seconds.ToString("0.0", inv))
                            .Append(" s in ").Append(stall.Scene).Append(", ").Append(((int)stall.SecondsSinceSceneChange).ToString(inv))
                            .Append(" s after the scene changed\n");
                    }

                    sb.Append("\nLast health rows:\n");
                    if (Rows.Count == 0) sb.Append("  none yet\n");
                    foreach (var row in Rows.Skip(Math.Max(0, Rows.Count - 10)))
                    {
                        sb.Append("  ").Append(row).Append('\n');
                    }
                }

                sb.Append("\nMost frequent errors (normalised):\n");
                var top = Signatures.Where(kv => kv.Key != OtherSignature).OrderByDescending(kv => kv.Value).Take(10).ToList();
                if (top.Count == 0) sb.Append("  none\n");
                foreach (var kv in top)
                {
                    sb.Append("  ").Append(kv.Value.ToString(inv).PadLeft(6)).Append("  ").Append(kv.Key).Append('\n');
                }

                if (Signatures.TryGetValue(OtherSignature, out var other))
                    sb.Append("  ").Append(other.ToString(inv).PadLeft(6)).Append("  more errors with signatures not tracked (too many different ones)\n");
            }
            catch (Exception e)
            {
                sb.Append("Could not build the health summary: ").Append(e.Message).Append('\n');
            }

            return sb.ToString();
        }

        #endregion
    }
}
