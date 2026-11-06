using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LmpClient.Utilities
{
    /// <summary>
    /// Names the slow part of a KSP game state save. Game.Updated() (run by every stock save and quicksave) saves every
    /// scenario module and then fires GameEvents.onGameStateSave to every mod; on a big pack one of them can freeze the
    /// game for minutes and the log only shows a silent gap. While a Game.Updated runs (Harmony patch Game_Updated) this
    /// class times:
    ///  * each scenario module's Save (Harmony patch ProtoScenarioModule_Ctor, the call stock makes for every module),
    ///  * each onGameStateSave handler and each onProgressNodeSave handler (ProgressTracking fires that one per
    ///    achievement node while it saves).
    /// When the whole call took <see cref="ReportThresholdMs"/> or more it writes one
    /// "[VMP]: Slow operation: Game.Updated (game state save) took N ms; ..." line naming the slowest parts.
    ///
    /// How the handlers are timed: stock EventData.Fire calls its handlers one after another, last added first, each in
    /// its own try/catch. For the duration of the save we put a tiny marker handler between every two handlers of the
    /// event (the stock handlers keep their order), and each marker notes the time. The gap between two markers is the
    /// handler between them. The stock handlers, their order, their exception handling and Remove() of a stock handler
    /// are unchanged; the markers are taken out again when Game.Updated returns. If KSP's private handler list cannot be
    /// read, handler timing is skipped and only the scenario modules are reported.
    ///
    /// Main thread only. Never throws into the game.
    /// </summary>
    public static class GameSaveProfiler
    {
        public const double ReportThresholdMs = 2000;
        private const double MinMsBetweenReports = 60000;
        private const int SlowestShown = 3;

        private static bool _open;
        private static long _start;
        private static long _lastReport;
        private static bool _setupErrorLogged;

        private static readonly Dictionary<Type, Timing> ScenarioTimes = new Dictionary<Type, Timing>();
        private static HandlerTimer<ConfigNode> _gameStateSave;
        private static HandlerTimer<GameEvents.FromToAction<ProgressNode, ConfigNode>> _progressNodeSave;

        private class Timing
        {
            public long Ticks;
            public int Calls;
        }

        /// <summary>True while a Game.Updated is being measured.</summary>
        public static bool IsMeasuring => _open;

        /// <summary>Called at the start of Game.Updated. Closes a measurement an exception left open.</summary>
        public static void Begin()
        {
            if (_open)
            {
                Close();
            }

            try
            {
                ScenarioTimes.Clear();
                _gameStateSave = HandlerTimer<ConfigNode>.Attach(GameEvents.onGameStateSave);
                _progressNodeSave = HandlerTimer<GameEvents.FromToAction<ProgressNode, ConfigNode>>.Attach(GameEvents.onProgressNodeSave);
                _open = true;
                _start = Stopwatch.GetTimestamp();
            }
            catch (Exception e)
            {
                LogSetupErrorOnce(e);
                Close();
            }
        }

        /// <summary>Called when Game.Updated returns. Logs the slow parts when the call was slow.</summary>
        public static void End()
        {
            if (!_open)
            {
                return;
            }

            var ms = StallWatch.ElapsedMs(_start);
            try
            {
                // Take the markers out first so nothing below can leave them behind.
                _gameStateSave?.Detach();
                _progressNodeSave?.Detach();

                if (ms >= ReportThresholdMs && (_lastReport == 0 || StallWatch.ElapsedMs(_lastReport) >= MinMsBetweenReports))
                {
                    _lastReport = Stopwatch.GetTimestamp();
                    LunaLog.Log(BuildLine(ms));
                }
            }
            catch (Exception e)
            {
                LogSetupErrorOnce(e);
            }
            finally
            {
                Close();
            }
        }

        /// <summary>Records one scenario module Save that started at <paramref name="startTimestamp"/>.</summary>
        public static void RecordScenarioModule(ScenarioModule module, long startTimestamp)
        {
            if (!_open || module == null)
            {
                return;
            }

            var type = module.GetType();
            if (!ScenarioTimes.TryGetValue(type, out var timing))
            {
                timing = new Timing();
                ScenarioTimes[type] = timing;
            }

            timing.Ticks += Stopwatch.GetTimestamp() - startTimestamp;
            timing.Calls++;
        }

        private static void Close()
        {
            try
            {
                _gameStateSave?.Detach();
                _progressNodeSave?.Detach();
            }
            catch (Exception e)
            {
                LogSetupErrorOnce(e);
            }

            _gameStateSave = null;
            _progressNodeSave = null;
            ScenarioTimes.Clear();
            _open = false;
        }

        private static void LogSetupErrorOnce(Exception e)
        {
            if (_setupErrorLogged)
            {
                return;
            }

            _setupErrorLogged = true;
            LunaLog.LogWarning($"[VMP]: Game save profiler disabled its handler timing: {e.GetType().Name}: {e.Message}");
        }

        private static string BuildLine(double totalMs)
        {
            var sb = new StringBuilder();
            sb.Append("[VMP]: Slow operation: Game.Updated (game state save) took ").Append(totalMs.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" ms in ").Append(HighLogic.LoadedScene);

            var scenarioMs = ScenarioTimes.Values.Sum(t => TicksToMs(t.Ticks));
            sb.Append("; scenario module saves ").Append(ScenarioTimes.Count).Append(" modules ").Append(Ms(scenarioMs)).Append(", slowest: ");
            AppendSlowest(sb, ScenarioTimes.Select(kv => new KeyValuePair<string, Timing>(TypeLabel(kv.Key), kv.Value)), false);

            var handlerMs = 0d;
            handlerMs += AppendHandlers(sb, "onGameStateSave", _gameStateSave, false);
            //ProgressTracking fires onProgressNodeSave from inside its own OnSave, so its time is already part of the
            //scenario module total above; it is listed for the names only and not subtracted again.
            AppendHandlers(sb, "onProgressNodeSave (inside the ProgressTracking save)", _progressNodeSave, true);

            sb.Append("; rest (flight state, crew check, stock code) ").Append(Ms(Math.Max(0, totalMs - scenarioMs - handlerMs)));
            return sb.ToString();
        }

        private static double AppendHandlers<T>(StringBuilder sb, string eventName, HandlerTimer<T> timer, bool showCalls)
        {
            sb.Append("; ").Append(eventName).Append(' ');
            if (timer == null)
            {
                sb.Append("not timed");
                return 0;
            }

            var entries = timer.Results().ToList();
            var ms = entries.Sum(e => TicksToMs(e.Value.Ticks));
            sb.Append(entries.Count).Append(" handlers ").Append(Ms(ms)).Append(", slowest: ");
            AppendSlowest(sb, entries, showCalls);
            return ms;
        }

        private static void AppendSlowest(StringBuilder sb, IEnumerable<KeyValuePair<string, Timing>> entries, bool showCalls)
        {
            var slowest = entries.Where(e => e.Value.Ticks > 0).OrderByDescending(e => e.Value.Ticks).Take(SlowestShown).ToList();
            if (slowest.Count == 0)
            {
                sb.Append("none");
                return;
            }

            for (var i = 0; i < slowest.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(slowest[i].Key).Append(' ').Append(Ms(TicksToMs(slowest[i].Value.Ticks)));
                if (showCalls) sb.Append(" over ").Append(slowest[i].Value.Calls).Append(" calls");
            }
        }

        private static string Ms(double ms) => ms.ToString("F0", CultureInfo.InvariantCulture) + " ms";

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static string TypeLabel(Type type)
        {
            return type == null ? "?" : $"{type.FullName} ({type.Assembly.GetName().Name})";
        }

        private static string HandlerLabel(Delegate handler)
        {
            var method = handler?.Method;
            if (method == null)
            {
                return "?";
            }

            var type = method.DeclaringType;
            return type == null ? method.Name : $"{type.FullName}.{method.Name} ({type.Assembly.GetName().Name})";
        }

        /// <summary>
        /// Times every handler of one EventData by putting a marker handler before, between and after them. See the class
        /// summary.
        /// </summary>
        private sealed class HandlerTimer<T>
        {
            private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

            private readonly IList _handlerList;
            private readonly object[] _markerEntries;
            private readonly Delegate[] _handlers;
            private readonly Timing[] _timings;
            private int _lastMarker = -1;
            private long _lastStamp;
            private bool _attached;

            private HandlerTimer(IList handlerList, int handlerCount)
            {
                _handlerList = handlerList;
                _markerEntries = new object[handlerCount + 1];
                _handlers = new Delegate[handlerCount];
                _timings = new Timing[handlerCount];
                for (var i = 0; i < handlerCount; i++)
                {
                    _timings[i] = new Timing();
                }
            }

            /// <summary>
            /// Puts the markers in. Returns null (and leaves the event as it was) when the handler list cannot be read.
            /// </summary>
            public static HandlerTimer<T> Attach(EventData<T> gameEvent)
            {
                if (gameEvent == null)
                {
                    return null;
                }

                if (!(typeof(EventData<T>).GetField("events", Private)?.GetValue(gameEvent) is IList list))
                {
                    return null;
                }

                var elementType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null;
                var evtField = elementType?.GetField("evt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (evtField == null)
                {
                    return null;
                }

                var count = list.Count;
                var originals = new object[count];
                list.CopyTo(originals, 0);

                var timer = new HandlerTimer<T>(list, count);
                for (var i = 0; i < count; i++)
                {
                    timer._handlers[i] = evtField.GetValue(originals[i]) as Delegate;
                }

                // Create the marker entries through the public API; Add appends them after the stock handlers.
                for (var i = 0; i <= count; i++)
                {
                    gameEvent.Add(new Marker(timer, i).OnEvent);
                }

                if (list.Count != 2 * count + 1)
                {
                    // Someone else changed the list meanwhile: take our markers out and do not time this event.
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] != null && evtField.GetValue(list[i]) is EventData<T>.OnEvent d && d.Target is Marker)
                            list.RemoveAt(i);
                    }

                    return null;
                }

                for (var i = 0; i <= count; i++)
                {
                    timer._markerEntries[i] = list[count + i];
                }

                // New order: marker 0, handler 0, marker 1, handler 1, ..., handler n-1, marker n.
                var interleaved = new object[2 * count + 1];
                for (var i = 0; i < count; i++)
                {
                    interleaved[2 * i] = timer._markerEntries[i];
                    interleaved[2 * i + 1] = originals[i];
                }

                interleaved[2 * count] = timer._markerEntries[count];

                try
                {
                    list.Clear();
                    foreach (var entry in interleaved)
                    {
                        list.Add(entry);
                    }
                }
                catch
                {
                    // Put the stock handlers back exactly as they were.
                    list.Clear();
                    foreach (var entry in originals)
                    {
                        list.Add(entry);
                    }

                    throw;
                }

                timer._attached = true;
                return timer;
            }

            /// <summary>Takes the markers out. The stock handlers (and any added meanwhile) stay as they are.</summary>
            public void Detach()
            {
                if (!_attached)
                {
                    return;
                }

                _attached = false;
                foreach (var entry in _markerEntries)
                {
                    _handlerList.Remove(entry);
                }
            }

            public IEnumerable<KeyValuePair<string, Timing>> Results()
            {
                for (var i = 0; i < _handlers.Length; i++)
                {
                    yield return new KeyValuePair<string, Timing>(HandlerLabel(_handlers[i]), _timings[i]);
                }
            }

            /// <summary>
            /// Stock fires the list from the end: marker n, handler n-1, marker n-1, ..., handler 0, marker 0. So when
            /// marker i fires right after marker i+1, the time since then belongs to handler i.
            /// </summary>
            private void Mark(int index)
            {
                var now = Stopwatch.GetTimestamp();
                if (index < _timings.Length && _lastMarker == index + 1)
                {
                    _timings[index].Ticks += now - _lastStamp;
                    _timings[index].Calls++;
                }

                _lastMarker = index == 0 ? -1 : index;
                _lastStamp = now;
            }

            private sealed class Marker
            {
                private readonly HandlerTimer<T> _timer;
                private readonly int _index;

                public Marker(HandlerTimer<T> timer, int index)
                {
                    _timer = timer;
                    _index = index;
                }

                public void OnEvent(T data) => _timer.Mark(_index);
            }
        }
    }
}
