using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmpControl.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileMode
{
    Remote,
    Host,
}

public sealed class Profile
{
    /// <summary>No default: the first run opens the settings dialog, or a settings.json next to the exe supplies it.</summary>
    public const string DefaultControlScript = "";
    public const string DefaultRemoteRoot = @"C:\VMP Server";

    public string Name { get; set; } = "Train PC";
    public ProfileMode Mode { get; set; } = ProfileMode.Remote;

    // Remote mode (app on another PC, server reached over an SSH tunnel).
    public string SshAlias { get; set; } = "trainpc";
    public string RemoteServerRoot { get; set; } = DefaultRemoteRoot;
    public int RemoteApiPort { get; set; } = 18900;
    public int LocalTunnelPort { get; set; } = 18901;
    public string ControlScriptPath { get; set; } = DefaultControlScript;

    // Host mode (app on the server PC itself).
    public string ServerFolder { get; set; } = "";
    public int ApiPort { get; set; } = 18900;

    [JsonIgnore]
    public bool IsRemote => Mode == ProfileMode.Remote;

    /// <summary>The port the app talks to on 127.0.0.1: the tunnel's local end in remote mode, the server's own API port in host mode.</summary>
    [JsonIgnore]
    public int LocalApiPort => IsRemote ? LocalTunnelPort : ApiPort;

    public static Profile CreateRemoteDefault() => new();

    public static Profile CreateHostTemplate() => new()
    {
        Name = "This PC (host)",
        Mode = ProfileMode.Host,
        ServerFolder = DefaultRemoteRoot,
        ApiPort = 18900,
    };

    public Profile Clone() => (Profile)MemberwiseClone();

    /// <summary>Null when the profile is usable, otherwise a message for the settings dialog.</summary>
    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "The profile needs a name.";
        if (IsRemote)
        {
            if (!SshCommands.IsValidAlias(SshAlias)) return "The ssh alias may contain only letters, digits, '_', '.' and '-' (and must not start with '-').";
            if (!SshCommands.IsSafeRemoteRoot(RemoteServerRoot)) return @"The remote server folder must be a full Windows path such as C:\Server and must not contain quotes or & | < > ^ %.";
            if (!ValidPort(RemoteApiPort) || !ValidPort(LocalTunnelPort)) return "Ports must be between 1 and 65535.";
            if (string.IsNullOrWhiteSpace(ControlScriptPath)) return "Set the path of vmp-server-control.ps1.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(ServerFolder)) return "Set the server folder.";
            if (!ValidPort(ApiPort)) return "The API port must be between 1 and 65535.";
        }

        return null;
    }

    private static bool ValidPort(int port) => port is >= 1 and <= 65535;
}

public sealed class AppSettings
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private List<Profile> _profiles = new();

    public List<Profile> Profiles { get => _profiles; set => _profiles = value ?? new(); }
    public string ActiveProfile { get; set; } = "Train PC";
    public bool Notifications { get; set; } = true;
    /// <summary>The script the Reports tab runs with -Analyze (remote mode only; the button is hidden when the file is missing).</summary>
    public string FetchLogsScriptPath { get; set; } = "";

    /// <summary>A settings.json next to the exe wins (portable use: keep the exe and the file together); otherwise %LOCALAPPDATA%\VmpControl.</summary>
    public static string DefaultPath
    {
        get
        {
            var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            if (!string.IsNullOrEmpty(exeDirectory))
            {
                var portable = Path.Combine(exeDirectory, "settings.json");
                if (File.Exists(portable)) return portable;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VmpControl", "settings.json");
        }
    }

    public static AppSettings CreateDefault() => new()
    {
        Profiles = { Profile.CreateRemoteDefault(), Profile.CreateHostTemplate() },
        ActiveProfile = "Train PC",
    };

    /// <summary>The settings file, or the defaults when it is missing, empty or unreadable. A broken file never stops the app from starting.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                {
                    loaded.Profiles.RemoveAll(p => p == null);
                    if (loaded.Profiles.Count > 0) return loaded;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to the defaults.
        }

        return CreateDefault();
    }

    /// <summary>Writes through a temporary file so a crash cannot leave half a settings file. Holds no token or password: there is no field for one.</summary>
    public void Save(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public Profile Find(string name)
    {
        return Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The profile to run: the requested name, else the saved active one, else the first.</summary>
    public Profile Pick(string requested)
    {
        return (requested != null ? Find(requested) : null) ?? Find(ActiveProfile) ?? Profiles.FirstOrDefault();
    }
}
