using System.Text.Json;
using System.Text.RegularExpressions;

namespace VmpControl.Core;

public enum ApiAvailability
{
    /// <summary>No answer yet.</summary>
    Unknown,
    Ok,
    /// <summary>404, or no token file although the server runs: an old server without the admin API.</summary>
    NotSupported,
    /// <summary>401: the token was refused.</summary>
    Unauthorized,
    /// <summary>Connection refused, timed out, or the tunnel/ssh is down.</summary>
    Unreachable,
}

public enum TrayState
{
    /// <summary>Grey.</summary>
    Connecting,
    /// <summary>Red: server down or unreachable.</summary>
    Down,
    /// <summary>Blue: running, nobody online.</summary>
    Idle,
    /// <summary>Green: running with players.</summary>
    Active,
}

/// <summary>What the control script's Status said (used when the API cannot be reached). Running is null when the script itself failed.</summary>
public sealed class ScriptStatus
{
    public bool? Running { get; set; }
    public int? Players { get; set; }
    public List<string> Names { get; set; } = new();
    public string Text { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PollSnapshot
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public ApiAvailability Api { get; set; }
    public string ApiDetail { get; set; }
    public StatusDto Status { get; set; }
    public List<ClientDto> Clients { get; set; } = new();
    public ScriptStatus Script { get; set; }
    public string TunnelStatus { get; set; }
    public string ProfileName { get; set; }
}

public static class TrayStateMapper
{
    public static TrayState Map(PollSnapshot s)
    {
        if (s == null) return TrayState.Connecting;
        switch (s.Api)
        {
            case ApiAvailability.Ok:
                return (s.Status?.PlayerCount ?? 0) > 0 ? TrayState.Active : TrayState.Idle;
            case ApiAvailability.Unknown:
                return TrayState.Connecting;
            default:
                if (s.Script == null) return TrayState.Connecting;
                if (s.Script.Running != true) return TrayState.Down;
                return (s.Script.Players ?? 0) > 0 ? TrayState.Active : TrayState.Idle;
        }
    }

    public static int? PlayerCount(PollSnapshot s)
    {
        if (s == null) return null;
        if (s.Api == ApiAvailability.Ok) return s.Status?.PlayerCount;
        return s.Script?.Players;
    }

    /// <summary>"KSP Server - 2 online - up 3h 12m". NotifyIcon.Text holds at most 127 characters.</summary>
    public static string Tooltip(PollSnapshot s, string fallbackName)
    {
        var name = s?.Status?.Settings?.ServerName;
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(fallbackName) ? "VMP server" : fallbackName;
        string text;
        switch (Map(s))
        {
            case TrayState.Connecting:
                text = name + " - connecting...";
                break;
            case TrayState.Down:
                text = name + (s.Script?.Running == null ? " - unreachable" : " - server down");
                break;
            default:
                if (s.Api == ApiAvailability.Ok)
                    text = $"{name} - {s.Status.PlayerCount} online - up {Fmt.Duration(s.Status.UptimeSeconds)}";
                else
                    text = $"{name} - running{(s.Script?.Players is int n ? $", {n} online" : "")} (no admin API)";
                break;
        }

        return text.Length <= 127 ? text : text.Substring(0, 124) + "...";
    }
}

/// <summary>Works out which balloon notifications a new snapshot deserves. The first snapshot never notifies, and "down" needs two in a row.</summary>
public sealed class NotificationTracker
{
    private HashSet<string> _players;
    private int _downStreak;
    private bool _announcedDown;
    private bool _everUp;

    public List<string> Process(PollSnapshot snapshot)
    {
        var messages = new List<string>();
        var state = TrayStateMapper.Map(snapshot);
        if (state == TrayState.Connecting) return messages;

        if (state == TrayState.Down)
        {
            _downStreak++;
            _players = null;
            if (_downStreak == 2 && _everUp)
            {
                _announcedDown = true;
                messages.Add("Server went down");
            }

            return messages;
        }

        _downStreak = 0;
        _everUp = true;
        if (_announcedDown)
        {
            _announcedDown = false;
            messages.Add("Server is back");
        }

        if (snapshot.Api != ApiAvailability.Ok)
        {
            _players = null;
            return messages;
        }

        var current = new HashSet<string>(snapshot.Status.Players.Select(p => p.Name ?? "?"), StringComparer.Ordinal);
        if (_players != null)
        {
            messages.AddRange(current.Except(_players).OrderBy(n => n, StringComparer.Ordinal).Select(n => n + " joined"));
            messages.AddRange(_players.Except(current).OrderBy(n => n, StringComparer.Ordinal).Select(n => n + " left"));
        }

        _players = current;
        return messages;
    }
}

/// <summary>Reads the text the control scripts print for Status: the people-friendly lines of vmp-server-control.ps1, or the JSON of Manage-VmpServer.ps1.</summary>
public static class ControlOutputParser
{
    private static readonly Regex PlayersRx = new(@"Players online:\s*(\d+)(?:\s*\(([^)]*)\))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotRunningRx = new(@"\bNOT\s+running\b|""Running""\s*:\s*false", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RunningRx = new(@"\bis\s+running\b|\bRUNNING\b|""Running""\s*:\s*true", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ScriptStatus ParseStatus(string text)
    {
        var status = new ScriptStatus { Text = (text ?? string.Empty).Trim() };
        if (status.Text.Length == 0) return status;

        var json = TryJsonRunning(status.Text);
        if (json != null) status.Running = json;
        else if (NotRunningRx.IsMatch(status.Text)) status.Running = false;
        else if (RunningRx.IsMatch(status.Text)) status.Running = true;

        var players = PlayersRx.Match(status.Text);
        if (players.Success)
        {
            status.Players = int.Parse(players.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (players.Groups[2].Success)
                status.Names = players.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        else if (Regex.IsMatch(status.Text, @"Players online:\s*nobody", RegexOptions.IgnoreCase))
        {
            status.Players = 0;
        }

        return status;
    }

    private static bool? TryJsonRunning(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.True) return true;
                if (property.Value.ValueKind == JsonValueKind.False) return false;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
