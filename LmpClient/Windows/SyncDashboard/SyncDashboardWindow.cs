using LmpClient.Base;
using LmpClient.Network;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.Utilities;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using LmpCommon.Stats;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LmpClient.Windows.SyncDashboard
{
    public partial class SyncDashboardWindow : Window<SyncDashboardWindow>
    {
        private const float DisplayUpdateInterval = 0.25f;
        private const float WindowHeight = 480;
        private const float WindowWidth = 680;

        private static bool _display;
        private static float _lastUpdateTime;
        private static string _summaryText = string.Empty;
        private static string _lastExportPath = string.Empty;
        private static readonly List<string> VesselLines = new List<string>();

        public override bool Display
        {
            get => base.Display && _display && MainSystem.NetworkState >= ClientState.Running;
            set => base.Display = _display = value;
        }

        public override void Update()
        {
            base.Update();
            if (!Display || Time.realtimeSinceStartup - _lastUpdateTime < DisplayUpdateInterval)
                return;

            _lastUpdateTime = Time.realtimeSinceStartup;
            RefreshText();
        }

        private static void RefreshText()
        {
            var stats = VmpNetStats.GetSnapshot();
            var sb = new StringBuilder();

            sb.AppendLine($"Ping: {NetworkStatistics.PingSec * 1000f:F0}ms  Avg: {NetworkStatistics.AvgPingSec * 1000f:F0}ms");
            sb.AppendLine($"UT: {TimeSyncSystem.UniversalTime:F2}  Time error: {TimeSyncSystem.CurrentErrorSec * 1000f:F0}ms");
            sb.AppendLine($"Send interval: {VesselCommon.GetEffectiveVesselUpdatesMsInterval()}ms");
            sb.AppendLine($"Pos-upd totals  sent={stats.TotalMessagesSent} recv={stats.TotalMessagesReceived} drops={stats.TotalQueueDrops}");
            sb.AppendLine($"Last {stats.RecentWindowSeconds:0}s  sent={stats.RecentMessagesSent} recv={stats.RecentMessagesReceived} drops={stats.RecentQueueDrops} peakQ={stats.RecentPeakQueueDepth}");
            sb.AppendLine($"Live (since last log) sent={stats.LiveMessagesSent} recv={stats.LiveMessagesReceived} peakQ={stats.LivePeakQueueDepth}");
            sb.AppendLine($"Trace recording: {(VmpSyncTrace.Enabled ? "on" : "off")}");
            AppendTraffic(sb);

            _summaryText = sb.ToString();

            VesselLines.Clear();
            foreach (var keyVal in VesselPositionSystem.CurrentVesselUpdate)
            {
                var update = keyVal.Value;
                if (update.Target == null)
                    continue;

                var vessel = update.Vessel;
                var name = vessel != null ? vessel.vesselName : keyVal.Key.ToString().Substring(0, 8);
                var queue = 0;
                if (VesselPositionSystem.TargetVesselUpdateQueue.TryGetValue(keyVal.Key, out var q))
                    queue = q.Count;

                var offsetMs = VesselCommon.PositionAndFlightStateMessageOffsetSec(update.PingSec) * 1000f;
                var warn = Math.Abs(update.TimeDifference) > 0.15 || queue >= 8;

                VesselLines.Add(
                    $"{(warn ? "! " : "  ")}{name}  ping={update.PingSec * 1000f:F0}ms  off={offsetMs:F0}ms  " +
                    $"tdiff={update.TimeDifference * 1000f:F0}ms  lerp={update.LerpPercentage * 100f:F0}%  q={queue}  " +
                    $"interp={update.InterpolationDuration * 1000f:F0}ms");
            }
        }

        /// <summary>Bytes per message type since the game started, biggest first, and what snapshot compression saved.</summary>
        private static void AppendTraffic(StringBuilder sb)
        {
            var sent = VmpTrafficStats.GetSentTotals();
            var received = VmpTrafficStats.GetReceivedTotals();
            long sentBytes = 0, receivedBytes = 0;
            foreach (var row in sent) sentBytes += row.Bytes;
            foreach (var row in received) receivedBytes += row.Bytes;

            sb.AppendLine();
            sb.AppendLine($"Traffic since start: sent {VmpTrafficStats.FormatBytes(sentBytes)}, received {VmpTrafficStats.FormatBytes(receivedBytes)}");
            sb.AppendLine("  Sent:");
            for (var i = 0; i < sent.Count && i < 6; i++)
                sb.AppendLine($"    {sent[i].Name}  {VmpTrafficStats.FormatBytes(sent[i].Bytes)}  ({sent[i].Messages} msgs)");
            sb.AppendLine("  Received:");
            for (var i = 0; i < received.Count && i < 8; i++)
                sb.AppendLine($"    {received[i].Name}  {VmpTrafficStats.FormatBytes(received[i].Bytes)}  ({received[i].Messages} msgs)");

            var snapshots = VmpTrafficStats.GetSnapshotTotals();
            if (snapshots.Count > 0)
            {
                sb.AppendLine("  Career sync snapshots (decoded size -> on the wire):");
                foreach (var row in snapshots)
                {
                    var ratio = row.WireBytes > 0 ? (double)row.RawBytes / row.WireBytes : 0;
                    sb.AppendLine($"    {row.Domain}  {row.Snapshots}x  {VmpTrafficStats.FormatBytes(row.RawBytes)} -> {VmpTrafficStats.FormatBytes(row.WireBytes)}  ({ratio:0.#}x)");
                }
            }
        }

        protected override void DrawGui()
        {
            GUI.skin = DefaultSkin;
            WindowRect = FixWindowPos(GUILayout.Window(6710 + MainSystem.WindowOffset, WindowRect, DrawContent, "VMP Sync", LayoutOptions));
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(40, Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);

            LayoutOptions = new GUILayoutOption[4];
            LayoutOptions[0] = GUILayout.MinWidth(WindowWidth);
            LayoutOptions[1] = GUILayout.MaxWidth(WindowWidth);
            LayoutOptions[2] = GUILayout.MinHeight(WindowHeight);
            LayoutOptions[3] = GUILayout.MaxHeight(WindowHeight);
        }

        public override void RemoveWindowLock()
        {
            if (!IsWindowLocked) return;
            IsWindowLocked = false;
            InputLockManager.RemoveControlLock("VMP_SyncDashboardLock");
        }

        public override void CheckWindowLock()
        {
            if (!Display)
            {
                if (IsWindowLocked) RemoveWindowLock();
                return;
            }

            if (MainSystem.NetworkState < ClientState.Running)
            {
                RemoveWindowLock();
                return;
            }

            var mousePos = Input.mousePosition;
            mousePos.y = Screen.height - mousePos.y;
            var shouldLock = WindowRect.Contains(mousePos);

            if (shouldLock && !IsWindowLocked)
            {
                InputLockManager.SetControlLock(LmpImguiInputLockMask.WindowMouseCapture, "VMP_SyncDashboardLock");
                IsWindowLocked = true;
            }
            else if (!shouldLock && IsWindowLocked)
            {
                RemoveWindowLock();
            }
        }
    }
}
