using System;
using System.IO;
using UnityEngine;

namespace LmpClient.Utilities
{
    /// <summary>
    /// A small file that exists while the game runs and is deleted on a clean exit. If it is still there at the next
    /// start the previous session ended without one (crash, kill after a freeze, power loss), and the client can offer
    /// the previous session's log (Unity keeps it as Player-prev.log) to the host.
    /// </summary>
    public static class SessionMarker
    {
        public static bool PreviousEndedUnexpectedly { get; private set; }
        public static string PreviousStartUtc { get; private set; } = string.Empty;
        public static string PreviousBuild { get; private set; } = string.Empty;

        /// <summary>Set once the crash report for the previous session has been started, so it is sent only once.</summary>
        public static bool CrashReportHandled { get; set; }

        private static string _path;

        public static void Begin(string logsFolder, string build)
        {
            try
            {
                Directory.CreateDirectory(logsFolder);
                _path = Path.Combine(logsFolder, "session.marker");

                if (File.Exists(_path))
                {
                    PreviousEndedUnexpectedly = true;
                    var lines = File.ReadAllLines(_path);
                    if (lines.Length > 0) PreviousStartUtc = lines[0];
                    if (lines.Length > 1) PreviousBuild = lines[1];
                }

                File.WriteAllText(_path, DateTime.UtcNow.ToString("o") + "\n" + build + "\n");
                Application.quitting += EndClean;
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[VMP]: Could not write the session marker: {e.Message}");
            }
        }

        public static void EndClean()
        {
            try
            {
                if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception)
            {
                // A leftover marker only makes the next start offer a report.
            }
        }
    }
}
