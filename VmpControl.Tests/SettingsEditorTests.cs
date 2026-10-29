using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

internal static class SettingsFixtures
{
    // GET /api/v1/settings from a real server build, trimmed to seven settings of different kinds (13 sections and 130 settings in the full response).
    public const string Schema = """
    {"sections":[
     {"id":"general","title":"General","settings":[
      {"key":"general.ServerName","label":"Server Name","description":"Name of the server. Max 30 char","type":"string","value":"VMP Train Career","default":"KSP Server","min":null,"max":null,"maxLength":30,"options":null,"restartRequired":false,"secret":false,"modified":true},
      {"key":"general.Password","label":"Password","description":"Password for the server. Leave it empty if you want to make a public server. Max 30 chars","type":"string","value":null,"default":null,"min":null,"max":null,"maxLength":30,"options":null,"restartRequired":false,"secret":true,"isSet":false,"modified":false},
      {"key":"general.MaxPlayers","label":"Max Players","description":"Maximum amount of players that can join the server.","type":"int","value":6,"default":20,"min":0,"max":null,"maxLength":null,"options":null,"restartRequired":true,"secret":false,"modified":true},
      {"key":"general.Cheats","label":"Cheats","description":"Enable use of Cheats in-game.","type":"bool","value":false,"default":true,"min":null,"max":null,"maxLength":null,"options":null,"restartRequired":false,"secret":false,"modified":true},
      {"key":"general.GameDifficulty","label":"Game Difficulty","description":"Specify the gameplay difficulty of the server. Values: Easy, Normal, Moderate, Hard, Custom","type":"enum","value":"Normal","default":"Normal","min":null,"max":null,"maxLength":null,"options":["Easy","Normal","Moderate","Hard","Custom"],"restartRequired":true,"secret":false,"modified":false}]},
     {"id":"connection","title":"Connection","settings":[
      {"key":"connection.Port","label":"Port","description":"UDP game port. Direct Internet hosting needs a reachable route and firewall rule.","type":"int","value":8800,"default":8800,"min":1,"max":65535,"maxLength":null,"options":null,"restartRequired":true,"secret":false,"modified":false}]},
     {"id":"adminApi","title":"Admin Api","settings":[
      {"key":"adminApi.Port","label":"Port","description":"TCP port of the admin API on 127.0.0.1. It is never opened to the network, so no router or firewall change is needed.","type":"int","value":18900,"default":18900,"min":1,"max":65535,"maxLength":null,"options":null,"restartRequired":true,"secret":false,"modified":false}]}
    ]}
    """;

    // The 400 answer of PUT /api/v1/settings (from docs/admin-api.md): every problem at once.
    public const string Rejected = """
    {"error":"settings rejected, nothing was changed: connection.Port (must be at most 65535); general.Bogus (unknown setting)","errors":[{"key":"connection.Port","message":"must be at most 65535"},{"key":"general.Bogus","message":"unknown setting"}]}
    """;

    public static SettingsEditModel Model()
    {
        var model = new SettingsEditModel();
        model.Load(ApiJson.TryParse<SettingsSchemaDto>(Schema));
        return model;
    }
}

[TestClass]
public class SettingsSchemaTests
{
    [TestMethod]
    public void RealResponse_ParsesSectionsTypesLimitsOptionsAndFlags()
    {
        var schema = ApiJson.TryParse<SettingsSchemaDto>(SettingsFixtures.Schema);

        CollectionAssert.AreEqual(new[] { "general", "connection", "adminApi" }, schema.Sections.Select(s => s.Id).ToArray());
        Assert.AreEqual("Admin Api", schema.Sections[2].Title);
        var all = schema.Sections.SelectMany(s => s.Settings).ToDictionary(s => s.Key);

        var name = all["general.ServerName"];
        Assert.AreEqual("string", name.Type);
        Assert.AreEqual(30, name.MaxLength);
        Assert.AreEqual("VMP Train Career", name.ServerValue());
        Assert.AreEqual("KSP Server", name.DefaultValue());
        Assert.IsTrue(name.Modified);
        Assert.IsFalse(name.RestartRequired);
        Assert.AreEqual("general", name.SectionId);

        var players = all["general.MaxPlayers"];
        Assert.AreEqual(6L, players.ServerValue());
        Assert.AreEqual(0.0, players.Min);
        Assert.IsNull(players.Max);
        Assert.IsTrue(players.RestartRequired);

        Assert.AreEqual(false, all["general.Cheats"].ServerValue());
        Assert.AreEqual(true, all["general.Cheats"].DefaultValue());
        CollectionAssert.AreEqual(new[] { "Easy", "Normal", "Moderate", "Hard", "Custom" }, all["general.GameDifficulty"].Options);
        Assert.AreEqual(65535.0, all["connection.Port"].Max);

        var password = all["general.Password"];
        Assert.IsTrue(password.Secret);
        Assert.AreEqual(false, password.IsSet);
        Assert.AreEqual(JsonValueKind.Null, password.Value.ValueKind);
        Assert.IsNull(password.ServerValue());
    }

    [TestMethod]
    public void MissingNullAndUnknownFields_AreTolerated()
    {
        var schema = ApiJson.TryParse<SettingsSchemaDto>("{\"sections\":[{\"id\":\"x\",\"title\":\"X\",\"newField\":1,\"settings\":[{\"key\":\"x.A\",\"type\":\"int\",\"options\":null,\"somethingNew\":[1,2]},{\"key\":\"x.B\"}]},{\"id\":\"y\",\"settings\":null}]}");

        Assert.AreEqual(2, schema.Sections.Count);
        Assert.AreEqual(2, schema.Sections[0].Settings.Count);
        Assert.AreEqual(0, schema.Sections[0].Settings[0].Options.Count);
        Assert.IsNull(schema.Sections[0].Settings[1].ServerValue());
        Assert.AreEqual(0, schema.Sections[1].Settings.Count);
        Assert.AreEqual(0, ApiJson.TryParse<SettingsSchemaDto>("{}").Sections.Count);
        Assert.AreEqual(0, ApiJson.TryParse<SettingsSchemaDto>("{\"sections\":null}").Sections.Count);
        Assert.IsNull(ApiJson.TryParse<SettingsSchemaDto>("<html></html>"));
    }

    [TestMethod]
    public void SaveResult_AndRejection_Parse()
    {
        var ok = ApiJson.TryParse<SettingsSaveResultDto>("{\"applied\":[\"general.ServerName\",\"connection.Port\"],\"restartRequired\":[\"connection.Port\"],\"saved\":true}");
        CollectionAssert.AreEqual(new[] { "general.ServerName", "connection.Port" }, ok.Applied);
        CollectionAssert.AreEqual(new[] { "connection.Port" }, ok.RestartRequired);
        Assert.IsTrue(ok.Saved);

        var rejected = ApiJson.TryParse<SettingsRejectedDto>(SettingsFixtures.Rejected);
        Assert.AreEqual(2, rejected.Errors.Count);
        Assert.AreEqual("connection.Port", rejected.Errors[0].Key);
        Assert.AreEqual("unknown setting", rejected.Errors[1].Message);
        StringAssert.StartsWith(rejected.Error, "settings rejected");
    }
}

[TestClass]
public class SettingsEditModelTests
{
    [TestMethod]
    public void Edits_FormADirtySet_AndEditingBackToTheServerValueRemovesThem()
    {
        var m = SettingsFixtures.Model();
        var changes = 0;
        m.Changed += () => changes++;

        Assert.AreEqual(0, m.DirtyCount);
        Assert.IsTrue(m.Set("general.ServerName", "Orbital Taxi"));
        Assert.IsTrue(m.Set("general.Cheats", true));
        Assert.AreEqual(2, m.DirtyCount);
        Assert.IsTrue(m.IsDirty("general.ServerName"));
        Assert.AreEqual("Orbital Taxi", m.Effective(m.Find("general.ServerName")));
        CollectionAssert.AreEqual(new[] { "general.ServerName", "general.Cheats" }, m.DirtyKeys.ToList());

        m.Set("general.ServerName", "VMP Train Career"); // the server's own value again
        Assert.IsFalse(m.IsDirty("general.ServerName"));
        Assert.AreEqual(1, m.DirtyCount);
        Assert.IsTrue(changes >= 3);
        Assert.IsFalse(m.Set("general.NoSuchSetting", 1));
    }

    [TestMethod]
    public void Revert_DiscardAll_AndResetToDefault()
    {
        var m = SettingsFixtures.Model();
        m.Set("general.ServerName", "A");
        m.Set("general.MaxPlayers", 10);
        m.Set("general.Cheats", true);

        m.Revert("general.ServerName");
        Assert.AreEqual(2, m.DirtyCount);
        Assert.AreEqual("VMP Train Career", m.Effective(m.Find("general.ServerName")));

        m.DiscardAll();
        Assert.AreEqual(0, m.DirtyCount);
        Assert.AreEqual(6L, m.Effective(m.Find("general.MaxPlayers")));

        // Reset to default only sets the edit; nothing is saved by it.
        var name = m.Find("general.ServerName");
        Assert.IsTrue(m.IsModified(name));
        Assert.IsTrue(m.ResetToDefault("general.ServerName"));
        Assert.AreEqual("KSP Server", m.Effective(name));
        Assert.IsFalse(m.IsModified(name));
        Assert.IsTrue(m.IsDirty("general.ServerName"));
        Assert.IsTrue(m.ResetToDefault("general.MaxPlayers"));
        Assert.AreEqual(20L, m.Effective(m.Find("general.MaxPlayers")));
        Assert.IsFalse(m.ResetToDefault("general.Nothing"));
    }

    [TestMethod]
    public void PutBody_HoldsOnlyTheChangedKeys_WithTheirJsonTypes()
    {
        var m = SettingsFixtures.Model();
        Assert.AreEqual("{\"changes\":{}}", m.BuildPutBody());

        m.Set("general.Cheats", true);
        m.Set("general.MaxPlayers", 12);
        m.Set("general.ServerName", "Taxi");
        m.Set("general.GameDifficulty", "hard"); // case is accepted and normalised to the server's spelling
        m.Set("connection.Port", 8800);          // unchanged: no edit

        var body = m.BuildPutBody();
        using var doc = JsonDocument.Parse(body);
        var changes = doc.RootElement.GetProperty("changes");
        CollectionAssert.AreEqual(new[] { "general.ServerName", "general.MaxPlayers", "general.Cheats", "general.GameDifficulty" }, changes.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("Taxi", changes.GetProperty("general.ServerName").GetString());
        Assert.AreEqual(JsonValueKind.Number, changes.GetProperty("general.MaxPlayers").ValueKind);
        Assert.AreEqual("12", changes.GetProperty("general.MaxPlayers").GetRawText());
        Assert.AreEqual(JsonValueKind.True, changes.GetProperty("general.Cheats").ValueKind);
        Assert.AreEqual("Hard", changes.GetProperty("general.GameDifficulty").GetString());
        Assert.IsFalse(body.Contains("connection.Port"));
        Assert.IsFalse(body.Contains("adminApi"));
        Assert.AreEqual(4, m.BuildChanges().Count);
    }

    [TestMethod]
    public void ClientValidation_AppliesMinMaxMaxLengthTypeAndEnumRules_BeforeAnythingIsSent()
    {
        var m = SettingsFixtures.Model();
        string Check(string key, object value) => SettingsEditModel.Validate(m.Find(key), SettingsEditModel.Normalize(m.Find(key), value));

        Assert.IsNull(Check("connection.Port", 8801));
        Assert.AreEqual("must be at least 1", Check("connection.Port", 0));
        Assert.AreEqual("must be at most 65535", Check("connection.Port", 70000));
        Assert.AreEqual("must be at least 0", Check("general.MaxPlayers", -1));
        Assert.IsNull(Check("general.MaxPlayers", 100000));
        Assert.AreEqual("must be a whole number", Check("general.MaxPlayers", 1.5));
        Assert.AreEqual("must be a number", Check("general.MaxPlayers", "ten"));
        Assert.IsNull(Check("general.ServerName", new string('x', 30)));
        Assert.AreEqual("must be at most 30 characters", Check("general.ServerName", new string('x', 31)));
        Assert.AreEqual("must be true or false", Check("general.Cheats", "yes"));
        Assert.IsNull(Check("general.GameDifficulty", "CUSTOM"));
        Assert.AreEqual("must be one of: Easy, Normal, Moderate, Hard, Custom", Check("general.GameDifficulty", "Insane"));
        Assert.AreEqual("must be at most 30 characters", Check("general.Password", new string('p', 31)));

        m.Set("connection.Port", 70000);
        m.Set("general.ServerName", new string('x', 40));
        m.Set("general.Cheats", true);
        Assert.AreEqual(2, m.ValidateAll());
        Assert.AreEqual("must be at most 65535", m.ErrorFor("connection.Port"));
        Assert.IsNull(m.ErrorFor("general.Cheats"));
        Assert.AreEqual(1, m.ErrorCountIn("connection"));
        Assert.AreEqual(3, m.DirtyCount, "invalid edits are kept so that the user can fix them");

        m.Set("connection.Port", 8802); // editing a row clears its error
        Assert.IsNull(m.ErrorFor("connection.Port"));
        Assert.AreEqual(1, m.ValidateAll());
    }

    [TestMethod]
    public void ServerRejection_ShowsEveryProblem_AndKeepsTheEdits()
    {
        var m = SettingsFixtures.Model();
        m.Set("connection.Port", 8801);
        m.Set("general.Cheats", true);

        var count = m.ApplyServerErrors(SettingsFixtures.Rejected);

        Assert.AreEqual(2, count);
        Assert.AreEqual("must be at most 65535", m.ErrorFor("connection.Port"));
        Assert.AreEqual("unknown setting", m.ErrorFor("general.Bogus"));
        Assert.AreEqual(2, m.DirtyCount);
        Assert.AreEqual(1, m.ErrorCountIn("general"), "an error for a key the schema does not know is still counted under its section id");
        Assert.AreEqual(0, m.ApplyServerErrors("not json"));
    }

    [TestMethod]
    public void Secrets_AreNeverRendered_AndOnlyAnExplicitClearSendsAnEmptyValue()
    {
        const string typed = "hunter2-very-secret";
        var m = SettingsFixtures.Model();
        var password = m.Find("general.Password");

        Assert.IsNull(m.Effective(password));
        Assert.AreEqual("not set", m.SecretState(password));
        Assert.AreEqual("not set", m.Describe(password));
        Assert.IsFalse(m.IsModified(password));

        Assert.IsFalse(m.Set("general.Password", ""), "an empty entry means no change, not clear");
        Assert.AreEqual(0, m.DirtyCount);

        Assert.IsTrue(m.Set("general.Password", typed));
        Assert.AreEqual("new value entered", m.SecretState(password));
        Assert.AreEqual("new value entered", m.Describe(password));
        Assert.IsFalse(m.Describe(password).Contains(typed));
        Assert.IsTrue(m.IsModified(password));
        StringAssert.Contains(m.BuildPutBody(), typed); // Change sends the new value ...
        Assert.AreEqual(typed, m.BuildChanges()["general.Password"]);

        Assert.IsTrue(m.ClearSecret("general.Password")); // ... and Clear is an explicit, separate action
        Assert.AreEqual("will be cleared", m.SecretState(password));
        Assert.IsFalse(m.BuildPutBody().Contains(typed));
        Assert.AreEqual("{\"changes\":{\"general.Password\":\"\"}}", m.BuildPutBody());
        Assert.IsFalse(m.IsModified(password));

        m.Revert("general.Password");
        Assert.AreEqual("not set", m.SecretState(password));
        Assert.IsFalse(m.ClearSecret("general.ServerName"), "Clear exists for secrets only");
        Assert.IsTrue(m.ResetToDefault("general.Password"), "for a secret, reset means clear");
        Assert.AreEqual("will be cleared", m.SecretState(password));
    }

    [TestMethod]
    public void ASecretThatTheServerShouldNeverSend_IsStillNotShown_AndNotLogged()
    {
        var schema = ApiJson.TryParse<SettingsSchemaDto>("{\"sections\":[{\"id\":\"general\",\"title\":\"General\",\"settings\":[{\"key\":\"general.AdminPassword\",\"label\":\"Admin Password\",\"type\":\"string\",\"value\":\"oops-leaked\",\"default\":\"oops-default\",\"secret\":true,\"isSet\":true,\"maxLength\":30}]}]}");
        var m = new SettingsEditModel();
        m.Load(schema);
        var s = m.Find("general.AdminPassword");

        Assert.IsNull(m.Effective(s));
        Assert.AreEqual("set", m.Describe(s));
        m.Set("general.AdminPassword", "typed-secret");

        var log = new ActivityLog();
        log.Add("Settings", SettingsEditModel.SaveSummary(new SettingsSaveResultDto { Applied = { "general.AdminPassword" }, Saved = true }));
        log.Add("Settings", m.Describe(s));
        foreach (var e in log.Snapshot())
        {
            Assert.IsFalse((e.Action + e.Result).Contains("typed-secret"));
            Assert.IsFalse((e.Action + e.Result).Contains("oops"));
        }

        StringAssert.Contains(log.Snapshot()[0].Result, "general.AdminPassword");
    }

    [TestMethod]
    public void AdminApiSettings_AreReadOnly()
    {
        var m = SettingsFixtures.Model();
        var port = m.Find("adminApi.Port");

        Assert.IsTrue(SettingsEditModel.IsReadOnly(port));
        Assert.IsFalse(SettingsEditModel.IsReadOnly(m.Find("connection.Port")));
        Assert.IsFalse(m.Set("adminApi.Port", 18901));
        Assert.IsFalse(m.ResetToDefault("adminApi.Port"));
        Assert.AreEqual(0, m.DirtyCount);
        Assert.AreEqual(18900L, m.Effective(port));
        Assert.IsFalse(m.BuildPutBody().Contains("adminApi"));
    }

    [TestMethod]
    public void Search_MatchesLabelKeyAndDescription_AllWordsRequired()
    {
        var m = SettingsFixtures.Model();
        string[] Keys(string search, bool modifiedOnly = false, string section = null) => m.Filter(search, modifiedOnly, section).Select(s => s.Key).ToArray();

        CollectionAssert.AreEquivalent(new[] { "connection.Port", "adminApi.Port" }, Keys("port"));
        CollectionAssert.AreEqual(new[] { "general.MaxPlayers" }, Keys("max players"));
        CollectionAssert.AreEqual(new[] { "general.Cheats" }, Keys("GENERAL.CHEATS"));
        CollectionAssert.AreEqual(new[] { "general.ServerName", "general.Password" }, Keys("30 char"), "words from the description");
        CollectionAssert.AreEqual(new[] { "general.Password" }, Keys("leave empty"));
        CollectionAssert.AreEqual(new[] { "adminApi.Port" }, Keys("admin port"), "label and key together");
        Assert.AreEqual(0, Keys("zzz").Length);
        Assert.AreEqual(7, Keys("").Length);
        Assert.AreEqual(1, Keys("", false, "connection").Length);
        Assert.AreEqual(0, Keys("cheats", false, "connection").Length);
    }

    [TestMethod]
    public void ModifiedOnly_KeepsSettingsThatDifferFromTheirDefault_AndRowsBeingEdited()
    {
        var m = SettingsFixtures.Model();
        string[] Keys(string search = "") => m.Filter(search, true, null).Select(s => s.Key).ToArray();

        CollectionAssert.AreEqual(new[] { "general.ServerName", "general.MaxPlayers", "general.Cheats" }, Keys());
        CollectionAssert.AreEqual(new[] { "general.Cheats" }, Keys("cheats"), "search and modified-only combine");

        m.Set("general.Cheats", true); // back to the default, but still a pending edit
        CollectionAssert.AreEqual(new[] { "general.ServerName", "general.MaxPlayers", "general.Cheats" }, Keys());
        Assert.IsFalse(m.IsModified(m.Find("general.Cheats")));

        m.Revert("general.Cheats");
        m.Set("general.Password", "x");
        CollectionAssert.AreEqual(new[] { "general.ServerName", "general.Password", "general.MaxPlayers", "general.Cheats" }, Keys());
    }

    [TestMethod]
    public void Reload_KeepsEditsForSettingsThatStillExist_AndDropsTheOnesTheServerNowHas()
    {
        var m = SettingsFixtures.Model();
        m.Set("general.ServerName", "Taxi");
        m.Set("general.Cheats", true);
        m.Set("general.MaxPlayers", 9);

        var newer = ApiJson.TryParse<SettingsSchemaDto>(SettingsFixtures.Schema.Replace("\"value\":\"VMP Train Career\"", "\"value\":\"Taxi\"").Replace("{\"key\":\"general.MaxPlayers\"", "{\"key\":\"general.MaxPlayersRenamed\""));
        m.Load(newer);

        Assert.IsFalse(m.IsDirty("general.ServerName"), "the server already has this value");
        Assert.IsTrue(m.IsDirty("general.Cheats"));
        Assert.IsFalse(m.IsDirty("general.MaxPlayers"), "the setting is gone");
        Assert.AreEqual(1, m.DirtyCount);
    }

    [TestMethod]
    public void SaveSummary_NamesKeysAndRestartCount_ButNoValues()
    {
        var text = SettingsEditModel.SaveSummary(new SettingsSaveResultDto { Applied = { "general.ServerName", "connection.Port" }, RestartRequired = { "connection.Port" }, Saved = true });

        Assert.AreEqual("saved 2 settings: general.ServerName, connection.Port (1 apply after a restart)", text);
        Assert.AreEqual("saved 1 setting: general.Cheats", SettingsEditModel.SaveSummary(new SettingsSaveResultDto { Applied = { "general.Cheats" } }));
    }
}

[TestClass]
public class RestartBannerTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 4, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void AppearsOnlyWhenASaveListsRestartRequiredKeys()
    {
        var b = new RestartBanner();
        Assert.IsFalse(b.Visible);

        b.OnSaved(Array.Empty<string>(), Start);
        Assert.IsFalse(b.Visible);

        b.OnSaved(new[] { "connection.Port" }, Start);
        Assert.IsTrue(b.Visible);
        Assert.AreEqual("1 change applies after a restart", b.Text);

        b.OnSaved(new[] { "general.MaxPlayers", "connection.Port" }, Start); // the same key twice counts once
        Assert.AreEqual("2 changes apply after a restart", b.Text);

        b.OnSaved(Array.Empty<string>(), Start); // a later save without restart keys leaves the banner alone
        Assert.AreEqual(2, b.Keys.Count);
    }

    [TestMethod]
    public void IsClearedWhenTheServerHasBeenRestarted_ButNotByJitter()
    {
        var b = new RestartBanner();
        b.OnSaved(new[] { "a.B" }, Start);

        b.OnStatus(Start.AddMilliseconds(40)); // the server derives its start time from "now - uptime"
        b.OnStatus(null);
        Assert.IsTrue(b.Visible);

        b.OnStatus(Start.AddMinutes(3)); // a different start time: the server restarted
        Assert.IsFalse(b.Visible);
        Assert.AreEqual("", b.Text);
    }

    [TestMethod]
    public void ASaveAfterARestart_StartsAFreshBanner()
    {
        var b = new RestartBanner();
        b.OnSaved(new[] { "a.B", "a.C" }, Start);

        b.OnSaved(new[] { "x.Y" }, Start.AddHours(2));

        CollectionAssert.AreEqual(new[] { "x.Y" }, b.Keys.ToList());
        b.Clear();
        Assert.IsFalse(b.Visible);
    }
}

[TestClass]
public class HostModeArgumentTests
{
    private static Profile Host(string folder) => new() { Name = "h", Mode = ProfileMode.Host, ServerFolder = folder, ApiPort = 18900 };

    [TestMethod]
    public void HostMode_PassesServerRootOfTheProfile_AndNoYes()
    {
        var args = LiveSession.ControlArgs(Host(@"C:\Users\Sam Doe\VMP Server\"), @"C:\Users\Sam Doe\VMP Server\Manage-VmpServer.ps1", "Restart");

        CollectionAssert.AreEqual(new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\Users\Sam Doe\VMP Server\Manage-VmpServer.ps1",
            "-Action", "Restart", "-ServerRoot", @"C:\Users\Sam Doe\VMP Server",
        }, args);
        Assert.IsFalse(args.Contains("-Yes"));
    }

    [TestMethod]
    public void HostMode_StatusAndEveryAction_GetTheServerRoot()
    {
        foreach (var action in SshCommands.ControlActions)
        {
            var args = LiveSession.ControlArgs(Host(@"D:\Games\VMP"), @"D:\Games\VMP\Manage-VmpServer.ps1", action);
            Assert.AreEqual(action, args[Array.IndexOf(args, "-Action") + 1]);
            Assert.AreEqual(@"D:\Games\VMP", args[Array.IndexOf(args, "-ServerRoot") + 1], action);
        }
    }

    [TestMethod]
    public void RemoteMode_KeepsItsOldArguments_YesAndNoServerRoot()
    {
        var remote = Profile.CreateRemoteDefault();
        remote.ControlScriptPath = @"C:\tools\vmp-server-control.ps1";

        var args = LiveSession.ControlArgs(remote, remote.ControlScriptPath, "Stop");

        CollectionAssert.AreEqual(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\tools\vmp-server-control.ps1", "-Action", "Stop", "-Yes" }, args);
    }

    [TestMethod]
    public void ServerRoot_IsNormalised_AndOnlyAddedWhenGiven()
    {
        Assert.AreEqual(@"C:\x y", SshCommands.NormalizeRoot(@"C:\x y\"));
        Assert.AreEqual(@"C:\x y", SshCommands.NormalizeRoot(@"  C:\x y\\ "));
        Assert.AreEqual(@"D:\", SshCommands.NormalizeRoot(@"D:\"));
        Assert.AreEqual(@"D:\", SshCommands.NormalizeRoot("D:"));
        Assert.IsFalse(SshCommands.ScriptArgs("a.ps1", "Status", false).Contains("-ServerRoot"));
        Assert.IsFalse(SshCommands.ScriptArgs("a.ps1", "Status", false, "  ").Contains("-ServerRoot"));
        var withRoot = SshCommands.ScriptArgs("a.ps1", "Backup", false, @"C:\srv");
        CollectionAssert.AreEqual(new[] { "-ServerRoot", @"C:\srv" }, withRoot.Skip(withRoot.Length - 2).ToArray());
    }

    [TestMethod]
    public async Task HostMode_TheScriptReallyReceivesServerRoot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vmpcontrol host root " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Manage-VmpServer.ps1"),
                "param([string]$Action, [string]$ServerRoot)\r\nSet-Content -LiteralPath (Join-Path $PSScriptRoot 'received.txt') -Value \"$Action|$ServerRoot\"\r\n");
            using var session = new LiveSession(Host(folder + "\\"));

            var result = await session.RunControlAsync("Stop", CancellationToken.None);

            Assert.IsTrue(result.Ok, result.Error + result.StartError);
            Assert.AreEqual("Stop|" + folder, File.ReadAllText(Path.Combine(folder, "received.txt")).Trim());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

[TestClass]
public class SettingsApiTests
{
    private const string Token = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface";
    private static TokenSource Tokens() => new(_ => Task.FromResult<(string, TokenProblem, string)>((Token, TokenProblem.None, null)));

    [TestMethod]
    public async Task Get_AndPut_GoToTheSettingsPath_WithTheRightVerbsAndBody()
    {
        var seen = new List<string>();
        using var server = new ApiClientTests.FakeServer();
        server.Handler = (method, path) =>
        {
            seen.Add(method + " " + path);
            return method == "GET"
                ? (200, SettingsFixtures.Schema, 0)
                : (200, "{\"applied\":[\"general.Cheats\"],\"restartRequired\":[],\"saved\":true}", 0);
        };
        using var client = new AdminApiClient(server.Port, Tokens());

        var get = await client.GetAsync<SettingsSchemaDto>("settings", CancellationToken.None);
        var m = new SettingsEditModel();
        m.Load(get.Value);
        m.Set("general.Cheats", true);
        var put = await client.PutAsync<SettingsSaveResultDto>("settings", new { changes = m.BuildChanges() }, CancellationToken.None);

        Assert.IsTrue(get.IsOk && put.IsOk);
        CollectionAssert.AreEqual(new[] { "GET /api/v1/settings", "PUT /api/v1/settings" }, seen);
        Assert.AreEqual("{\"changes\":{\"general.Cheats\":true}}", server.LastBody);
        Assert.AreEqual("Bearer " + Token, server.LastAuth);
        Assert.AreEqual(1, put.Value.Applied.Count);
        Assert.IsTrue(put.Value.Saved);
    }

    [TestMethod]
    public async Task ARejectedSave_KeepsTheBody_SoEveryProblemCanBeShown()
    {
        using var server = new ApiClientTests.FakeServer { Handler = (m, p) => (400, SettingsFixtures.Rejected, 0) };
        using var client = new AdminApiClient(server.Port, Tokens());

        var put = await client.PutAsync<SettingsSaveResultDto>("settings", new { changes = new Dictionary<string, object> { ["connection.Port"] = 70000 } }, CancellationToken.None);

        Assert.AreEqual(ApiOutcome.Failed, put.Outcome);
        Assert.AreEqual(400, put.StatusCode);
        StringAssert.StartsWith(put.Error, "settings rejected, nothing was changed");
        var m = SettingsFixtures.Model();
        Assert.AreEqual(2, m.ApplyServerErrors(put.Body));
    }

    [DataTestMethod]
    [DataRow(403, "the adminApi settings can not be changed through the API; edit the file and restart the server")]
    [DataRow(409, "the settings could not be saved because the settings file was not readable at start, so nothing was changed")]
    [DataRow(429, "too many failed authentications, try again later")]
    [DataRow(500, "the settings could not be saved, nothing was changed")]
    public async Task OtherFailures_ShowTheServersOwnMessage(int status, string message)
    {
        using var server = new ApiClientTests.FakeServer { Handler = (m, p) => (status, "{\"error\":\"" + message + "\"}", 0) };
        using var client = new AdminApiClient(server.Port, Tokens());

        var put = await client.PutAsync<SettingsSaveResultDto>("settings", new { changes = new { } }, CancellationToken.None);

        Assert.AreEqual(ApiOutcome.Failed, put.Outcome);
        Assert.AreEqual(status, put.StatusCode);
        Assert.AreEqual(message, put.Error);
    }

    [TestMethod]
    public async Task AnOldServerWithoutTheEndpoint_IsReportedAsNotSupported()
    {
        using var server = new ApiClientTests.FakeServer { Handler = (m, p) => (404, "{\"error\":\"not found\"}", 0) };
        using var client = new AdminApiClient(server.Port, Tokens());

        Assert.AreEqual(ApiOutcome.NotSupported, (await client.GetAsync<SettingsSchemaDto>("settings", CancellationToken.None)).Outcome);
    }

    [TestMethod]
    public async Task TheSampleServer_ValidatesLikeTheRealOne_AndAppliesAllOrNothing()
    {
        var session = new MockSession(null);
        var schema = (await session.GetSettingsAsync(CancellationToken.None)).Value;
        Assert.IsTrue(schema.Sections.Count >= 4 && schema.Sections.SelectMany(s => s.Settings).Count() >= 18);
        var kinds = schema.Sections.SelectMany(s => s.Settings).Select(s => s.TypeName).Distinct().ToList();
        CollectionAssert.IsSubsetOf(new[] { "bool", "int", "string", "enum" }, kinds);

        var bad = await session.PutSettingsAsync(new Dictionary<string, object> { ["general.MaxPlayers"] = 5000L, ["general.Cheats"] = true, ["general.Nope"] = 1L }, CancellationToken.None);
        Assert.AreEqual(400, bad.StatusCode);
        Assert.AreEqual(2, new SettingsEditModel().ApplyServerErrors(bad.Body));
        Assert.AreEqual(false, (await session.GetSettingsAsync(CancellationToken.None)).Value.Sections[0].Settings.First(s => s.Key == "general.Cheats").ServerValue(), "nothing was changed");

        var admin = await session.PutSettingsAsync(new Dictionary<string, object> { ["adminApi.Port"] = 1L }, CancellationToken.None);
        Assert.AreEqual(403, admin.StatusCode);

        var good = await session.PutSettingsAsync(new Dictionary<string, object> { ["general.Cheats"] = true, ["connection.Port"] = 8801L, ["general.Password"] = "pw" }, CancellationToken.None);
        Assert.IsTrue(good.IsOk);
        CollectionAssert.AreEquivalent(new[] { "general.Cheats", "connection.Port", "general.Password" }, good.Value.Applied);
        CollectionAssert.AreEqual(new[] { "connection.Port" }, good.Value.RestartRequired);
        var after = (await session.GetSettingsAsync(CancellationToken.None)).Value;
        var password = after.Sections.SelectMany(s => s.Settings).First(s => s.Key == "general.Password");
        Assert.AreEqual(true, password.IsSet);
        Assert.AreEqual(JsonValueKind.Null, password.Value.ValueKind, "the sample server, like the real one, never returns a secret");
    }
}
