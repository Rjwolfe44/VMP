using LmpCommon.LogUpload;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.System.LogUpload
{
    public enum LogUploadOutcome
    {
        /// <summary>Begin accepted: reply Accepted.</summary>
        Accepted,
        /// <summary>Chunk written, more expected: no reply.</summary>
        ChunkStored,
        /// <summary>Last chunk written and the file stored: reply Done.</summary>
        Completed,
        /// <summary>Refused, or the upload was aborted: reply Refused.</summary>
        Refused,
        /// <summary>Nothing to do (no matching upload): no reply.</summary>
        Ignored,
    }

    public sealed class LogUploadDecision
    {
        public LogUploadOutcome Outcome;
        public object Owner;
        /// <summary>Sanitized player name (safe for logs and file names).</summary>
        public string PlayerName = string.Empty;
        public int UploadId;
        public long DeclaredBytes;
        public LogUploadRefusalReason Reason;
        public string Detail = string.Empty;
        public string StoredName = string.Empty;
        public string StoredPath = string.Empty;
        public long StoredBytes;
        public List<string> DeletedForQuota = new List<string>();

        /// <summary>Why the client sent the bundle (manual click or one of the automatic triggers).</summary>
        public LogUploadReason UploadReason;
    }

    /// <summary>
    /// Server side of "send logs to host": validates and reassembles chunked zip uploads into the ClientLogs folder.
    /// The client is untrusted. Only authenticated players, at most one upload per player and
    /// <see cref="LogUploadLimits.MaxConcurrentUploadsServerWide"/> at once, hourly caps, a size cap, strict chunk order,
    /// zip magic and exact size checks, inactivity and lifetime timeouts, and a folder quota. Chunks are spooled to a temp
    /// file in the target folder (never held in RAM) and the stored name is generated here from the sanitized player
    /// name and the UTC time; nothing the client sends is used in a path. The content is never extracted or parsed.
    /// Thread-safe; pure logic apart from file IO so it can be unit tested.
    /// </summary>
    public sealed class LogUploadAssembler : IDisposable
    {
        public const string TempFilePrefix = ".incoming-";
        public const string TempFileExtension = ".part";
        public const string IndexFileName = "index.jsonl";
        public const int MaxSafeNameLength = 15;

        private const long IndexMaxBytes = 256 * 1024;
        private const int IndexKeepLines = 1000;

        private static readonly byte[] ZipMagic = { (byte)'P', (byte)'K', 3, 4 };
        private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
        private static readonly TimeSpan AutoDayWindow = TimeSpan.FromHours(24);

        private sealed class Session
        {
            public object Owner;
            public string Key;
            public string SafeName;
            public int UploadId;
            public int TotalBytes;
            public int ChunkSize;
            public int ChunkCount;
            public int NextChunk;
            public long Received;
            public LogUploadReason Reason;
            public FileStream Stream;
            public string TempPath;
            public DateTime StartedUtc;
            public DateTime LastActivityUtc;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DateTime>> _playerHistory = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        // Automatic reports are counted apart from the player's own uploads (24 h window; the hourly cap is a subset).
        private readonly Dictionary<string, List<DateTime>> _autoHistory = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        private readonly List<DateTime> _globalHistory = new List<DateTime>();
        private readonly Dictionary<string, DateTime> _lastEnded = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Func<DateTime> _utcNow;
        private readonly Func<string, long> _freeDiskBytes;
        private readonly string _folderFull;
        private readonly int _folderMaxFiles;
        private readonly long _folderMaxBytes;
        private bool _folderReady;

        public LogUploadAssembler(string folder, Func<DateTime> utcNow = null, Func<string, long> freeDiskBytes = null,
            int folderMaxFiles = LogUploadLimits.FolderMaxFiles, long folderMaxBytes = LogUploadLimits.FolderMaxBytes)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("Folder required", nameof(folder));
            _folderFull = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _freeDiskBytes = freeDiskBytes ?? DefaultFreeDiskBytes;
            _folderMaxFiles = Math.Max(1, folderMaxFiles);
            _folderMaxBytes = Math.Max(1, folderMaxBytes);
        }

        public string Folder => _folderFull;

        /// <summary>The host's switch: when false every automatic report is refused (manual uploads are unaffected).</summary>
        public volatile bool AutoUploadsEnabled = true;

        public int ActiveUploadCount
        {
            get { lock (_sync) return _sessions.Count; }
        }

        /// <summary>Bytes currently spooled to temp files (RAM use is one 64 KB file buffer per active upload).</summary>
        public long ActiveSpooledBytes
        {
            get { lock (_sync) return _sessions.Values.Sum(s => s.Received); }
        }

        public bool HasActiveUpload(string playerName)
        {
            lock (_sync) return _sessions.ContainsKey(KeyOf(playerName));
        }

        /// <summary>File name part derived from a player name: <c>[A-Za-z0-9_-]{1,15}</c>, "player" when nothing is left.</summary>
        public static string SanitizePlayerName(string playerName)
        {
            var sb = new StringBuilder(MaxSafeNameLength);
            foreach (var c in playerName ?? string.Empty)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                {
                    sb.Append(c);
                    if (sb.Length == MaxSafeNameLength) break;
                }
            }

            return sb.Length == 0 ? "player" : sb.ToString();
        }

        public LogUploadDecision Begin(object owner, string playerName, bool authenticated, int uploadId, LogUploadFileKind fileKind,
            int totalBytes, int chunkSize, int chunkCount, LogUploadReason reason = LogUploadReason.Manual)
        {
            var safeName = SanitizePlayerName(playerName);
            var decision = new LogUploadDecision
            {
                Owner = owner, PlayerName = safeName, UploadId = uploadId, DeclaredBytes = totalBytes, UploadReason = reason
            };

            if (!authenticated || owner == null)
                return Refuse(decision, LogUploadRefusalReason.NotAllowed, "Only players who have joined the server can send logs.");
            if (fileKind != LogUploadFileKind.ClientLogsZip)
                return Refuse(decision, LogUploadRefusalReason.BadFile, "Unknown upload type.");
            if (!reason.IsKnown())
                return Refuse(decision, LogUploadRefusalReason.BadFile, "Unknown report reason.");
            if (reason.IsAutomatic() && !AutoUploadsEnabled)
                return Refuse(decision, LogUploadRefusalReason.NotAllowed, "The host turned off automatic reports.");
            if (totalBytes > LogUploadLimits.MaxTotalBytes)
                return Refuse(decision, LogUploadRefusalReason.TooLarge,
                    $"The logs are {totalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes; the limit is {LogBundleBuilder.FormatBytes(LogUploadLimits.MaxTotalBytes)}.");
            if (totalBytes < LogUploadLimits.MinTotalBytes)
                return Refuse(decision, LogUploadRefusalReason.BadFile, "The upload is too small to be a zip file.");
            if (chunkSize < LogUploadLimits.MinChunkBytes || chunkSize > LogUploadLimits.MaxChunkBytes)
                return Refuse(decision, LogUploadRefusalReason.BadFile, "Bad chunk size.");
            var expectedChunks = (int)(((long)totalBytes + chunkSize - 1) / chunkSize);
            if (chunkCount != expectedChunks || chunkCount > LogUploadLimits.MaxChunkCount)
                return Refuse(decision, LogUploadRefusalReason.BadFile, "The chunk count does not match the size.");

            lock (_sync)
            {
                var now = _utcNow();
                var key = KeyOf(playerName);

                if (_sessions.ContainsKey(key))
                    return Refuse(decision, LogUploadRefusalReason.Busy, "You already have an upload running.");

                List<DateTime> history;
                if (reason.IsAutomatic())
                {
                    history = GetAutoHistory(key, now);
                    var lastHour = history.Where(t => now - t < RateWindow).ToList();
                    if (lastHour.Count >= LogUploadLimits.MaxAutoUploadsPerPlayerPerHour)
                    {
                        var wait = lastHour[0] + RateWindow - now;
                        return Refuse(decision, LogUploadRefusalReason.TooMany,
                            $"Automatic reports are limited to {LogUploadLimits.MaxAutoUploadsPerPlayerPerHour} per hour. Next one in {FormatWait(wait)}.");
                    }

                    if (history.Count >= LogUploadLimits.MaxAutoUploadsPerPlayerPerDay)
                    {
                        var wait = history[0] + AutoDayWindow - now;
                        return Refuse(decision, LogUploadRefusalReason.TooMany,
                            $"Automatic reports are limited to {LogUploadLimits.MaxAutoUploadsPerPlayerPerDay} per day. Next one in {FormatWait(wait)}.");
                    }
                }
                else
                {
                    history = GetPlayerHistory(key, now);
                    if (history.Count >= LogUploadLimits.MaxUploadsPerPlayerPerHour)
                    {
                        var wait = history[0] + RateWindow - now;
                        return Refuse(decision, LogUploadRefusalReason.TooMany,
                            $"You can send logs {LogUploadLimits.MaxUploadsPerPlayerPerHour} times per hour. Try again in {FormatWait(wait)}.");
                    }
                }

                // Measured from the later of the previous upload's start and its end, so a retry right after a long
                // upload also waits.
                var last = history.Count > 0 ? history[history.Count - 1] : DateTime.MinValue;
                if (_lastEnded.TryGetValue(key, out var ended) && ended > last) last = ended;
                if (last != DateTime.MinValue && now - last < LogUploadLimits.MinTimeBetweenUploads)
                {
                    var wait = last + LogUploadLimits.MinTimeBetweenUploads - now;
                    return Refuse(decision, LogUploadRefusalReason.TooFrequent, $"Please wait {FormatWait(wait)} before sending again.");
                }

                PruneHistory(_globalHistory, now);
                if (_globalHistory.Count >= LogUploadLimits.MaxUploadsPerHourServerWide)
                    return Refuse(decision, LogUploadRefusalReason.TooMany, "The server has received too many log uploads this hour. Try again later.");

                if (_sessions.Count >= LogUploadLimits.MaxConcurrentUploadsServerWide)
                    return Refuse(decision, LogUploadRefusalReason.Busy, "Another player is sending logs right now. Try again in a minute.");

                long freeBytes;
                try
                {
                    freeBytes = _freeDiskBytes(_folderFull);
                }
                catch (Exception)
                {
                    freeBytes = long.MaxValue;
                }

                if (freeBytes - totalBytes < LogUploadLimits.MinFreeDiskBytes)
                    return Refuse(decision, LogUploadRefusalReason.Busy, "The server's disk is nearly full.");

                string tempPath;
                FileStream stream;
                try
                {
                    EnsureFolder();
                    tempPath = Path.Combine(_folderFull, TempFilePrefix + Guid.NewGuid().ToString("N") + TempFileExtension);
                    stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    return Refuse(decision, LogUploadRefusalReason.ServerError, "The server could not create the file.");
                }

                history.Add(now);
                _globalHistory.Add(now);
                _sessions[key] = new Session
                {
                    Owner = owner,
                    Key = key,
                    SafeName = safeName,
                    UploadId = uploadId,
                    TotalBytes = totalBytes,
                    ChunkSize = chunkSize,
                    ChunkCount = chunkCount,
                    Reason = reason,
                    Stream = stream,
                    TempPath = tempPath,
                    StartedUtc = now,
                    LastActivityUtc = now,
                };

                decision.Outcome = LogUploadOutcome.Accepted;
                return decision;
            }
        }

        public LogUploadDecision AddChunk(object owner, string playerName, int uploadId, int chunkIndex, int chunkCount, int totalBytes,
            byte[] data, int count)
        {
            lock (_sync)
            {
                var now = _utcNow();
                if (!_sessions.TryGetValue(KeyOf(playerName), out var session) || !ReferenceEquals(session.Owner, owner) || owner == null)
                {
                    return new LogUploadDecision
                    {
                        Outcome = LogUploadOutcome.Ignored,
                        Owner = owner,
                        PlayerName = SanitizePlayerName(playerName),
                        UploadId = uploadId,
                        Detail = "No upload is running for this player.",
                    };
                }

                if (uploadId != session.UploadId)
                    return Abort(session, LogUploadRefusalReason.BadFile, "A chunk for a different upload arrived.");
                if (now - session.StartedUtc > LogUploadLimits.MaxUploadDuration)
                    return Abort(session, LogUploadRefusalReason.TimedOut, "The upload took too long.");
                if (chunkCount != session.ChunkCount || totalBytes != session.TotalBytes)
                    return Abort(session, LogUploadRefusalReason.BadFile, "A chunk header does not match the upload.");
                if (chunkIndex != session.NextChunk)
                    return Abort(session, LogUploadRefusalReason.BadFile,
                        $"Chunk {chunkIndex} arrived but chunk {session.NextChunk} was expected (missing, duplicate or out of order).");

                var expectedLength = chunkIndex < session.ChunkCount - 1
                    ? session.ChunkSize
                    : session.TotalBytes - (long)session.ChunkSize * (session.ChunkCount - 1);
                if (data == null || count < 0 || count > data.Length || count != expectedLength)
                    return Abort(session, LogUploadRefusalReason.BadFile, $"Chunk {chunkIndex} has the wrong size.");

                if (chunkIndex == 0 && !StartsWithZipMagic(data, count))
                    return Abort(session, LogUploadRefusalReason.BadFile, "The upload is not a zip file.");

                try
                {
                    session.Stream.Write(data, 0, count);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ObjectDisposedException)
                {
                    return Abort(session, LogUploadRefusalReason.ServerError, "The server could not write the file.");
                }

                session.NextChunk++;
                session.Received += count;
                session.LastActivityUtc = now;

                if (session.NextChunk < session.ChunkCount)
                {
                    return new LogUploadDecision
                    {
                        Outcome = LogUploadOutcome.ChunkStored,
                        Owner = owner,
                        PlayerName = session.SafeName,
                        UploadId = uploadId,
                        DeclaredBytes = session.TotalBytes,
                    };
                }

                return Complete(session, now);
            }
        }

        /// <summary>The client gave up; the partial file is deleted. Returns Ignored when nothing matched.</summary>
        public LogUploadDecision Cancel(object owner, string playerName, int uploadId)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(KeyOf(playerName), out var session) && owner != null && ReferenceEquals(session.Owner, owner) &&
                    session.UploadId == uploadId)
                {
                    return Abort(session, LogUploadRefusalReason.Cancelled, "Cancelled by the player.");
                }

                return new LogUploadDecision { Outcome = LogUploadOutcome.Ignored, Owner = owner, PlayerName = SanitizePlayerName(playerName), UploadId = uploadId };
            }
        }

        /// <summary>The connection closed: drop its upload, if any. Returns null when it had none.</summary>
        public LogUploadDecision AbortForDisconnect(object owner, string playerName)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(KeyOf(playerName), out var session) && owner != null && ReferenceEquals(session.Owner, owner))
                    return Abort(session, LogUploadRefusalReason.Cancelled, "The player disconnected.");
                return null;
            }
        }

        /// <summary>Drops uploads that went quiet or ran too long, freeing their temp files. Call periodically.</summary>
        public List<LogUploadDecision> SweepExpired()
        {
            var result = new List<LogUploadDecision>();
            lock (_sync)
            {
                var now = _utcNow();
                foreach (var session in _sessions.Values.ToArray())
                {
                    if (now - session.LastActivityUtc > LogUploadLimits.InactivityTimeout)
                        result.Add(Abort(session, LogUploadRefusalReason.TimedOut,
                            $"No data arrived for {(int)LogUploadLimits.InactivityTimeout.TotalSeconds} seconds."));
                    else if (now - session.StartedUtc > LogUploadLimits.MaxUploadDuration)
                        result.Add(Abort(session, LogUploadRefusalReason.TimedOut, "The upload took too long."));
                }

                foreach (var key in _playerHistory.Keys.ToArray())
                {
                    PruneHistory(_playerHistory[key], now);
                    if (_playerHistory[key].Count == 0) _playerHistory.Remove(key);
                }

                foreach (var key in _autoHistory.Keys.ToArray())
                {
                    _autoHistory[key].RemoveAll(t => now - t >= AutoDayWindow);
                    if (_autoHistory[key].Count == 0) _autoHistory.Remove(key);
                }

                foreach (var key in _lastEnded.Where(kv => now - kv.Value >= RateWindow).Select(kv => kv.Key).ToArray())
                {
                    _lastEnded.Remove(key);
                }

                PruneHistory(_globalHistory, now);
            }

            return result;
        }

        /// <summary>
        /// Keeps the newest <see cref="LogUploadLimits.FolderMaxFiles"/> stored zips totalling at most
        /// <see cref="LogUploadLimits.FolderMaxBytes"/> (unless overridden in the constructor); deletes older ones. Only
        /// top-level *.zip files are touched.
        /// </summary>
        public List<string> EnforceQuota()
        {
            var deleted = new List<string>();
            lock (_sync)
            {
                if (!Directory.Exists(_folderFull)) return deleted;

                var files = new DirectoryInfo(_folderFull).GetFiles("*.zip", SearchOption.TopDirectoryOnly)
                    .Where(f => string.Equals(f.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .ToList();

                long keptBytes = 0;
                var kept = 0;
                var full = false;
                foreach (var file in files)
                {
                    // Newest first; the newest file is always kept. Once one does not fit, it and everything older go.
                    full = full || (kept > 0 && (kept >= _folderMaxFiles || keptBytes + file.Length > _folderMaxBytes));
                    if (!full)
                    {
                        kept++;
                        keptBytes += file.Length;
                        continue;
                    }

                    try
                    {
                        file.Delete();
                        deleted.Add(file.Name);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        // Leave it; the next upload tries again.
                    }
                }
            }

            return deleted;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                foreach (var session in _sessions.Values.ToArray())
                {
                    Abort(session, LogUploadRefusalReason.Cancelled, "Server shutting down.");
                }
            }
        }

        private LogUploadDecision Complete(Session session, DateTime now)
        {
            var decision = new LogUploadDecision
            {
                Owner = session.Owner,
                PlayerName = session.SafeName,
                UploadId = session.UploadId,
                DeclaredBytes = session.TotalBytes,
                UploadReason = session.Reason,
            };

            try
            {
                session.Stream.Flush();
                var length = session.Stream.Length;
                session.Stream.Dispose();
                session.Stream = null;
                if (length != session.TotalBytes)
                    return Abort(session, LogUploadRefusalReason.BadFile, "The stored size does not match the declared size.");

                var finalPath = ChooseFinalPath(session.SafeName, now, session.Reason);
                File.Move(session.TempPath, finalPath);
                _sessions.Remove(session.Key);
                _lastEnded[session.Key] = now;

                decision.Outcome = LogUploadOutcome.Completed;
                decision.StoredPath = finalPath;
                decision.StoredName = Path.GetFileName(finalPath);
                decision.StoredBytes = length;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return Abort(session, LogUploadRefusalReason.ServerError, "The server could not store the file.");
            }

            decision.DeletedForQuota = EnforceQuota();
            AppendIndex(decision, now);
            return decision;
        }

        /// <summary>
        /// One JSON line per stored upload (file, player, UTC time, size, reason) so the host can see what arrived and why
        /// without opening any zip. The zip content itself is still never read. A failure here never loses the upload.
        /// </summary>
        private void AppendIndex(LogUploadDecision decision, DateTime now)
        {
            try
            {
                var path = Path.Combine(_folderFull, IndexFileName);
                var reason = decision.UploadReason.ToString().ToLowerInvariant();
                var line = "{\"file\":\"" + decision.StoredName + "\",\"player\":\"" + decision.PlayerName + "\",\"utc\":\"" +
                           now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\",\"bytes\":" +
                           decision.StoredBytes.ToString(CultureInfo.InvariantCulture) + ",\"reason\":\"" + reason + "\"}";
                File.AppendAllText(path, line + "\n", new UTF8Encoding(false));

                var info = new FileInfo(path);
                if (info.Length > IndexMaxBytes)
                {
                    var lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(Math.Max(0, lines.Length - IndexKeepLines)).ToArray(), new UTF8Encoding(false));
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // The index is a convenience.
            }
        }

        private string ChooseFinalPath(string safeName, DateTime now, LogUploadReason reason)
        {
            var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var suffix = reason.FileSuffix();
            for (var i = 1; i < 100; i++)
            {
                var name = i == 1 ? $"{safeName}_{stamp}Z{suffix}.zip" : $"{safeName}_{stamp}Z{suffix}-{i}.zip";
                var path = Path.GetFullPath(Path.Combine(_folderFull, name));
                // Defence in depth: the name is built from [A-Za-z0-9_-] only, but never write outside the folder.
                if (!string.Equals(Path.GetDirectoryName(path), _folderFull, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Refusing to write outside the client logs folder");
                if (!File.Exists(path)) return path;
            }

            throw new IOException("Could not find a free file name");
        }

        private LogUploadDecision Abort(Session session, LogUploadRefusalReason reason, string detail)
        {
            try
            {
                session.Stream?.Dispose();
            }
            catch (Exception)
            {
                // Closing a broken stream must not stop the cleanup.
            }

            session.Stream = null;
            try
            {
                if (File.Exists(session.TempPath)) File.Delete(session.TempPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // A leftover temp file is removed the next time the folder is prepared.
            }

            _sessions.Remove(session.Key);
            _lastEnded[session.Key] = _utcNow();

            return new LogUploadDecision
            {
                Outcome = LogUploadOutcome.Refused,
                Owner = session.Owner,
                PlayerName = session.SafeName,
                UploadId = session.UploadId,
                DeclaredBytes = session.TotalBytes,
                Reason = reason,
                Detail = detail,
            };
        }

        private static LogUploadDecision Refuse(LogUploadDecision decision, LogUploadRefusalReason reason, string detail)
        {
            decision.Outcome = LogUploadOutcome.Refused;
            decision.Reason = reason;
            decision.Detail = detail;
            return decision;
        }

        private void EnsureFolder()
        {
            Directory.CreateDirectory(_folderFull);
            if (_folderReady) return;
            _folderReady = true;

            // Temp files left by a crash or a restart mid-upload. No session exists yet at this point.
            foreach (var stale in Directory.GetFiles(_folderFull, TempFilePrefix + "*" + TempFileExtension, SearchOption.TopDirectoryOnly))
            {
                if (_sessions.Values.Any(s => string.Equals(s.TempPath, stale, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    File.Delete(stale);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Try again next start.
                }
            }
        }

        private List<DateTime> GetPlayerHistory(string key, DateTime now)
        {
            if (!_playerHistory.TryGetValue(key, out var history))
            {
                history = new List<DateTime>();
                _playerHistory[key] = history;
            }

            PruneHistory(history, now);
            return history;
        }

        private List<DateTime> GetAutoHistory(string key, DateTime now)
        {
            if (!_autoHistory.TryGetValue(key, out var history))
            {
                history = new List<DateTime>();
                _autoHistory[key] = history;
            }

            history.RemoveAll(t => now - t >= AutoDayWindow);
            return history;
        }

        private static void PruneHistory(List<DateTime> history, DateTime now)
        {
            history.RemoveAll(t => now - t >= RateWindow);
        }

        private static bool StartsWithZipMagic(byte[] data, int count)
        {
            if (data == null || count < ZipMagic.Length) return false;
            for (var i = 0; i < ZipMagic.Length; i++)
            {
                if (data[i] != ZipMagic[i]) return false;
            }

            return true;
        }

        private static string KeyOf(string playerName) => (playerName ?? string.Empty).ToLowerInvariant();

        private static string FormatWait(TimeSpan wait)
        {
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            return wait.TotalMinutes >= 1 ? $"{Math.Ceiling(wait.TotalMinutes):0} min" : $"{Math.Ceiling(wait.TotalSeconds):0} s";
        }

        private static long DefaultFreeDiskBytes(string folder)
        {
            var root = Path.GetPathRoot(folder);
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
    }
}
