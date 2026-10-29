using LmpClient.Base;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.LogUpload;
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
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);
        private const string ZipFolderName = "VMP-LogUpload";

        private readonly object _lock = new object();
        private LogUploadPhase _phase = LogUploadPhase.Idle;
        private string _resultText = string.Empty;
        private int _uploadId;
        private string _zipPath;
        private int _totalBytes;
        private int _chunkSize;
        private int _chunkCount;
        private int _chunksSent;
        private long _packDone;
        private long _packTotal;
        private LogUploadReason _reason;
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
            lock (_lock)
            {
                uploadId = _uploadId;
                busy = IsBusyPhase(_phase);
            }

            if (busy)
            {
                Finish(uploadId, LogUploadPhase.Failed, "Disconnected before the upload finished.", false);
            }
            else
            {
                lock (_lock)
                {
                    _phase = LogUploadPhase.Idle;
                    _resultText = string.Empty;
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

        public string StatusText
        {
            get
            {
                lock (_lock)
                {
                    switch (_phase)
                    {
                        case LogUploadPhase.Packing:
                            return _packTotal > 0 ? $"Packing logs... {Percent(_packDone, _packTotal)}%" : "Packing logs...";
                        case LogUploadPhase.WaitingForServer:
                            return "Asking the server...";
                        case LogUploadPhase.Sending:
                            return $"Sending {Percent(_chunksSent, _chunkCount)}% of {LogBundleBuilder.FormatBytes(_totalBytes)}...";
                        case LogUploadPhase.Finishing:
                            return "Waiting for the server to confirm...";
                        default:
                            return _resultText;
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Called from the UI (main thread) for the player's own upload, and by <see cref="AutoReport"/> for automatic
        /// reports (<paramref name="reason"/> other than Manual, with <paramref name="trigger"/> saying what fired it).
        /// </summary>
        public void StartUpload(LogUploadReason reason = LogUploadReason.Manual, string trigger = null)
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

            LunaLog.Log(reason.IsAutomatic()
                ? $"[VMP]: Packing an automatic {reason} report to send to the host"
                : "[VMP]: Packing logs to send to the host");
            StartWorker("VMP log upload (pack)", () => PackAndBegin(request, uploadId, token));
        }

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

            StartWorker("VMP log upload (send)", () => StreamChunks(uploadId, token));
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
                _resultText = $"Sent: {LogBundleBuilder.FormatBytes(storedBytes)}, the host can read it now.";
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
            string text;
            bool automatic;
            lock (_lock)
            {
                if (uploadId != _uploadId || !IsBusyPhase(_phase)) return;
                var clean = CleanText(detail);
                text = "Not sent: " + DescribeRefusal(reason) + (clean.Length > 0 ? " " + clean : string.Empty);
                _phase = LogUploadPhase.Failed;
                _resultText = text;
                automatic = _reason.IsAutomatic();
                _cancel?.Cancel();
            }

            LunaLog.LogWarning($"[VMP]: Log upload refused by the server: {reason} {CleanText(detail)}");
            if (!automatic)
                LunaScreenMsg.PostScreenMessage(text, 10f, ScreenMessageStyle.UPPER_CENTER, UnityEngine.Color.yellow);
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
                        $"The logs are too big to send ({LogBundleBuilder.FormatBytes(result.ZipBytes)}, limit {LogBundleBuilder.FormatBytes(LogUploadLimits.MaxTotalBytes)}). The zip is in %TEMP%\\{ZipFolderName}.",
                        false);
                    return;
                }

                var totalBytes = (int)result.ZipBytes;
                var chunkSize = LogUploadLimits.ClientChunkBytes;
                var chunkCount = (totalBytes + chunkSize - 1) / chunkSize;
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
                Finish(uploadId, LogUploadPhase.Failed, $"Could not pack the logs: {e.Message}", false);
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
        /// in flight and a slow link automatically slows the upload down).
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
                var msPerChunk = chunkSize * 1000.0 / LogUploadLimits.ClientMaxBytesPerSecond;
                var clock = Stopwatch.StartNew();
                var nextSendMs = 0.0;

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
                                Finish(uploadId, LogUploadPhase.Failed, "Lost the connection while sending.", false);
                                return;
                            }

                            if (clock.ElapsedMilliseconds >= nextSendMs && inUse <= windowSize / 4) break;

                            if (clock.ElapsedMilliseconds - waitStartedMs > StallTimeout.TotalMilliseconds)
                            {
                                Finish(uploadId, LogUploadPhase.Failed, "The connection stalled while sending.", true);
                                return;
                            }

                            Thread.Sleep(10);
                        }

                        var count = (int)Math.Min(chunkSize, totalBytes - (long)index * chunkSize);
                        ReadExactly(file, buffer, count);
                        if (!MessageSender.SendChunkNow(uploadId, index, chunkCount, totalBytes, buffer, count))
                        {
                            Finish(uploadId, LogUploadPhase.Failed, "Lost the connection while sending.", false);
                            return;
                        }

                        nextSendMs = Math.Max(nextSendMs, clock.ElapsedMilliseconds) + msPerChunk;
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
                Finish(uploadId, LogUploadPhase.Failed, $"Sending failed: {e.Message}", true);
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
                Finish(uploadId, LogUploadPhase.Failed, "Disconnected before the upload finished.", false);
            else if (phase == LogUploadPhase.WaitingForServer && age > AcceptTimeout)
                Finish(uploadId, LogUploadPhase.Failed, "The server did not answer. Try again later.", true);
            else if (phase == LogUploadPhase.Finishing && age > DoneTimeout)
                Finish(uploadId, LogUploadPhase.Failed, "The server did not confirm the upload; ask the host whether it arrived.", true);
        }

        /// <summary>Ends the current upload (if <paramref name="uploadId"/> is still the current one and running).</summary>
        private void Finish(int uploadId, LogUploadPhase phase, string text, bool cancelOnServer)
        {
            bool sendCancel;
            bool automatic;
            lock (_lock)
            {
                if (uploadId != _uploadId || !IsBusyPhase(_phase)) return;
                sendCancel = cancelOnServer && _phase != LogUploadPhase.Packing;
                _phase = phase;
                _resultText = text;
                automatic = _reason.IsAutomatic();
                _cancel?.Cancel();
            }

            LunaLog.LogWarning($"[VMP]: Log upload stopped: {text}");
            if (!automatic)
                LunaScreenMsg.PostScreenMessage("Logs not sent: " + text, 10f, ScreenMessageStyle.UPPER_CENTER, UnityEngine.Color.yellow);
            if (sendCancel && MainSystem.NetworkState >= ClientState.Handshaked)
                MessageSender.SendCancel(uploadId);
        }

        #region Helpers

        private static bool IsBusyPhase(LogUploadPhase phase)
        {
            return phase == LogUploadPhase.Packing || phase == LogUploadPhase.WaitingForServer || phase == LogUploadPhase.Sending ||
                   phase == LogUploadPhase.Finishing;
        }

        private static void StartWorker(string name, ThreadStart work)
        {
            // Below-normal priority so packing a 150 MB log never competes with the game's main thread.
            var thread = new Thread(work) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
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
                case LogUploadRefusalReason.TooMany: return "too many uploads.";
                case LogUploadRefusalReason.TooFrequent: return "too soon after the last upload.";
                case LogUploadRefusalReason.BadFile: return "the server rejected the file.";
                case LogUploadRefusalReason.Busy: return "the server is busy.";
                case LogUploadRefusalReason.TimedOut: return "the upload timed out.";
                case LogUploadRefusalReason.NotAllowed: return "you must be connected to the server.";
                case LogUploadRefusalReason.Cancelled: return "cancelled.";
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
            sb.Append("Reason: ").Append(reason).Append(reason.IsAutomatic() ? " (automatic report)" : " (sent by the player)").Append('\n');
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
