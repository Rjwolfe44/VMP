using LmpClient.Base;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.LogUpload;
using LmpCommon.Message.Data.LogUpload;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LmpClient.Systems.LogUpload
{
    public enum LogUploadPhase
    {
        Idle,
        Packing,
        WaitingForServer,
        Sending,
        Finishing,
        Succeeded,
        Failed,
    }

    /// <summary>
    /// "Send logs to host": packs KSP.log, Player.log and VMP's own trace/log folders into a zip on a worker thread
    /// (account name hidden, settings files never included) and uploads it over the game connection in paced chunks.
    /// See <see cref="LogUploadLimits"/> for the protocol limits and Server.System.LogUploadSystem for the receiving end.
    /// </summary>
    public class LogUploadSystem : MessageSystem<LogUploadSystem, LogUploadMessageSender, LogUploadMessageHandler>
    {
        private sealed class PackLimits
        {
            public long KspLog;
            public long PlayerLog;
            public long PrevPlayerLog;
            public long ApexLog;
            public int ExtraFiles;
            public long ExtraFileBytes;
            public string Note;
        }

        // KSP.log compresses about 25:1, so the normal limits give a zip of roughly 10 MB at most.
        private static readonly PackLimits NormalLimits = new PackLimits
        {
            KspLog = 150L * 1024 * 1024, PlayerLog = 60L * 1024 * 1024, PrevPlayerLog = 40L * 1024 * 1024, ApexLog = 8L * 1024 * 1024,
            ExtraFiles = 20, ExtraFileBytes = 20L * 1024 * 1024, Note = string.Empty
        };

        private static readonly PackLimits SmallerLimits = new PackLimits
        {
            KspLog = 60L * 1024 * 1024, PlayerLog = 10L * 1024 * 1024, PrevPlayerLog = 8L * 1024 * 1024, ApexLog = 2L * 1024 * 1024,
            ExtraFiles = 5, ExtraFileBytes = 2L * 1024 * 1024,
            Note = "Packed with reduced limits so the zip fits the upload limit."
        };

        // No working compressor: everything is stored, so keep the total under the 25 MB upload limit.
        private static readonly PackLimits StoredLimits = new PackLimits
        {
            KspLog = 10L * 1024 * 1024, PlayerLog = 4L * 1024 * 1024, PrevPlayerLog = 2L * 1024 * 1024, ApexLog = 512L * 1024,
            ExtraFiles = 3, ExtraFileBytes = 1L * 1024 * 1024,
            Note = "No compressor was available; logs were trimmed to fit uncompressed."
        };

        private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(60);
        // A game starved of memory can freeze for minutes; the server waits 10 minutes for the next chunk, so the sender waits 5 for an acknowledgement.
        private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);
        private const string ZipFolderName = "VMP-LogUpload";

        private readonly object _lock = new object();
        private LogUploadPhase _phase = LogUploadPhase.Idle;
        private string _resultText = string.Empty;
        // The failure sentence without the countdown: the status line adds "Will retry in N minutes" live.
        private string _resultTextWithoutRetry = string.Empty;
        private int _uploadId;
        private string _zipPath;
        private int _totalBytes;
        private int _chunkSize;
        private int _chunkCount;
        private int _chunksSent;
        private long _packDone;
        private long _packTotal;
        private LogUploadReason _reason;
        private string _trigger;
        // The server this upload was for; a retry only goes to the same one.
        private string _attemptEndpoint = string.Empty;
        private readonly LogUploadRetryPlan _retry = new LogUploadRetryPlan();
        private DateTime _phaseStartedUtc;
        private CancellationTokenSource _cancel;

        #region Base overrides

        public override string SystemName { get; } = nameof(LogUploadSystem);

        /// <summary>Players stuck while joining need this most, so it works as soon as the server accepted the handshake.</summary>
        protected override ClientState EnableStage => ClientState.Handshaked;

        /// <summary>
        /// The network system moves on from Handshaked in the same frame it sees it, and only the last state of a frame is
        /// fired as an event, so the event for exactly Handshaked never arrives. Enable on any later state as well; without
        /// this the system stayed disabled for the whole session.
        /// </summary>
        protected override void NetworkEventHandler(ClientState data)
        {
            if (data <= ClientState.Disconnected)
                Enabled = false;
            else if (data >= EnableStage)
                Enabled = true;
        }

        protected override void OnEnabled()
        {
            base.OnEnabled();
            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, CheckTimeouts));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            int uploadId;
            bool busy;
            bool confirming;
            lock (_lock)
            {
                uploadId = _uploadId;
                busy = IsBusyPhase(_phase);
                confirming = _phase == LogUploadPhase.Finishing;
            }

            if (busy)
            {
                Finish(uploadId, LogUploadPhase.Failed, ConnectionLostText(confirming), false, transient: LogUploadRetry.CanRetryAfterConnectionLoss(confirming));
            }
            else
            {
                lock (_lock)
                {
                    // A retry that is still waiting keeps its text; it starts again once the player is back on a server.
                    if (!_retry.Pending)
                    {
                        _phase = LogUploadPhase.Idle;
                        _resultText = string.Empty;
                    }
                }
            }
        }

        #endregion

        #region Public state for the UI

        public LogUploadPhase Phase
        {
            get { lock (_lock) return _phase; }
        }

        public bool IsBusy => IsBusyPhase(Phase);

        public bool IsConnected => Enabled && MainSystem.NetworkState >= ClientState.Handshaked;

        public bool CanStartUpload => IsConnected && !IsBusy;

        /// <summary>Text for the options button: what is happening now ("Sending logs 37%"), or the normal label when idle.</summary>
        public string ButtonText
        {
            get
            {
                lock (_lock)
                {
                    switch (_phase)
                    {
                        case LogUploadPhase.Packing:
                            return _packTotal > 0 ? $"Packing logs {Percent(_packDone, _packTotal)}%" : "Packing logs...";
                        case LogUploadPhase.WaitingForServer:
                            return "Sending logs 0%";
                        case LogUploadPhase.Sending:
                            return $"Sending logs {Percent(_chunksSent, _chunkCount)}%";
                        case LogUploadPhase.Finishing:
                            return "Sending logs 100%";
                        default:
                            return SendLogsLabel;
                    }
                }
            }
        }

        public const string SendLogsLabel = "Send logs to host";

        /// <summary>One plain line under the button: the size while sending, the result afterwards ("Sent", "Failed: ..., will retry in 2 minutes").</summary>
        public string StatusText
        {
            get
            {
                lock (_lock)
                {
                    var retryPrefix = _retry.RetriesStarted > 0 && IsBusyPhase(_phase)
                        ? $"Retry {_retry.RetriesStarted} of {LogUploadRetry.MaxRetries}. " : string.Empty;
                    switch (_phase)
                    {
                        case LogUploadPhase.Packing:
                            return retryPrefix + "Packing your logs, this can take a few seconds.";
                        case LogUploadPhase.WaitingForServer:
                            return retryPrefix + "Asking the server...";
                        case LogUploadPhase.Sending:
                            return retryPrefix + $"Sending {LogBundleBuilder.FormatBytes(_totalBytes)} to the host. The game can keep running.";
                        case LogUploadPhase.Finishing:
                            return retryPrefix + "Waiting for the server to confirm...";
                        default:
                            if (_retry.Pending && _phase == LogUploadPhase.Failed)
                                return RetryText(_resultTextWithoutRetry, _retry.TimeLeft(DateTime.UtcNow));
                            return _resultText;
                    }
                }
            }
        }

        private static string ConnectionLostText(bool waitingForConfirmation)
        {
            return waitingForConfirmation
                ? "the connection to the server was lost before it confirmed the upload; ask the host whether it arrived."
                : "the connection to the server was lost.";
        }

        private static string RetryText(string failure, TimeSpan left)
        {
            return $"{failure} Will retry in {LogUploadRetry.FormatDelay(left)}.";
        }

        #endregion

        /// <summary>
        /// Called from the UI (main thread) for the player's own upload, and by <see cref="AutoReport"/> for automatic
        /// reports (<paramref name="reason"/> other than Manual, with <paramref name="trigger"/> saying what fired it).
        /// </summary>
        public void StartUpload(LogUploadReason reason = LogUploadReason.Manual, string trigger = null)
        {
            StartAttempt(reason, trigger, isRetry: false);
        }

        /// <summary>One attempt: packs the logs and asks the server. A new upload (<paramref name="isRetry"/> false) replaces a waiting retry.</summary>
        private void StartAttempt(LogUploadReason reason, string trigger, bool isRetry)
        {
            if (!CanStartUpload) return;

            // Unity and KSP calls stay on the main thread; the worker only gets plain strings.
            var playerLog = FindPlayerLog();
            var request = new PackRequest
            {
                KspLogPath = Path.Combine(KSPUtil.ApplicationRootPath, "KSP.log"),
                PlayerLogPath = playerLog,
                PrevPlayerLogPath = string.IsNullOrEmpty(playerLog) ? null : Path.Combine(Path.GetDirectoryName(playerLog) ?? string.Empty, "Player-prev.log"),
                ApexLogPath = Path.Combine(KSPUtil.ApplicationRootPath, "ApexGuidance.log"),
                ApexLogsFolder = CommonUtil.CombinePaths(MainSystem.KspPath, "GameData", "ApexGuidance", "Plugins", "PluginData", "ApexGuidance", "Logs"),
                VmpFolder = CommonUtil.CombinePaths(MainSystem.KspPath, "GameData", "VladMultiplayer"),
                InfoText = BuildInfoText(reason, trigger),
                AccountNames = GetAccountNames(),
                Reason = reason,
            };

            int uploadId;
            CancellationToken token;
            lock (_lock)
            {
                if (IsBusyPhase(_phase)) return;
                if (!isRetry) _retry.Reset();
                _trigger = trigger;
                _attemptEndpoint = CurrentEndpoint();
                _uploadId = NewUploadId(_uploadId);
                _zipPath = null;
                _totalBytes = _chunkSize = _chunkCount = _chunksSent = 0;
                _packDone = _packTotal = 0;
                _resultText = string.Empty;
                _phase = LogUploadPhase.Packing;
                _reason = reason;
                _phaseStartedUtc = DateTime.UtcNow;
                _cancel = new CancellationTokenSource();
                uploadId = _uploadId;
                token = _cancel.Token;
            }

            LunaLog.Log(reason == LogUploadReason.Requested
                ? "[VMP]: Packing the logs the host asked for"
                : reason.IsAutomatic()
                    ? $"[VMP]: Packing an automatic {reason} report to send to the host"
                    : "[VMP]: Packing logs to send to the host");
            StartWorker("VMP log upload (pack)", () => PackAndBegin(request, uploadId, token));
        }

        #region Host requests (main thread)

        // One host request can wait here (until the scene is usable); the next one is ignored while it does.
        private bool _hasPendingRequest;
        private int _pendingRequestId;
        private string _pendingRequestNote = string.Empty;
        private DateTime _pendingRequestUtc;
        private DateTime _lastHonouredRequestUtc = DateTime.MinValue;

        /// <summary>
        /// The server host asked for this player's logs. Nothing happens unless the player allows automatic diagnostics in the
        /// options (one log line says so). A request is ignored while an upload is running, while an earlier request still waits and
        /// when another one was honoured less than <see cref="LogUploadLimits.ClientMinTimeBetweenRequests"/> ago. The client's own
        /// automatic-report limits (20 minutes, 8 a day) do not apply to it. In the loading screen or main menu it waits for
        /// <see cref="TryStartPendingRequest"/>, which AutoReport also calls every few seconds.
        /// </summary>
        internal void OnLogsRequested(int requestId, string note)
        {
            if (!SettingsSystem.CurrentSettings.AutoSendDiagnostics)
            {
                LunaLog.Log($"[VMP]: The server host asked for the game logs (request {requestId}); sending diagnostics is turned off in the options, so nothing is sent");
                return;
            }

            string ignored = null;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (IsBusyPhase(_phase))
                    ignored = "a log upload is already running";
                else if (_hasPendingRequest)
                    ignored = "an earlier request is still waiting";
                else if (now - _lastHonouredRequestUtc < LogUploadLimits.ClientMinTimeBetweenRequests)
                    ignored = "another request was honoured less than a minute ago";
                else
                {
                    _lastHonouredRequestUtc = now;
                    _hasPendingRequest = true;
                    _pendingRequestId = requestId;
                    _pendingRequestNote = LogUploadRequestMsgData.CleanNote(note);
                    _pendingRequestUtc = now;
                }
            }

            if (ignored != null)
            {
                LunaLog.Log($"[VMP]: Ignored the host's request for the game logs (request {requestId}): {ignored}");
                return;
            }

            TryStartPendingRequest();
        }

        /// <summary>
        /// Starts the upload for a waiting host request when everything allows it. Keeps the request while the scene is LOADING or
        /// MAINMENU; drops it when it expired, the option was turned off, the connection is gone or another upload is running.
        /// Returns true when the upload was started.
        /// </summary>
        internal bool TryStartPendingRequest()
        {
            int requestId;
            string note;
            string dropped = null;
            lock (_lock)
            {
                if (!_hasPendingRequest) return false;

                if (DateTime.UtcNow - _pendingRequestUtc > LogUploadLimits.RequestValidity) dropped = "it expired";
                else if (!SettingsSystem.CurrentSettings.AutoSendDiagnostics) dropped = "sending diagnostics was turned off in the options";
                else if (!IsConnected) dropped = "the connection was lost";
                else if (IsBusyPhase(_phase)) dropped = "a log upload is already running";
                else
                {
                    var scene = HighLogic.LoadedScene;
                    if (scene == GameScenes.LOADING || scene == GameScenes.MAINMENU) return false;
                }

                _hasPendingRequest = false;
                requestId = _pendingRequestId;
                note = _pendingRequestNote;
            }

            if (dropped != null)
            {
                LunaLog.Log($"[VMP]: Dropped the host's request for the game logs (request {requestId}): {dropped}");
                return false;
            }

            LunaLog.Log($"[VMP]: The server host asked for the game logs (request {requestId}); sending them now");
            LunaScreenMsg.PostScreenMessage("The server host asked for your game logs, sending them now.", 8f, ScreenMessageStyle.UPPER_CENTER);
            StartUpload(LogUploadReason.Requested, note.Length > 0 ? "requested by the host: " + note : "requested by the host");
            return true;
        }

        #endregion

        #region Server replies (main thread, via the message handler)

        internal void OnAccepted(int uploadId)
        {
            CancellationToken token;
            lock (_lock)
            {
                if (uploadId != _uploadId || _phase != LogUploadPhase.WaitingForServer) return;
                _phase = LogUploadPhase.Sending;
                _phaseStartedUtc = DateTime.UtcNow;
                token = _cancel.Token;
            }

            StartWorker("VMP log upload (send)", () => StreamChunks(uploadId, token), ThreadPriority.Normal);
        }

        internal void OnDone(int uploadId, long storedBytes, string storedName)
        {
            string zipPath;
            bool automatic;
            lock (_lock)
            {
                // Done can overtake the sender thread's switch to Finishing.
                if (uploadId != _uploadId || (_phase != LogUploadPhase.Sending && _phase != LogUploadPhase.Finishing)) return;
                _phase = LogUploadPhase.Succeeded;
                _resultText = $"Sent ({LogBundleBuilder.FormatBytes(storedBytes)}). The host can read your logs now.";
                _resultTextWithoutRetry = _resultText;
                _retry.Reset();
                zipPath = _zipPath;
                automatic = _reason.IsAutomatic();
                _cancel?.Cancel();
            }

            LunaLog.Log($"[VMP]: Logs sent to the host ({LogBundleBuilder.FormatBytes(storedBytes)}, stored as {CleanText(storedName)})");
            // Automatic reports stay quiet: a message in the middle of a flight would only be a distraction.
            if (!automatic)
                LunaScreenMsg.PostScreenMessage($"Logs sent to the host ({LogBundleBuilder.FormatBytes(storedBytes)}).", 8f, ScreenMessageStyle.UPPER_CENTER);
            TryDelete(zipPath);
        }

        internal void OnRefused(int uploadId, LogUploadRefusalReason reason, string detail)
        {
            var clean = CleanText(detail);
            LunaLog.LogWarning($"[VMP]: Log upload refused by the server: {reason} {clean}");
            // The server already dropped the upload, so there is nothing to cancel there.
            Finish(uploadId, LogUploadPhase.Failed, DescribeRefusal(reason) + (clean.Length > 0 ? " " + clean : string.Empty), false,
                transient: LogUploadRetry.IsTransient(reason));
        }

        #endregion

        #region Workers

        private sealed class PackRequest
        {
            public string KspLogPath;
            public string PlayerLogPath;
            public string PrevPlayerLogPath;
            public string ApexLogPath;
            public string ApexLogsFolder;
            public string VmpFolder;
            public string InfoText;
            public List<string> AccountNames;
            public LogUploadReason Reason;
        }

        private void PackAndBegin(PackRequest request, int uploadId, CancellationToken token)
        {
            try
            {
                var deflate = LogUploadDeflate.GetFactory(out var deflateName);
                var folder = Path.Combine(Path.GetTempPath(), ZipFolderName);
                Directory.CreateDirectory(folder);
                DeleteOldZips(folder);
                // The upload id keeps a quick retry from colliding with a cancelled worker still closing its file.
                var zipPath = Path.Combine(folder, $"VMP-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{uploadId:x8}.zip");

                var result = Pack(request, zipPath, deflate, deflateName, deflate != null ? NormalLimits : StoredLimits, uploadId, token);
                if (result.ZipBytes > LogUploadLimits.MaxTotalBytes && deflate != null)
                {
                    LunaLog.Log($"[VMP]: Log bundle is {LogBundleBuilder.FormatBytes(result.ZipBytes)}; packing again with smaller limits");
                    result = Pack(request, zipPath, deflate, deflateName, SmallerLimits, uploadId, token);
                }

                if (result.ZipBytes > LogUploadLimits.MaxTotalBytes)
                {
                    Finish(uploadId, LogUploadPhase.Failed,
                        $"the logs are too big to send ({LogBundleBuilder.FormatBytes(result.ZipBytes)}, limit {LogBundleBuilder.FormatBytes(LogUploadLimits.MaxTotalBytes)}). The zip is in %TEMP%\\{ZipFolderName}.",
                        false);
                    return;
                }

                var totalBytes = (int)result.ZipBytes;
                var chunkSize = LogUploadLimits.ClientChunkBytes;
                var chunkCount = LogUploadChunkPlan.ChunkCount(totalBytes, chunkSize);
                lock (_lock)
                {
                    if (uploadId != _uploadId || _phase != LogUploadPhase.Packing) return;
                    _zipPath = zipPath;
                    _totalBytes = totalBytes;
                    _chunkSize = chunkSize;
                    _chunkCount = chunkCount;
                    _phase = LogUploadPhase.WaitingForServer;
                    _phaseStartedUtc = DateTime.UtcNow;
                }

                LunaLog.Log($"[VMP]: Log bundle ready: {LogBundleBuilder.FormatBytes(totalBytes)} (compression: {deflateName}); asking the server to accept it");
                MessageSender.SendBegin(uploadId, totalBytes, chunkSize, chunkCount, request.Reason);
            }
            catch (OperationCanceledException)
            {
                // Finish() already set the result.
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[VMP]: Packing logs failed: {e}");
                Finish(uploadId, LogUploadPhase.Failed, $"could not pack the logs ({e.Message}).", false);
            }
        }

        private LogBundleResult Pack(PackRequest request, string zipPath, Func<Stream, Stream> deflate, string deflateName, PackLimits limits,
            int uploadId, CancellationToken token)
        {
            var sources = new List<LogBundleSource>
            {
                new LogBundleSource { EntryName = "KSP.log", Path = request.KspLogPath, MaxBytes = limits.KspLog },
            };

            if (!string.IsNullOrEmpty(request.PlayerLogPath))
                sources.Add(new LogBundleSource { EntryName = "Player.log", Path = request.PlayerLogPath, MaxBytes = limits.PlayerLog });

            // The previous session's log: after a crash or a killed freeze this is the only copy (KSP.log is overwritten at start).
            if (!string.IsNullOrEmpty(request.PrevPlayerLogPath) && File.Exists(request.PrevPlayerLogPath))
                sources.Add(new LogBundleSource { EntryName = "Player-prev.log", Path = request.PrevPlayerLogPath, MaxBytes = limits.PrevPlayerLog });

            // ApexGuidance's own always-on log and its newest telemetry/report files (name and extension filters apply).
            if (!string.IsNullOrEmpty(request.ApexLogPath) && File.Exists(request.ApexLogPath))
                sources.Add(new LogBundleSource { EntryName = "ApexGuidance.log", Path = request.ApexLogPath, MaxBytes = limits.ApexLog });
            sources.AddRange(LogBundleBuilder.CollectFolder(request.ApexLogsFolder, "ApexGuidance/Logs", Math.Min(limits.ExtraFiles, 6), Math.Min(limits.ExtraFileBytes, 3L * 1024 * 1024)));

            sources.AddRange(LogBundleBuilder.CollectFolder(Path.Combine(request.VmpFolder, "Traces"), "VladMultiplayer/Traces", limits.ExtraFiles, limits.ExtraFileBytes));
            sources.AddRange(LogBundleBuilder.CollectFolder(Path.Combine(request.VmpFolder, "logs"), "VladMultiplayer/logs", limits.ExtraFiles, limits.ExtraFileBytes));

            var info = new StringBuilder(request.InfoText);
            info.Append("Compression: ").Append(deflateName).Append('\n');
            if (!string.IsNullOrEmpty(limits.Note)) info.Append(limits.Note).Append('\n');

            var options = new LogBundleOptions
            {
                OutputPath = zipPath,
                InfoText = info.ToString(),
                Sources = sources,
                RedactNames = request.AccountNames,
                DeflateFactory = deflate,
                Timestamp = DateTime.Now,
            };

            return LogBundleBuilder.Build(options, token, (done, total) =>
            {
                lock (_lock)
                {
                    if (uploadId != _uploadId) return;
                    _packDone = done;
                    _packTotal = total;
                }
            });
        }

        /// <summary>
        /// Streams the zip in <see cref="LogUploadLimits.ClientChunkBytes"/> chunks, paced two ways so gameplay traffic
        /// never waits behind the upload: at most <see cref="LogUploadLimits.ClientMaxBytesPerSecond"/>, and a new chunk
        /// only once the upload channel's earlier fragments are mostly acknowledged (so at most about one chunk is ever
        /// in flight and a slow link automatically slows the upload down). It runs on its own thread, so it does not depend on the
        /// frame rate: a game that freezes for a minute only delays the next chunk, and the server waits ten minutes for it.
        /// </summary>
        private void StreamChunks(int uploadId, CancellationToken token)
        {
            string zipPath;
            int totalBytes, chunkSize, chunkCount;
            lock (_lock)
            {
                if (uploadId != _uploadId) return;
                zipPath = _zipPath;
                totalBytes = _totalBytes;
                chunkSize = _chunkSize;
                chunkCount = _chunkCount;
            }

            try
            {
                var buffer = new byte[chunkSize];
                var pacer = new LogUploadPacer(chunkSize, LogUploadLimits.ClientMaxBytesPerSecond);
                var clock = Stopwatch.StartNew();

                using (var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    for (var index = 0; index < chunkCount; index++)
                    {
                        var waitStartedMs = clock.ElapsedMilliseconds;
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            var inUse = LogUploadMessageSender.UploadChannelSlotsInUse(out var windowSize);
                            if (inUse < 0)
                            {
                                Finish(uploadId, LogUploadPhase.Failed, "the connection to the server was lost.", false, transient: true);
                                return;
                            }

                            if (pacer.TrySend(clock.ElapsedMilliseconds, inUse, windowSize)) break;

                            if (clock.ElapsedMilliseconds - waitStartedMs > StallTimeout.TotalMilliseconds)
                            {
                                Finish(uploadId, LogUploadPhase.Failed, "the connection stalled while sending.", true, transient: true);
                                return;
                            }

                            Thread.Sleep(10);
                        }

                        var count = LogUploadChunkPlan.ChunkLength(totalBytes, chunkSize, index);
                        ReadExactly(file, buffer, count);
                        if (!MessageSender.SendChunkNow(uploadId, index, chunkCount, totalBytes, buffer, count))
                        {
                            Finish(uploadId, LogUploadPhase.Failed, "the connection to the server was lost.", false, transient: true);
                            return;
                        }

                        lock (_lock)
                        {
                            if (uploadId == _uploadId) _chunksSent = index + 1;
                        }
                    }
                }

                lock (_lock)
                {
                    if (uploadId == _uploadId && _phase == LogUploadPhase.Sending)
                    {
                        _phase = LogUploadPhase.Finishing;
                        _phaseStartedUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Done, refused, disconnected or timed out: the result is already set.
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[VMP]: Sending logs failed: {e}");
                Finish(uploadId, LogUploadPhase.Failed, $"sending failed ({e.Message}).", true);
            }
            finally
            {
                // If Done overtook this thread, OnDone could not delete the zip while it was still open here.
                bool succeeded;
                lock (_lock)
                {
                    succeeded = uploadId == _uploadId && _phase == LogUploadPhase.Succeeded;
                }

                if (succeeded) TryDelete(zipPath);
            }
        }

        #endregion

        /// <summary>Main-thread watchdog for replies that never come.</summary>
        private void CheckTimeouts()
        {
            LogUploadPhase phase;
            DateTime started;
            int uploadId;
            lock (_lock)
            {
                phase = _phase;
                started = _phaseStartedUtc;
                uploadId = _uploadId;
            }

            var age = DateTime.UtcNow - started;
            if (IsBusyPhase(phase) && MainSystem.NetworkState < ClientState.Handshaked)
            {
                var confirming = phase == LogUploadPhase.Finishing;
                Finish(uploadId, LogUploadPhase.Failed, ConnectionLostText(confirming), false, transient: LogUploadRetry.CanRetryAfterConnectionLoss(confirming));
            }
            else if (phase == LogUploadPhase.WaitingForServer && age > AcceptTimeout)
                Finish(uploadId, LogUploadPhase.Failed, "the server did not answer.", true, transient: true);
            else if (phase == LogUploadPhase.Finishing && age > DoneTimeout)
                // The upload may have arrived, so no automatic retry (it would send a second copy); the host can check.
                Finish(uploadId, LogUploadPhase.Failed, "the server did not confirm the upload; ask the host whether it arrived.", true);
            else
                TryStartDueRetry();
        }

        /// <summary>Ends the current upload (if <paramref name="uploadId"/> is still the current one and running).</summary>
        /// <param name="transient">The failure is nobody's fault in particular (timeout, lost connection, busy server): schedule a retry.</param>
        private void Finish(int uploadId, LogUploadPhase phase, string text, bool cancelOnServer, bool transient = false)
        {
            bool sendCancel;
            bool automatic;
            string display;
            TimeSpan? retryIn = null;
            lock (_lock)
            {
                if (uploadId != _uploadId || !IsBusyPhase(_phase)) return;
                sendCancel = cancelOnServer && _phase != LogUploadPhase.Packing;
                _phase = phase;
                automatic = _reason.IsAutomatic();
                if (phase == LogUploadPhase.Failed)
                {
                    retryIn = _retry.OnAttemptFailed(DateTime.UtcNow, transient);
                    _resultTextWithoutRetry = "Failed: " + text;
                    _resultText = retryIn.HasValue
                        ? RetryText(_resultTextWithoutRetry, retryIn.Value)
                        : _resultTextWithoutRetry + (transient ? " Giving up for now, press the button to try again." : string.Empty);
                    display = _resultText;
                }
                else
                {
                    _resultText = text;
                    _resultTextWithoutRetry = text;
                    display = text;
                }

                _cancel?.Cancel();
            }

            LunaLog.LogWarning($"[VMP]: Log upload stopped: {display}");
            if (!automatic)
                LunaScreenMsg.PostScreenMessage("Logs not sent. " + display.Replace("Failed: ", string.Empty), 10f, ScreenMessageStyle.UPPER_CENTER, UnityEngine.Color.yellow);
            if (sendCancel && MainSystem.NetworkState >= ClientState.Handshaked)
                MessageSender.SendCancel(uploadId);
        }

        /// <summary>
        /// Starts the scheduled retry when it is due and the player is connected and idle; forgets it when it could not start for a long time.
        /// A retry sends the zip that is already packed (same content, so it is the same report), packs again only if the file is gone, and
        /// does not touch AutoReport counters: an automatic report counts once however often it is sent.
        /// </summary>
        private void TryStartDueRetry()
        {
            LogUploadReason reason;
            string trigger, zipPath;
            int totalBytes;
            int retryNumber;
            lock (_lock)
            {
                if (!_retry.Pending || IsBusyPhase(_phase)) return;
                var now = DateTime.UtcNow;
                if (_retry.HasExpired(now))
                {
                    _retry.Reset();
                    _resultText = _resultTextWithoutRetry + " The automatic retry was dropped.";
                    return;
                }

                if (!_retry.IsDue(now) || !IsConnected) return;
                var drop = LogUploadRetry.DropReason(_reason.IsAutomatic(), SettingsSystem.CurrentSettings.AutoSendDiagnostics, _attemptEndpoint, CurrentEndpoint());
                if (drop != null)
                {
                    _retry.Reset();
                    _resultText = _resultTextWithoutRetry + " The automatic retry was cancelled: " + drop + ".";
                    LunaLog.Log($"[VMP]: Dropped the log upload retry: {drop}");
                    return;
                }

                _retry.MarkStarted();
                retryNumber = _retry.RetriesStarted;
                reason = _reason;
                trigger = _trigger;
                zipPath = _zipPath;
                totalBytes = _totalBytes;
            }

            LunaLog.Log($"[VMP]: Trying the log upload again (retry {retryNumber} of {LogUploadRetry.MaxRetries})");
            if (!string.IsNullOrEmpty(zipPath) && ZipStillMatches(zipPath, totalBytes))
                BeginSendOfPackedZip(reason, zipPath, totalBytes);
            else
                StartAttempt(reason, trigger, isRetry: true);
        }

        private static string CurrentEndpoint()
        {
            try
            {
                return NetworkMain.ClientConnection?.ServerConnection?.RemoteEndPoint?.ToString() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool ZipStillMatches(string path, int totalBytes)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length == totalBytes;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void BeginSendOfPackedZip(LogUploadReason reason, string zipPath, int totalBytes)
        {
            int uploadId, chunkSize, chunkCount;
            lock (_lock)
            {
                if (IsBusyPhase(_phase)) return;
                _uploadId = NewUploadId(_uploadId);
                _zipPath = zipPath;
                _totalBytes = totalBytes;
                chunkSize = _chunkSize = LogUploadLimits.ClientChunkBytes;
                chunkCount = _chunkCount = LogUploadChunkPlan.ChunkCount(totalBytes, chunkSize);
                _chunksSent = 0;
                _resultText = string.Empty;
                _phase = LogUploadPhase.WaitingForServer;
                _reason = reason;
                _phaseStartedUtc = DateTime.UtcNow;
                _cancel = new CancellationTokenSource();
                _attemptEndpoint = CurrentEndpoint();
                uploadId = _uploadId;
            }

            MessageSender.SendBegin(uploadId, totalBytes, chunkSize, chunkCount, reason);
        }

        #region Helpers

        private static bool IsBusyPhase(LogUploadPhase phase)
        {
            return phase == LogUploadPhase.Packing || phase == LogUploadPhase.WaitingForServer || phase == LogUploadPhase.Sending ||
                   phase == LogUploadPhase.Finishing;
        }

        private static void StartWorker(string name, ThreadStart work, ThreadPriority priority = ThreadPriority.BelowNormal)
        {
            // Packing a 150 MB log runs below normal priority so it never competes with the game's main thread. The sender
            // only reads a file and hands bytes to the network, and a starved game must not starve it too, so it runs at normal priority.
            var thread = new Thread(work) { IsBackground = true, Name = name, Priority = priority };
            thread.Start();
        }

        private static int NewUploadId(int previous)
        {
            int id;
            do
            {
                id = Guid.NewGuid().GetHashCode() & int.MaxValue;
            } while (id == 0 || id == previous);

            return id;
        }

        private static int Percent(long done, long total)
        {
            if (total <= 0) return 0;
            return (int)Math.Max(0, Math.Min(100, done * 100 / total));
        }

        private static string DescribeRefusal(LogUploadRefusalReason reason)
        {
            switch (reason)
            {
                case LogUploadRefusalReason.TooLarge: return "the logs are too big.";
                case LogUploadRefusalReason.TooMany: return "too many uploads for now.";
                case LogUploadRefusalReason.TooFrequent: return "too soon after the last upload.";
                case LogUploadRefusalReason.BadFile: return "the server rejected the file.";
                case LogUploadRefusalReason.Busy: return "the server is busy with other uploads.";
                case LogUploadRefusalReason.TimedOut: return "the upload took too long (the game or the connection was too slow).";
                case LogUploadRefusalReason.NotAllowed: return "the server does not accept this upload.";
                case LogUploadRefusalReason.Cancelled: return "the upload was cancelled.";
                case LogUploadRefusalReason.ServerError: return "the server could not save it.";
                default: return "refused by the server.";
            }
        }

        /// <summary>Server text is shown in the UI: keep it to one short printable line.</summary>
        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(Math.Min(text.Length, LogUploadLimits.MaxReplyTextLength));
            foreach (var c in text)
            {
                if (sb.Length >= LogUploadLimits.MaxReplyTextLength) break;
                sb.Append(char.IsControl(c) ? ' ' : c);
            }

            return sb.ToString().Trim();
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException("The log zip is shorter than expected");
                offset += read;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A leftover zip in %TEMP% is cleaned up by the next upload.
            }
        }

        private static void DeleteOldZips(string folder)
        {
            try
            {
                foreach (var old in new DirectoryInfo(folder).GetFiles("VMP-logs-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(2))
                {
                    TryDelete(old.FullName);
                }
            }
            catch (Exception)
            {
                // Not important.
            }
        }

        private static List<string> GetAccountNames()
        {
            var names = new List<string>();
            try
            {
                names.Add(Environment.UserName);
                names.Add(Environment.GetEnvironmentVariable("USERNAME"));
                names.Add(Environment.GetEnvironmentVariable("USER"));
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) names.Add(Path.GetFileName(profile.TrimEnd('\\', '/')));
            }
            catch (Exception)
            {
                // Redact what we have.
            }

            return names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FindPlayerLog()
        {
            var candidates = new List<string>();
            try
            {
                var unityPath = UnityEngine.Application.consoleLogPath;
                if (!string.IsNullOrEmpty(unityPath)) candidates.Add(unityPath);
            }
            catch (Exception)
            {
                // Older Unity or a headless run: use the known locations.
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                candidates.Add(CommonUtil.CombinePaths(home, "AppData", "LocalLow", "Squad", "Kerbal Space Program", "Player.log"));
                candidates.Add(CommonUtil.CombinePaths(home, ".config", "unity3d", "Squad", "Kerbal Space Program", "Player.log"));
                candidates.Add(CommonUtil.CombinePaths(home, "Library", "Logs", "Squad", "Kerbal Space Program", "Player.log"));
                candidates.Add(CommonUtil.CombinePaths(home, "Library", "Logs", "Unity", "Player.log"));
            }

            return candidates.FirstOrDefault(p =>
            {
                try
                {
                    return File.Exists(p);
                }
                catch (Exception)
                {
                    return false;
                }
            });
        }

        private static string BuildInfoText(LogUploadReason reason, string trigger)
        {
            var sb = new StringBuilder();
            sb.Append("VMP log upload\n");
            sb.Append("Created (UTC): ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("Reason: ").Append(reason).Append(reason == LogUploadReason.Requested ? " (requested by the host)" : reason.IsAutomatic() ? " (automatic report)" : " (sent by the player)").Append('\n');
            if (!string.IsNullOrEmpty(trigger)) sb.Append("Trigger: ").Append(trigger).Append('\n');
            sb.Append("Player: ").Append(SettingsSystem.CurrentSettings.PlayerName).Append('\n');
            sb.Append("VMP build: ").Append(SessionAdmission.LocalExactBuild).Append('\n');
            sb.Append("KSP version: ").Append(Versioning.version_major).Append('.').Append(Versioning.version_minor).Append('.').Append(Versioning.Revision).Append('\n');
            sb.Append("Unity: ").Append(UnityEngine.Application.unityVersion).Append('\n');
            sb.Append("OS: ").Append(UnityEngine.SystemInfo.operatingSystem).Append('\n');
            sb.Append("CPU: ").Append(UnityEngine.SystemInfo.processorType).Append(" (").Append(UnityEngine.SystemInfo.processorCount).Append(" threads)\n");
            sb.Append("RAM: ").Append(UnityEngine.SystemInfo.systemMemorySize).Append(" MB\n");
            sb.Append("GPU: ").Append(UnityEngine.SystemInfo.graphicsDeviceName).Append(" (").Append(UnityEngine.SystemInfo.graphicsMemorySize)
                .Append(" MB, ").Append(UnityEngine.SystemInfo.graphicsDeviceVersion).Append(")\n");
            sb.Append("Server: ").Append(DescribeServer()).Append('\n');
            sb.Append("Scene: ").Append(HighLogic.LoadedScene).Append('\n');
            sb.Append("Connection state: ").Append(MainSystem.NetworkState).Append('\n');
            sb.Append("Automatic reports enabled: ").Append(SettingsSystem.CurrentSettings.AutoSendDiagnostics).Append('\n');
            sb.Append('\n').Append("Client health digest\n").Append("--------------------\n").Append(ClientHealth.BuildSummary());
            return sb.ToString();
        }

        private static string DescribeServer()
        {
            try
            {
                var endpoint = NetworkMain.ClientConnection?.ServerConnection?.RemoteEndPoint;
                if (endpoint == null) return "unknown";

                // Saved server name only (never its password), when it can be told apart by port.
                var saved = SettingsSystem.CurrentSettings.Servers.Where(s => s.Port == endpoint.Port).ToList();
                return saved.Count == 1 ? $"{saved[0].Name} ({endpoint})" : endpoint.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        #endregion
    }
}
