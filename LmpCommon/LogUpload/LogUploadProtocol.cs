using System;

namespace LmpCommon.LogUpload
{
    /// <summary>What a log upload contains. Only one kind exists today; the byte leaves room for more.</summary>
    public enum LogUploadFileKind : byte
    {
        ClientLogsZip = 1,
    }

    /// <summary>
    /// Why a bundle was sent. <see cref="Manual"/> is the player's own "Send logs to host" click; every other value is an
    /// automatic report the client started itself and is limited more tightly (see <see cref="LogUploadLimits"/>).
    /// </summary>
    public enum LogUploadReason : byte
    {
        Manual = 0,
        /// <summary>The game froze: a very long frame, or several long frames in a few minutes (outside scene loads).</summary>
        Stall = 1,
        /// <summary>A burst of errors or exceptions in the game log.</summary>
        Errors = 2,
        /// <summary>The previous game session ended without a clean exit (crash, kill, power loss).</summary>
        Crash = 3,
        /// <summary>The host asked for it (see LogUploadRequestMsgData). The server only accepts it while a request for that player is pending.</summary>
        Requested = 4,
        /// <summary>Forced by the developer switch to test the pipeline.</summary>
        Test = 5,
    }

    public static class LogUploadReasonExtensions
    {
        public static bool IsAutomatic(this LogUploadReason reason)
        {
            return reason != LogUploadReason.Manual;
        }

        /// <summary>Stored file name suffix: empty for a manual upload, otherwise "-stall" and so on.</summary>
        public static string FileSuffix(this LogUploadReason reason)
        {
            switch (reason)
            {
                case LogUploadReason.Stall: return "-stall";
                case LogUploadReason.Errors: return "-errors";
                case LogUploadReason.Crash: return "-crash";
                case LogUploadReason.Requested: return "-requested";
                case LogUploadReason.Test: return "-test";
                default: return string.Empty;
            }
        }

        public static bool IsKnown(this LogUploadReason reason)
        {
            return reason >= LogUploadReason.Manual && reason <= LogUploadReason.Test;
        }
    }

    /// <summary>Why the server refused or aborted a log upload. Sent to the client as one byte.</summary>
    public enum LogUploadRefusalReason : byte
    {
        None = 0,
        /// <summary>Declared size above <see cref="LogUploadLimits.MaxTotalBytes"/>.</summary>
        TooLarge = 1,
        /// <summary>Hourly upload cap reached (per player or server-wide).</summary>
        TooMany = 2,
        /// <summary>Started again too soon after the previous upload.</summary>
        TooFrequent = 3,
        /// <summary>Not a zip, wrong size, bad chunk header, or chunks out of order / duplicated / missing.</summary>
        BadFile = 4,
        /// <summary>This player already has an upload running, the server is busy with others, or its disk is full.</summary>
        Busy = 5,
        /// <summary>No chunk for <see cref="LogUploadLimits.InactivityTimeout"/>, or the upload ran past <see cref="LogUploadLimits.MaxUploadDuration"/>.</summary>
        TimedOut = 6,
        /// <summary>The sender is not an authenticated player.</summary>
        NotAllowed = 7,
        /// <summary>The client cancelled the upload.</summary>
        Cancelled = 8,
        /// <summary>The server could not write the file.</summary>
        ServerError = 9,
    }

    /// <summary>
    /// Limits shared by the client (so it never asks for something the server refuses) and the server (which
    /// enforces every one of them and treats the client as untrusted).
    /// </summary>
    public static class LogUploadLimits
    {
        /// <summary>Largest zip a client may upload.</summary>
        public const int MaxTotalBytes = 25 * 1024 * 1024;

        /// <summary>A zip with one empty entry is 22 + 30 + 46 bytes; anything smaller is not a zip.</summary>
        public const int MinTotalBytes = 22;

        public const int MinChunkBytes = 1024;
        public const int MaxChunkBytes = 64 * 1024;

        /// <summary>Largest chunk count the server accepts (25 MB in 1 KB chunks).</summary>
        public const int MaxChunkCount = (MaxTotalBytes + MinChunkBytes - 1) / MinChunkBytes;

        // Defaults of the host-adjustable limits (general.LogUploadsPerPlayerPerHour and the like in the server settings).
        // A friend must be able to send logs whenever they want, so manual sends are generous; automatic reports stay tight.
        public const int MaxUploadsPerPlayerPerHour = 30;
        public static readonly TimeSpan MinTimeBetweenUploads = TimeSpan.FromSeconds(10);
        public const int MaxUploadsPerHourServerWide = 300;
        public const int MaxConcurrentUploadsServerWide = 2;

        /// <summary>Automatic reports have their own per-player budget so they never use up the player's manual uploads.</summary>
        public const int MaxAutoUploadsPerPlayerPerHour = 2;
        public const int MaxAutoUploadsPerPlayerPerDay = 8;

        /// <summary>Host-requested reports have their own per-player budget; they never use up the manual or the automatic ones.</summary>
        public const int MaxRequestedUploadsPerPlayerPerHour = 10;

        /// <summary>A request the host issued stays valid for this long; the upload it asks for must begin within it. Consumed when the upload begins.</summary>
        public static readonly TimeSpan RequestValidity = TimeSpan.FromMinutes(10);

        /// <summary>The server issues at most one request per player in this time (the admin API answers 429 otherwise).</summary>
        public static readonly TimeSpan MinTimeBetweenRequests = TimeSpan.FromSeconds(60);

        /// <summary>The client honours at most one host request in this time, whatever the server sends.</summary>
        public static readonly TimeSpan ClientMinTimeBetweenRequests = TimeSpan.FromSeconds(60);

        /// <summary>Longest note the host can attach to a request.</summary>
        public const int MaxRequestNoteLength = 120;

        /// <summary>The client waits at least this long between two automatic reports (and remembers it across restarts).</summary>
        public static readonly TimeSpan ClientMinTimeBetweenAutoReports = TimeSpan.FromMinutes(20);

        /// <summary>
        /// An upload that receives no chunk for this long is dropped and its temp file deleted. Long on purpose: a game
        /// starved of memory can freeze for a minute or more at a time and still finish the upload afterwards.
        /// </summary>
        public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Hard ceiling on one upload's lifetime so a slow drip cannot hold a slot forever (see <see cref="MaxDurationFor"/>).</summary>
        public static readonly TimeSpan MaxUploadDuration = TimeSpan.FromMinutes(60);

        /// <summary>The server lets an upload last the inactivity timeout plus the time its size takes at this slow rate.</summary>
        public const int SlowestExpectedBytesPerSecond = 16 * 1024;

        /// <summary>
        /// How long an upload of <paramref name="totalBytes"/> may run in total: <see cref="InactivityTimeout"/> plus the time at
        /// <see cref="SlowestExpectedBytesPerSecond"/>, never more than <see cref="MaxUploadDuration"/>. A 5.7 MB bundle gets about 16
        /// minutes, a 25 MB one about 37.
        /// </summary>
        public static TimeSpan MaxDurationFor(long totalBytes)
        {
            var seconds = InactivityTimeout.TotalSeconds + Math.Max(0, totalBytes) / (double)SlowestExpectedBytesPerSecond;
            return seconds >= MaxUploadDuration.TotalSeconds ? MaxUploadDuration : TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// A new Begin from the connection that already has an upload running replaces it when the running one has been silent
        /// at least this long (the client gave up and its Cancel never arrived). A younger one still answers Busy.
        /// </summary>
        public static readonly TimeSpan StaleSessionIdle = TimeSpan.FromSeconds(30);

        /// <summary>
        /// A failed attempt (timed out, connection dropped, replaced) that had delivered at least one chunk is not counted against the
        /// player's hourly limit, so its retry counts once. At most this many such refunds per player per hour.
        /// </summary>
        public const int MaxRefundsPerPlayerPerHour = 3;

        /// <summary>Server keeps at most this many stored uploads ...</summary>
        public const int FolderMaxFiles = 300;

        /// <summary>... and at most this many bytes of them; the oldest are deleted first.</summary>
        public const long FolderMaxBytes = 2048L * 1024 * 1024;

        /// <summary>The server refuses an upload when the disk would have less than this left afterwards.</summary>
        public const long MinFreeDiskBytes = 512L * 1024 * 1024;

        /// <summary>Upper bound for the server-written detail / stored-name strings in replies.</summary>
        public const int MaxReplyTextLength = 200;

        /// <summary>Client-to-server ReliableOrdered channel used only by log uploads (no other client message uses 11).</summary>
        public const int ClientChannel = 11;

        /// <summary>
        /// Chunk size the VMP client sends. It is the largest the server accepts (<see cref="MaxChunkBytes"/>; every server build
        /// since log uploads exist accepts 1 KB to 64 KB and checks it in Begin, so this needs no protocol change) and keeps the
        /// number of messages, and so of moments a starved game has to get right, small: a 5.7 MB bundle is 91 chunks, not 365.
        /// </summary>
        public const int ClientChunkBytes = 64 * 1024;

        /// <summary>Upper bound on the client's upload rate.</summary>
        public const int ClientMaxBytesPerSecond = 256 * 1024;
    }
}
