using System;
using System.Threading;
using HarmonyLib;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// Silences the stock line "[PartSet]: Failed to add Resource ... as corresponding Part ... SimulationResource was
    /// not found." and nothing else.
    ///
    /// Stock 1.12.5 PartSet.AddPartResource(PartResource, Dictionary) for a simulation set does: if the resource is not
    /// itself a simulation resource, r.part.ResetSimulationResources(), look r up in r.part.SimulationResources, and
    /// when it is missing log that line (formatting four boxed arguments and capturing a Unity stack trace) and return.
    /// Interstellar Fuel Switch / KSPI cryostat tanks add an ElectricCharge buffer to every tank that the simulation
    /// list does not have, so one big vessel produced about 104,000 of those lines (about 5 s) per launch.
    ///
    /// The prefix repeats exactly the stock decision and skips the original only in the failing case, where stock would
    /// have logged and returned. In every other case the original runs untouched. Any problem returns true.
    /// </summary>
    [HarmonyPatch(typeof(PartSet))]
    [HarmonyPatch("AddPartResource")]
    public class PartSet_AddPartResource
    {
        [HarmonyPrefix]
        private static bool PrefixSilenceMissingSimulationResource(PartResource r, bool ___simulationSet)
        {
            try
            {
                if (!___simulationSet || ReferenceEquals(r, null) || r.simulationResource) return true;

                var part = r.part;
                if (ReferenceEquals(part, null)) return true;

                var simulationResources = part.SimulationResources;
                if (ReferenceEquals(simulationResources, null)) return true;

                var id = r.info.id;

                // Common case: the simulation resource is already there. Let stock run (it resets and uses it).
                if (simulationResources.Get(id) != null) return true;

                // Stock resets the simulation list before looking again; that can create the missing entry.
                part.ResetSimulationResources();
                simulationResources = part.SimulationResources;
                if (ReferenceEquals(simulationResources, null) || simulationResources.Get(id) != null) return true;

                // Stock would log here and return.
                SuppressedLogLines.Record(Environment.TickCount);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Counts the silenced lines and writes one info line per minute at most, so the diagnostics can still see it.
    /// </summary>
    internal static class SuppressedLogLines
    {
        internal const int ReportIntervalMs = 60000;

        private static long _count;
        private static int _lastReportTick;
        private static int _reported; // 0 = nothing reported yet

        internal static long Count => Interlocked.Read(ref _count);

        internal static void Record(int nowTick)
        {
            var count = Interlocked.Increment(ref _count);
            // A burst of 100,000 lines happens within seconds, so also report when the count passes 10, 100, 1,000...
            // (otherwise the first line says "suppressed 1" and the real total only appears a minute later, or never).
            if (!IsPowerOfTen(count) && !ShouldReport(nowTick, _reported != 0, _lastReportTick, ReportIntervalMs)) return;

            _lastReportTick = nowTick;
            _reported = 1;
            LunaLog.Log($"[VMP]: Silenced stock 'Failed to add Resource' log lines (suppressed {count} so far)");
        }

        /// <summary>True for 1, 10, 100, 1000, ... : the milestones that are reported at once.</summary>
        internal static bool IsPowerOfTen(long count)
        {
            if (count < 1) return false;
            while (count % 10 == 0) count /= 10;
            return count == 1;
        }

        /// <summary>Pure rule: report the first time, then once the interval has passed (tick wrap safe).</summary>
        internal static bool ShouldReport(int nowTick, bool reportedBefore, int lastReportTick, int intervalMs)
        {
            return !reportedBefore || unchecked(nowTick - lastReportTick) >= intervalMs;
        }
    }
}
