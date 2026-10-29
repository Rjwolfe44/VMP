using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using LmpCommon;
using LmpCommon.Message.Data.Vessel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LmpClient
{
    /// <summary>
    /// Ring buffer of recent vessel position sync events for export and offline review.
    /// </summary>
    public static class VmpSyncTrace
    {
        private const int MaxEntries = 2000;
        private const double ApplySampleIntervalSec = 0.2;

        private static readonly object Lock = new object();
        private static readonly string[] Buffer = new string[MaxEntries];
        private static int _head;
        private static int _count;
        private static readonly Dictionary<Guid, double> LastApplySampleUt = new Dictionary<Guid, double>();

        // Diagnostics must never break sync: faults are swallowed and logged at most once per session per entry point.
        private static int _receivedFaultLogged;
        private static int _applyFaultLogged;

        public static bool Enabled { get; set; } = true;

        public static void RecordReceived(VesselPositionMsgData msg)
        {
            if (!Enabled || msg == null) return;

            try
            {
                Append("recv",
                    $"\"vesselId\":\"{msg.VesselId}\"," +
                    $"\"gameTime\":{msg.GameTime.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"pingSec\":{msg.PingSec.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"seq\":{msg.SequenceNumber}," +
                    $"\"delta\":\"{msg.DeltaFields}\"," +
                    $"\"subspace\":{msg.SubspaceId}");
            }
            catch (Exception e)
            {
                LogFaultOnce(ref _receivedFaultLogged, nameof(RecordReceived), e);
            }
        }

        public static void RecordApply(VesselPositionUpdate update)
        {
            if (!Enabled || update == null) return;

            try
            {
                var ut = TimeSyncSystem.UniversalTime;
                lock (Lock)
                {
                    if (LastApplySampleUt.TryGetValue(update.VesselId, out var lastUt) &&
                        ut - lastUt < ApplySampleIntervalSec)
                    {
                        return;
                    }

                    LastApplySampleUt[update.VesselId] = ut;
                }

                var queueDepth = 0;
                if (VesselPositionSystem.TargetVesselUpdateQueue.TryGetValue(update.VesselId, out var queue))
                    queueDepth = queue.Count;

                var offsetMs = VesselCommon.PositionAndFlightStateMessageOffsetSec(update.PingSec) * 1000f;

                // Target is null for a vessel that has no newer position queued (parked vessel, owner at the KSC, ...)
                // and it can also be cleared at any time (AdjustExtraInterpolationTimes), so read it exactly once.
                var hasTarget = update.Target != null;
                var lerp = hasTarget ? update.LerpPercentage : 1f;
                var interpMs = hasTarget
                    ? (update.InterpolationDuration * 1000).ToString(CultureInfo.InvariantCulture)
                    : "null";

                Append("apply",
                    $"\"vesselId\":\"{update.VesselId}\"," +
                    $"\"gameTime\":{update.GameTimeStamp.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"pingSec\":{update.PingSec.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"renderOffsetMs\":{offsetMs.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"timeDiffMs\":{(update.TimeDifference * 1000).ToString(CultureInfo.InvariantCulture)}," +
                    $"\"extraInterpMs\":{(update.ExtraInterpolationTime * 1000).ToString(CultureInfo.InvariantCulture)}," +
                    $"\"lerp\":{lerp.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"queue\":{queueDepth}," +
                    $"\"interpMs\":{interpMs}");
            }
            catch (Exception e)
            {
                LogFaultOnce(ref _applyFaultLogged, nameof(RecordApply), e);
            }
        }

        public static string ExportToFile()
        {
            var folder = CommonUtil.CombinePaths(MainSystem.KspPath, "GameData", "VladMultiplayer", "Traces");
            Directory.CreateDirectory(folder);

            var fileName = $"sync-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl";
            var path = CommonUtil.CombinePaths(folder, fileName);

            var sb = new StringBuilder();
            lock (Lock)
            {
                var start = _count < MaxEntries ? 0 : _head;
                for (var i = 0; i < _count; i++)
                {
                    var idx = (start + i) % MaxEntries;
                    var line = Buffer[idx];
                    if (!string.IsNullOrEmpty(line))
                    {
                        sb.AppendLine(line);
                    }
                }
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        public static void Clear()
        {
            lock (Lock)
            {
                _head = 0;
                _count = 0;
                Array.Clear(Buffer, 0, Buffer.Length);
                LastApplySampleUt.Clear();
            }
        }

        private static void LogFaultOnce(ref int loggedFlag, string source, Exception e)
        {
            try
            {
                if (Interlocked.Exchange(ref loggedFlag, 1) == 0)
                    LunaLog.LogError($"VmpSyncTrace.{source} failed, trace entries will be skipped for this fault (logged once per session): {e}");
            }
            catch (Exception)
            {
                // Diagnostics must never throw into the sync path.
            }
        }

        private static void Append(string kind, string fields)
        {
            var line = $"{{\"kind\":\"{kind}\",\"ut\":{TimeSyncSystem.UniversalTime.ToString(CultureInfo.InvariantCulture)},{fields}}}";

            lock (Lock)
            {
                Buffer[_head] = line;
                _head = (_head + 1) % MaxEntries;
                if (_count < MaxEntries)
                    _count++;
            }
        }
    }
}
