using System.Globalization;
using System.Text.RegularExpressions;

namespace VmpControl.Core;

/// <summary>Number and duration formatting. Always invariant culture so the numbers look the same on every PC.</summary>
public static class Fmt
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(long? value)
    {
        if (value == null) return "-";
        double v = value.Value;
        var neg = v < 0;
        v = Math.Abs(v);
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }

        var text = unit == 0 ? ((long)v).ToString(Inv) : v.ToString("0.0", Inv);
        return (neg ? "-" : "") + text + " " + Units[unit];
    }

    public static string Rate(double bytesPerSecond) => Bytes((long)Math.Round(bytesPerSecond)) + "/s";

    public static string Count(long value) => value.ToString("N0", Inv);

    public static string Percent(double value) => value.ToString("0.0", Inv) + "%";

    public static string Ratio(double value) => value <= 0 ? "-" : value.ToString("0.0#", Inv) + "x";

    /// <summary>45s, 12m 05s, 3h 12m, 2d 4h.</summary>
    public static string Duration(double? seconds)
    {
        if (seconds == null || double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value)) return "-";
        var total = (long)Math.Max(0, Math.Floor(seconds.Value));
        var days = total / 86400;
        var hours = total % 86400 / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        if (days > 0) return $"{days}d {hours}h";
        if (hours > 0) return $"{hours}h {minutes}m";
        if (minutes > 0) return $"{minutes}m {secs:00}s";
        return $"{secs}s";
    }

    public static string LocalTime(DateTime? utc)
    {
        if (utc == null) return "-";
        var value = utc.Value;
        if (value.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv);
    }

    public static string LocalTimeText(string isoUtc)
    {
        return DateTime.TryParse(isoUtc, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? LocalTime(parsed) : (isoUtc ?? "-");
    }
}

/// <summary>Reconnect delays for the SSH tunnel: 1, 2, 5, 10, 30, then 60 seconds for every later try.</summary>
public static class Backoff
{
    private static readonly int[] Seconds = { 1, 2, 5, 10, 30, 60 };

    /// <summary>The wait before try number <paramref name="attempt"/> (1 = the first retry).</summary>
    public static TimeSpan Delay(int attempt)
    {
        var index = Math.Clamp(attempt, 1, Seconds.Length) - 1;
        return TimeSpan.FromSeconds(Seconds[index]);
    }
}

/// <summary>
/// Builds the argument lists for ssh and the control scripts. Every list is an array handed to ProcessStartInfo.ArgumentList: no shell, and
/// no string is ever glued into a command line. The remote text is built only from validated values.
/// </summary>
public static class SshCommands
{
    public static readonly string[] ControlActions = { "Status", "Start", "Stop", "Restart", "Backup" };
    private static readonly Regex AliasRx = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex RootRx = new(@"^[A-Za-z]:\\[^""&|<>^%`$\r\n\0]*$", RegexOptions.CultureInvariant);

    /// <summary>Letters, digits, '_', '.', '-' only, and never a leading '-' (ssh would read it as an option).</summary>
    public static bool IsValidAlias(string alias)
    {
        return !string.IsNullOrEmpty(alias) && alias.Length <= 64 && alias[0] != '-' && AliasRx.IsMatch(alias);
    }

    /// <summary>A drive-absolute Windows path without any character the remote cmd/PowerShell would treat specially.</summary>
    public static bool IsSafeRemoteRoot(string root)
    {
        return !string.IsNullOrWhiteSpace(root) && root.Length <= 240 && RootRx.IsMatch(root);
    }

    public static string TokenPath(string root) => root.TrimEnd('\\') + @"\Config\admin-token.txt";

    private static string[] Common(string alias)
    {
        if (!IsValidAlias(alias)) throw new ArgumentException("The ssh alias may contain only letters, digits, '_', '.' and '-'.", nameof(alias));
        return new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10" };
    }

    public static string[] TunnelArgs(string alias, int localPort, int remotePort)
    {
        Common(alias);
        CheckPort(localPort);
        CheckPort(remotePort);
        return new[]
        {
            "-N", "-T",
            "-L", $"127.0.0.1:{localPort.ToString(CultureInfo.InvariantCulture)}:127.0.0.1:{remotePort.ToString(CultureInfo.InvariantCulture)}",
            "-o", "BatchMode=yes",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "ConnectTimeout=10",
            alias,
        };
    }

    /// <summary>
    /// Reads the admin token over ssh as an encoded PowerShell command, so the path (which has spaces) never goes through the remote
    /// shell's quoting; this is how vmp-server-control.ps1 talks to the train PC too. The path sits in a single-quoted PowerShell literal.
    /// </summary>
    public static string[] TokenReadArgs(string alias, string remoteRoot)
    {
        var common = Common(alias);
        if (!IsSafeRemoteRoot(remoteRoot)) throw new ArgumentException("The remote server folder contains characters that are not allowed.", nameof(remoteRoot));
        var script = "$ProgressPreference='SilentlyContinue'; [Console]::Out.Write([IO.File]::ReadAllText('" + TokenPath(remoteRoot).Replace("'", "''") + "'))";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return common.Concat(new[] { "-n", "-T", alias, "powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }).ToArray();
    }

    public static string[] ShutdownRestartArgs(string alias)
    {
        return Common(alias).Concat(new[] { "-n", "-T", alias, "shutdown", "/r", "/t", "30", "/c", "\"VMP Control restart\"" }).ToArray();
    }

    public static string[] ShutdownAbortArgs(string alias)
    {
        return Common(alias).Concat(new[] { "-n", "-T", alias, "shutdown", "/a" }).ToArray();
    }

    /// <summary>powershell.exe arguments that run a control script with one of <see cref="ControlActions"/>.</summary>
    /// <param name="serverRoot">Host mode: the server folder of the profile, passed as <c>-ServerRoot</c> so the script works on that folder and not on its own default. Remote mode leaves it null.</param>
    public static string[] ScriptArgs(string scriptPath, string action, bool yes, string serverRoot = null)
    {
        if (string.IsNullOrWhiteSpace(scriptPath)) throw new ArgumentException("No control script path is set.", nameof(scriptPath));
        var match = ControlActions.FirstOrDefault(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));
        if (match == null) throw new ArgumentException("Unknown action: " + action, nameof(action));
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Action", match };
        if (yes) args.Add("-Yes");
        if (!string.IsNullOrWhiteSpace(serverRoot)) args.AddRange(new[] { "-ServerRoot", NormalizeRoot(serverRoot) });
        return args.ToArray();
    }

    /// <summary>The folder as the script should get it: no trailing separator, except for a drive root such as <c>C:\</c>.</summary>
    public static string NormalizeRoot(string folder)
    {
        var trimmed = folder.Trim();
        var cut = trimmed.TrimEnd('\\', '/');
        if (cut.Length == 0) return trimmed;
        return cut.Length == 2 && cut[1] == ':' ? cut + "\\" : cut;
    }

    private static void CheckPort(int port)
    {
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1-65535.");
    }
}

public sealed class ActivityEntry
{
    public DateTime Time { get; init; }
    public string Action { get; init; }
    public string Result { get; init; }
    public bool Failed { get; init; }
}

/// <summary>The in-app activity list. Everything that goes in passes through <see cref="Redact"/> first, so the admin token can never be shown or logged.</summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 500;
    private readonly List<ActivityEntry> _entries = new();
    private readonly object _lock = new();
    private string _secret;

    public event Action<ActivityEntry> Added;

    public void SetSecret(string secret) => _secret = string.IsNullOrEmpty(secret) ? null : secret;

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public ActivityEntry Add(string action, string result, bool failed = false)
    {
        var secret = _secret;
        var entry = new ActivityEntry { Time = DateTime.Now, Action = Redact(action, secret), Result = Redact(result, secret), Failed = failed };
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }

        Added?.Invoke(entry);
        return entry;
    }

    private static readonly Regex BearerRx = new(@"Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Redact(string text, string secret)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[token]", StringComparison.Ordinal);
        return BearerRx.Replace(text, "Bearer [token]");
    }
}

/// <summary>Command-line switches. Anything unrecognised is collected in <see cref="Problems"/> instead of being ignored silently.</summary>
public sealed class CommandLineOptions
{
    public bool Mock { get; private set; }
    public bool Minimized { get; private set; }
    public string Profile { get; private set; }
    public string ScreenshotFolder { get; private set; }
    /// <summary>Builds an in-memory Host profile for this folder (named by --profile, default "Command line"). Never saved.</summary>
    public string ServerFolder { get; private set; }
    public int? ApiPort { get; private set; }
    public string SettingsPath { get; private set; }
    /// <summary>Tab to show first (Overview, Network, Logs, Backups, Reports, Actions).</summary>
    public string Tab { get; private set; }
    /// <summary>With --mock and --screenshot: also render the Settings page with a few pending edits, a validation error and the restart banner.</summary>
    public bool MockEdits { get; private set; }
    public List<string> Problems { get; } = new();

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CommandLineOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Next(string name)
            {
                if (i + 1 < args.Count) return args[++i];
                o.Problems.Add(name + " needs a value");
                return null;
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--mock": o.Mock = true; break;
                case "--minimized": o.Minimized = true; break;
                case "--profile": o.Profile = Next("--profile"); break;
                case "--screenshot": o.ScreenshotFolder = Next("--screenshot"); break;
                case "--server-folder": o.ServerFolder = Next("--server-folder"); break;
                case "--settings": o.SettingsPath = Next("--settings"); break;
                case "--tab": o.Tab = Next("--tab"); break;
                case "--mock-edits": o.MockEdits = true; break;
                case "--api-port":
                    var text = Next("--api-port");
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535) o.ApiPort = port;
                    else o.Problems.Add("--api-port must be 1-65535");
                    break;
                default: o.Problems.Add("unknown argument " + args[i]); break;
            }
        }

        return o;
    }
}
