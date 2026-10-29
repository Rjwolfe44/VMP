using LmpCommon.Stats;
using Server.Log;
using System;

namespace Server.Server
{
    /// <summary>
    /// Logs what the server sent and received per message type about once a minute (only while something moved), so the
    /// next "we use a lot of bandwidth" question can be answered from the log: which message types, how many bytes, and
    /// how much the snapshot encodings saved. The same numbers are in the <c>connectionstats</c> console command.
    /// </summary>
    public static class ServerTrafficLog
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
        private static DateTime _lastLogUtc = DateTime.UtcNow;

        public static void MaybeLog()
        {
            var now = DateTime.UtcNow;
            VmpTrafficStats.RollWindows(now);
            if (now - _lastLogUtc < Interval)
            {
                return;
            }

            _lastLogUtc = now;
            var summary = VmpTrafficStats.TakeWindowSummary();
            if (summary != null)
            {
                LunaLog.Normal(summary);
            }
        }
    }
}
