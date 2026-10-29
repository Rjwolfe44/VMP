using UnityEngine;

namespace LmpClient.Windows.SyncDashboard
{
    public partial class SyncDashboardWindow
    {
        protected override void DrawWindowContent(int windowId)
        {
            GUI.DragWindow(MoveRect);

            ScrollPos = GUILayout.BeginScrollView(ScrollPos, GUILayout.Width(WindowWidth), GUILayout.Height(WindowHeight));
            GUILayout.Label(_summaryText);

            GUILayout.Space(6);
            GUILayout.Label("Remote vessels (! = possible sync stress):");
            for (var i = 0; i < VesselLines.Count; i++)
                GUILayout.Label(VesselLines[i]);

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            VmpSyncTrace.Enabled = GUILayout.Toggle(VmpSyncTrace.Enabled, "Record trace");

            if (GUILayout.Button("Export trace"))
            {
                try
                {
                    _lastExportPath = VmpSyncTrace.ExportToFile();
                    LunaLog.Log($"[VMP Sync] Trace exported to {_lastExportPath}");
                }
                catch (System.Exception ex)
                {
                    LunaLog.LogError($"[VMP Sync] Trace export failed: {ex.Message}");
                }
            }

            if (GUILayout.Button("Clear trace"))
                VmpSyncTrace.Clear();

            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_lastExportPath))
                GUILayout.Label($"Last export:\n{_lastExportPath}");

            GUILayout.EndScrollView();
        }
    }
}
