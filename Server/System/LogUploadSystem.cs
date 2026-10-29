using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.LogUpload;
using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System.LogUpload;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Server.System
{
    /// <summary>
    /// "Send logs to host": players upload a zip of their game logs over the normal authenticated connection and the
    /// server stores it in <see cref="ClientLogsDirectory"/> (next to Universe and Config) for the host to read. All
    /// validation lives in <see cref="LogUploadAssembler"/>; this class wires it to messages, replies and the log.
    /// </summary>
    public static class LogUploadSystem
    {
        public static readonly string ClientLogsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClientLogs");

        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan IgnoredLogInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromSeconds(10);
        private static readonly ConcurrentDictionary<string, long> LastBeginRefusalLogTicks = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private static readonly object TimerLock = new object();
        private static Timer _sweepTimer;
        private static long _lastIgnoredLogTicks;

        internal static LogUploadAssembler Assembler { get; set; } = new LogUploadAssembler(ClientLogsDirectory);

        private static int _nextRequestId;

        /// <summary>
        /// Asks one connected player's game to send a diagnostics bundle. Shared by the console command (<c>requestlogs</c>) and the
        /// admin API. Nothing is sent to a player whose game is not this server's own build: an older client could not parse the message.
        /// </summary>
        public static LogRequestResult RequestLogs(string playerName, string note)
        {
            EnsureSweepTimer();
            var client = ClientRetriever.GetClientByName(playerName);
            var result = EvaluateRequest(Assembler, playerName, client != null && client.ConnectionStatus == ConnectionStatus.Connected,
                client?.ExactClientBuild, SessionAdmission.LocalExactBuild);
            if (!result.Sent) return result;

            var cleanNote = LogUploadRequestMsgData.CleanNote(note);
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<LogUploadRequestMsgData>();
            data.RequestId = Interlocked.Increment(ref _nextRequestId);
            data.Note = cleanNote;
            MessageQueuer.SendToClient<LogUploadSrvMsg>(client, data);

            result.PlayerName = client.PlayerName;
            LunaLog.Normal($"[LogUpload] asked {result.PlayerName} to send their game logs (request {data.RequestId}" +
                           (cleanNote.Length > 0 ? $", note: {cleanNote}" : string.Empty) + ")");
            return result;
        }

        /// <summary>The checks of <see cref="RequestLogs"/> without the network: registers the request when it may be sent.</summary>
        internal static LogRequestResult EvaluateRequest(LogUploadAssembler assembler, string playerName, bool connected, string clientBuild, string serverBuild)
        {
            if (!connected) return LogRequestResult.NotFound(playerName);
            if (!string.Equals(clientBuild, serverBuild, StringComparison.Ordinal))
                return new LogRequestResult { Status = LogRequestStatus.OldClient, PlayerName = playerName };
            if (!assembler.TryRegisterRequest(playerName, out var retryAfter))
                return new LogRequestResult { Status = LogRequestStatus.TooSoon, PlayerName = playerName, RetryAfterSeconds = LogRequestResult.WholeSeconds(retryAfter) };
            return new LogRequestResult { Status = LogRequestStatus.Sent, PlayerName = playerName };
        }

        public static void HandleBegin(ClientStructure client, LogUploadBeginMsgData data)
        {
            EnsureSweepTimer();
            Assembler.AutoUploadsEnabled = GeneralSettings.SettingsStore.AutoLogUpload;
            var decision = Assembler.Begin(client, client.PlayerName, client.Authenticated, data.UploadId, data.FileKind,
                data.TotalBytes, data.ChunkSize, data.ChunkCount, data.Reason);

            if (decision.Outcome == LogUploadOutcome.Accepted)
            {
                LunaLog.Debug($"[LogUpload] {decision.PlayerName} started a {data.Reason} upload of {LogBundleBuilder.FormatBytes(data.TotalBytes)} in {data.ChunkCount} chunks");
                var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<LogUploadAcceptedMsgData>();
                reply.UploadId = decision.UploadId;
                MessageQueuer.SendToClient<LogUploadSrvMsg>(client, reply);
                return;
            }

            Report(decision, ShouldLogBeginRefusal(decision.PlayerName));
        }

        /// <summary>A client spamming Begin gets one warning line per player per interval, not one per message.</summary>
        private static bool ShouldLogBeginRefusal(string playerName)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var logged = true;
            LastBeginRefusalLogTicks.AddOrUpdate(playerName ?? string.Empty, nowTicks, (_, previous) =>
            {
                if (nowTicks - previous > RefusalLogInterval.Ticks) return nowTicks;
                logged = false;
                return previous;
            });
            return logged;
        }

        public static void HandleChunk(ClientStructure client, LogUploadChunkMsgData data)
        {
            var decision = Assembler.AddChunk(client, client.PlayerName, data.UploadId, data.ChunkIndex, data.ChunkCount, data.TotalBytes,
                data.Data, data.NumBytes);
            Report(decision);
        }

        public static void HandleCancel(ClientStructure client, LogUploadCancelMsgData data)
        {
            var decision = Assembler.Cancel(client, client.PlayerName, data.UploadId);
            if (decision.Outcome == LogUploadOutcome.Refused)
                LunaLog.Normal($"[LogUpload] {decision.PlayerName} cancelled their upload ({LogBundleBuilder.FormatBytes(decision.DeclaredBytes)})");
        }

        public static void OnClientDisconnected(ClientStructure client)
        {
            try
            {
                var decision = Assembler.AbortForDisconnect(client, client.PlayerName);
                if (decision != null)
                    LunaLog.Normal($"[LogUpload] dropped the unfinished upload from {decision.PlayerName} ({LogBundleBuilder.FormatBytes(decision.DeclaredBytes)}): player disconnected");
            }
            catch (Exception e)
            {
                LunaLog.Error($"[LogUpload] cleanup after disconnect failed: {e.Message}");
            }
        }

        /// <summary>Sends the reply a decision calls for and writes its one log line.</summary>
        private static void Report(LogUploadDecision decision, bool logRefusal = true)
        {
            var client = decision.Owner as ClientStructure;
            switch (decision.Outcome)
            {
                case LogUploadOutcome.Completed:
                    LunaLog.Normal($"[LogUpload] stored {decision.StoredName} ({LogBundleBuilder.FormatBytes(decision.StoredBytes)}, {decision.UploadReason}) from {decision.PlayerName} in {ClientLogsDirectory}");
                    if (decision.DeletedForQuota.Count > 0)
                        LunaLog.Debug($"[LogUpload] folder quota: deleted {string.Join(", ", decision.DeletedForQuota)}");
                    if (client != null && client.ConnectionStatus == ConnectionStatus.Connected)
                    {
                        var done = ServerContext.ServerMessageFactory.CreateNewMessageData<LogUploadDoneMsgData>();
                        done.UploadId = decision.UploadId;
                        done.StoredBytes = decision.StoredBytes;
                        done.StoredName = decision.StoredName;
                        MessageQueuer.SendToClient<LogUploadSrvMsg>(client, done);
                    }
                    break;
                case LogUploadOutcome.Refused:
                    if (logRefusal)
                        LunaLog.Warning($"[LogUpload] refused upload from {decision.PlayerName} ({LogBundleBuilder.FormatBytes(decision.DeclaredBytes)}): {decision.Reason} - {decision.Detail}");
                    if (client != null && client.ConnectionStatus == ConnectionStatus.Connected)
                    {
                        var refused = ServerContext.ServerMessageFactory.CreateNewMessageData<LogUploadRefusedMsgData>();
                        refused.UploadId = decision.UploadId;
                        refused.Reason = decision.Reason;
                        refused.Detail = decision.Detail;
                        MessageQueuer.SendToClient<LogUploadSrvMsg>(client, refused);
                    }
                    break;
                case LogUploadOutcome.Ignored:
                    // Chunks still in flight after a refusal land here; log at most one line per interval.
                    var nowTicks = DateTime.UtcNow.Ticks;
                    var lastTicks = Interlocked.Read(ref _lastIgnoredLogTicks);
                    if (nowTicks - lastTicks > IgnoredLogInterval.Ticks &&
                        Interlocked.CompareExchange(ref _lastIgnoredLogTicks, nowTicks, lastTicks) == lastTicks)
                        LunaLog.Debug($"[LogUpload] ignored a chunk from {decision.PlayerName}: {decision.Detail}");
                    break;
            }
        }

        private static void EnsureSweepTimer()
        {
            if (_sweepTimer != null) return;
            lock (TimerLock)
            {
                if (_sweepTimer == null)
                    _sweepTimer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
            }
        }

        private static void Sweep()
        {
            try
            {
                foreach (var decision in Assembler.SweepExpired())
                {
                    Report(decision);
                }
            }
            catch (Exception e)
            {
                LunaLog.Error($"[LogUpload] sweep failed: {e.Message}");
            }
        }
    }
}
