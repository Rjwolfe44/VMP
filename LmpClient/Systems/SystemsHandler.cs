using LmpClient.Base.Interface;
using LmpClient.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine.Profiling;

// ReSharper disable ForCanBeConvertedToForeach

namespace LmpClient.Systems
{
    /// <summary>
    /// This class contains all the systems. Here they are called from the MainSystem so they are run in the update, fixed update or late update calls
    /// </summary>
    public static class SystemsHandler
    {
        private static ISystem[] _systems = new ISystem[0];

        #region Own cost (for the health line)

        /// <summary>One system's share of VMP's own cost over a health window.</summary>
        public struct SystemCost
        {
            public string Name;
            public double MsPerFrame;
            public double WorstCallMs;
        }

        /// <summary>VMP's own cost (all system FixedUpdate/Update/LateUpdate calls) over a health window.</summary>
        public sealed class CostWindow
        {
            public int Frames;
            public double AvgMsPerFrame;
            public double WorstFrameMs;
            public List<SystemCost> Top = new List<SystemCost>();
        }

        // Stopwatch ticks per system, all three phases combined. Main thread only.
        private static long[] _costTicks = new long[0];
        private static long[] _worstCallTicks = new long[0];
        private static long _frameTicks;
        private static long _windowTicks;
        private static long _worstFrameTicks;
        private static int _windowFrames;

        private static void RecordCost(int index, long startTimestamp)
        {
            var elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            if (index >= _costTicks.Length) return;
            _costTicks[index] += elapsed;
            if (elapsed > _worstCallTicks[index]) _worstCallTicks[index] = elapsed;
            _frameTicks += elapsed;
        }

        /// <summary>Closes the current rendered frame's cost bucket. Called once per frame (from <see cref="Utilities.ClientHealth.OnFrame"/>).</summary>
        public static void EndFrameCost()
        {
            _windowTicks += _frameTicks;
            if (_frameTicks > _worstFrameTicks) _worstFrameTicks = _frameTicks;
            _frameTicks = 0;
            _windowFrames++;
        }

        /// <summary>Returns the cost since the last call (top <paramref name="topCount"/> systems by average ms per frame) and resets the counters.</summary>
        public static CostWindow TakeCostWindow(int topCount)
        {
            var toMs = 1000.0 / Stopwatch.Frequency;
            var frames = Math.Max(1, _windowFrames);
            var window = new CostWindow
            {
                Frames = _windowFrames,
                AvgMsPerFrame = _windowTicks * toMs / frames,
                WorstFrameMs = _worstFrameTicks * toMs,
            };

            var count = Math.Min(_systems.Length, _costTicks.Length);
            window.Top = Enumerable.Range(0, count)
                .Where(i => _costTicks[i] > 0)
                .OrderByDescending(i => _costTicks[i])
                .Take(topCount)
                .Select(i => new SystemCost { Name = _systems[i].SystemName, MsPerFrame = _costTicks[i] * toMs / frames, WorstCallMs = _worstCallTicks[i] * toMs })
                .ToList();

            Array.Clear(_costTicks, 0, _costTicks.Length);
            Array.Clear(_worstCallTicks, 0, _worstCallTicks.Length);
            _windowTicks = 0;
            _worstFrameTicks = 0;
            _windowFrames = 0;
            return window;
        }

        #endregion

        /// <summary>
        /// Here we pick all the classes that inherit from ISystem and we put them in the systems array
        /// </summary>
        public static void FillUpSystemsList()
        {
            var systemsList = new List<ISystem>();

            var systems = Assembly.GetExecutingAssembly().GetLoadableTypes().Where(t => t.IsClass && typeof(ISystem).IsAssignableFrom(t) && !t.IsAbstract).ToArray();
            foreach (var system in systems)
            {
                try
                {
                    if (system.GetProperty("Singleton", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null, null) is ISystem systemImplementation)
                        systemsList.Add(systemImplementation);
                }

                catch (Exception ex)
                {
                    LunaLog.LogError($"Exception loading system type {system.FullName}: {ex.Message}");
                }
            }

            var ordered = systemsList.OrderBy(s => s.ExecutionOrder).ToArray();
            _costTicks = new long[ordered.Length];
            _worstCallTicks = new long[ordered.Length];
            _systems = ordered;
        }

        /// <summary>
        /// Call all the fixed updates of the systems
        /// </summary>
        public static void FixedUpdate()
        {
            for (var i = 0; i < _systems.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    Profiler.BeginSample(_systems[i].SystemName);
                    _systems[i].FixedUpdate();
                    Profiler.EndSample();
                }
                catch (Exception e)
                {
                    MainSystem.Singleton.HandleException(e, "SystemHandler-FixedUpdate");
                }
                RecordCost(i, start);
            }
        }

        /// <summary>
        /// Call all the updates of the systems
        /// </summary>
        public static void Update()
        {
            for (var i = 0; i < _systems.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    Profiler.BeginSample(_systems[i].SystemName);
                    _systems[i].Update();
                    Profiler.EndSample();
                }
                catch (Exception e)
                {
                    MainSystem.Singleton.HandleException(e, "SystemHandler-Update");
                }
                RecordCost(i, start);
            }
        }

        /// <summary>
        /// Call all the updates of the systems
        /// </summary>
        public static void LateUpdate()
        {
            for (var i = 0; i < _systems.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    Profiler.BeginSample(_systems[i].SystemName);
                    _systems[i].LateUpdate();
                    Profiler.EndSample();
                }
                catch (Exception e)
                {
                    MainSystem.Singleton.HandleException(e, "SystemHandler-Update");
                }
                RecordCost(i, start);
            }
        }
    }
}
