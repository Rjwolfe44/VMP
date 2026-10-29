using System;
using System.Globalization;

namespace Server.System.LogUpload
{
    public enum LogRequestStatus
    {
        /// <summary>The request was registered and sent to the player's game.</summary>
        Sent,
        /// <summary>No such player is connected.</summary>
        NotFound,
        /// <summary>The player's game is not the server's own build, so it cannot parse the request message.</summary>
        OldClient,
        /// <summary>A request for this player was issued less than <see cref="LmpCommon.LogUpload.LogUploadLimits.MinTimeBetweenRequests"/> ago.</summary>
        TooSoon,
    }

    /// <summary>The outcome of "ask this player's game for a diagnostics bundle", shared by the console command and the admin API.</summary>
    public sealed class LogRequestResult
    {
        public LogRequestStatus Status;
        /// <summary>The player's name as the server knows it (the name asked for when nobody matched).</summary>
        public string PlayerName = string.Empty;
        public int RetryAfterSeconds;

        public bool Sent => Status == LogRequestStatus.Sent;

        public static LogRequestResult NotFound(string playerName)
        {
            return new LogRequestResult { Status = LogRequestStatus.NotFound, PlayerName = playerName ?? string.Empty };
        }

        /// <summary>One plain sentence for the console or the API's error text.</summary>
        public string Describe()
        {
            switch (Status)
            {
                case LogRequestStatus.Sent:
                    return $"Asked {PlayerName} to send their game logs.";
                case LogRequestStatus.OldClient:
                    return $"The game of {PlayerName} is not the same build as the server, so it cannot be asked for logs. They need to update first.";
                case LogRequestStatus.TooSoon:
                    return $"Logs were already requested from {PlayerName} a moment ago. Try again in {RetryAfterSeconds.ToString(CultureInfo.InvariantCulture)} s.";
                default:
                    return $"Player not found or not connected: {PlayerName}";
            }
        }

        public static int WholeSeconds(TimeSpan span)
        {
            return span <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(span.TotalSeconds);
        }
    }
}
