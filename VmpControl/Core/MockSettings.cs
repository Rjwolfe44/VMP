using System.Text.Json;

namespace VmpControl.Core;

/// <summary>A sample settings schema for --mock: five sections, about twenty settings of every kind (bool, int with a range, string with a length limit, enum, secrets, restart-required, modified, adminApi).</summary>
public static class MockSettings
{
    private static SettingDto S(string key, string label, string description, string type, object value, object def,
        double? min = null, double? max = null, int? maxLength = null, string[] options = null, bool restart = true, bool secret = false, bool? isSet = null)
    {
        var dto = new SettingDto
        {
            Key = key,
            Label = label,
            Description = description,
            Type = type,
            Value = JsonSerializer.SerializeToElement(secret ? null : value),
            Default = JsonSerializer.SerializeToElement(secret ? null : def),
            Min = min,
            Max = max,
            MaxLength = maxLength,
            Options = options?.ToList() ?? new List<string>(),
            RestartRequired = restart,
            Secret = secret,
            IsSet = secret ? isSet : null,
        };
        dto.Modified = secret ? isSet == true : JsonSerializer.Serialize(value) != JsonSerializer.Serialize(def);
        return dto;
    }

    public static SettingsSchemaDto Create()
    {
        return new SettingsSchemaDto
        {
            Sections =
            {
                new SettingsSectionDto
                {
                    Id = "general", Title = "General",
                    Settings =
                    {
                        S("general.ServerName", "Server Name", "Name of the server. Max 30 char", "string", "Example VMP Server", "KSP Server", maxLength: 30, restart: false),
                        S("general.ServerMotd", "Server Motd", "Message of the day, shown to a player who joins", "string", "Welcome aboard!", "Welcome to the server", maxLength: 255, restart: false),
                        S("general.MaxPlayers", "Max Players", "Maximum number of players that can be connected at the same time", "int", 20, 20, min: 1, max: 1000),
                        S("general.GameMode", "Game Mode", "Game mode of the universe", "enum", "Career", "Sandbox", options: new[] { "Sandbox", "Science", "Career" }),
                        S("general.GameDifficulty", "Game Difficulty", "Difficulty preset. With Custom the gameplay section is used", "enum", "Normal", "Normal", options: new[] { "Easy", "Normal", "Moderate", "Hard", "Custom" }),
                        S("general.Cheats", "Cheats", "Allow the debug menu and the cheat tools", "bool", false, true, restart: false),
                        S("general.AllowSackKerbals", "Allow Sack Kerbals", "Allow players to remove kerbals from the roster", "bool", false, false, restart: false),
                        S("general.Password", "Password", "Password players need to join. Empty means no password", "string", null, null, maxLength: 30, restart: false, secret: true, isSet: true),
                        S("general.AdminPassword", "Admin Password", "Password for the admin commands in the game", "string", null, null, maxLength: 30, restart: false, secret: true, isSet: false),
                    },
                },
                new SettingsSectionDto
                {
                    Id = "connection", Title = "Connection",
                    Settings =
                    {
                        S("connection.Port", "Port", "UDP port the server listens on", "int", 8800, 8800, min: 1, max: 65535),
                        S("connection.Address", "Address", "Local address to listen on. 0.0.0.0 means all addresses", "string", "0.0.0.0", "0.0.0.0", maxLength: 45),
                        S("connection.HeartbeatMsInterval", "Heartbeat Ms Interval", "Milliseconds between the keep-alive messages", "int", 5000, 5000, min: 0),
                        S("connection.Upnp", "Upnp", "Ask the router to open the port with UPnP", "bool", false, false),
                    },
                },
                new SettingsSectionDto
                {
                    Id = "warp", Title = "Warp",
                    Settings =
                    {
                        S("warp.WarpMode", "Warp Mode", "How time warp works for the players", "enum", "Subspace", "Subspace", options: new[] { "None", "Subspace" }),
                        S("warp.MinWarpTimeSeconds", "Min Warp Time Seconds", "Smallest jump a player may make into the future, in seconds", "int", 0, 0, min: 0),
                        S("warp.WarpMaster", "Warp Master", "Name of the player who controls the warp. Max 30 char", "string", "", "", maxLength: 30, restart: false),
                        S("warp.ForceSubspaceSync", "Force Subspace Sync", "Move players that are close in time into the same subspace", "bool", true, true),
                    },
                },
                new SettingsSectionDto
                {
                    Id = "interval", Title = "Intervals",
                    Settings =
                    {
                        S("interval.VesselUpdateMsInterval", "Vessel Update Ms Interval", "Milliseconds between two position updates of a vessel", "int", 50, 50, min: 0),
                        S("interval.BackupIntervalMs", "Backup Interval Ms", "Milliseconds between two automatic backups. 0 switches them off", "int", 900000, 900000, min: 0),
                    },
                },
                new SettingsSectionDto
                {
                    Id = "adminApi", Title = "Admin API",
                    Settings =
                    {
                        S("adminApi.EnableAdminApi", "Enable Admin Api", "Start the local admin API", "bool", true, true),
                        S("adminApi.Port", "Port", "TCP port of the admin API on 127.0.0.1", "int", 18900, 18900, min: 1, max: 65535),
                    },
                },
            },
        };
    }

    /// <summary>A deep copy (through JSON), so the sample "server" and the editor never share objects.</summary>
    public static SettingsSchemaDto Clone(SettingsSchemaDto schema) =>
        JsonSerializer.Deserialize<SettingsSchemaDto>(JsonSerializer.Serialize(schema, ApiJson.Options), ApiJson.Options);

    /// <summary>What the sample server does with a PUT: checks everything, then applies all or nothing.</summary>
    public static ApiResult<SettingsSaveResultDto> Apply(SettingsSchemaDto schema, IReadOnlyDictionary<string, object> changes)
    {
        var model = new SettingsEditModel();
        model.Load(schema);
        var problems = new List<SettingProblemDto>();
        var checkedValues = new Dictionary<string, object>();
        foreach (var (key, value) in changes)
        {
            var s = model.Find(key);
            if (s == null)
            {
                problems.Add(new SettingProblemDto { Key = key, Message = "unknown setting" });
                continue;
            }

            if (SettingsEditModel.IsReadOnly(s))
                return ApiResult<SettingsSaveResultDto>.Fail(ApiOutcome.Failed, "the adminApi settings can not be changed through the API; edit vmp-server.json and restart the server", 403);
            var normal = SettingsEditModel.Normalize(s, value);
            var message = SettingsEditModel.Validate(s, normal);
            if (message != null) problems.Add(new SettingProblemDto { Key = key, Message = message });
            else checkedValues[key] = normal;
        }

        if (problems.Count > 0)
        {
            var text = "settings rejected, nothing was changed: " + string.Join("; ", problems.Select(p => $"{p.Key} ({p.Message})"));
            var body = JsonSerializer.Serialize(new { error = text, errors = problems }, ApiJson.Options);
            return ApiResult<SettingsSaveResultDto>.Fail(ApiOutcome.Failed, text, 400, body);
        }

        var result = new SettingsSaveResultDto { Saved = true };
        foreach (var (key, value) in checkedValues)
        {
            var s = model.Find(key);
            var target = schema.Sections.SelectMany(x => x.Settings).First(x => x.Key == key);
            if (target.Secret)
            {
                target.IsSet = value is string { Length: > 0 };
                target.Modified = target.IsSet == true;
            }
            else
            {
                target.Value = JsonSerializer.SerializeToElement(value);
                target.Modified = JsonSerializer.Serialize(value) != JsonSerializer.Serialize(s.DefaultValue());
            }

            result.Applied.Add(key);
            if (target.RestartRequired) result.RestartRequired.Add(key);
        }

        return new ApiResult<SettingsSaveResultDto> { Outcome = ApiOutcome.Ok, Value = result };
    }
}
