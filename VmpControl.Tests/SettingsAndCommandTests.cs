using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

[TestClass]
public class SettingsTests
{
    private string _dir;

    [TestInitialize]
    public void Init() => _dir = Path.Combine(Path.GetTempPath(), "vmpcontrol-tests-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Clean()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [TestMethod]
    public void Defaults_AreOneRemoteTrainPcProfilePlusAHostTemplate()
    {
        var s = AppSettings.CreateDefault();

        Assert.AreEqual(2, s.Profiles.Count);
        var remote = s.Profiles[0];
        Assert.AreEqual("Train PC", remote.Name);
        Assert.AreEqual(ProfileMode.Remote, remote.Mode);
        Assert.AreEqual("trainpc", remote.SshAlias);
        Assert.AreEqual(@"C:\VMP Server", remote.RemoteServerRoot);
        Assert.AreEqual(18900, remote.RemoteApiPort);
        Assert.AreEqual(18901, remote.LocalTunnelPort);
        Assert.AreEqual(string.Empty, remote.ControlScriptPath);
        Assert.AreEqual(18901, remote.LocalApiPort);
        Assert.IsNotNull(remote.Validate(), "the control script path has no default, so a fresh profile asks to be set up");
        var host = s.Profiles[1];
        Assert.AreEqual(ProfileMode.Host, host.Mode);
        Assert.AreEqual(18900, host.ApiPort);
        Assert.AreEqual(18900, host.LocalApiPort);
        Assert.IsTrue(s.Notifications);
        Assert.AreEqual("Train PC", s.ActiveProfile);
        Assert.AreEqual(string.Empty, s.FetchLogsScriptPath);
    }

    [TestMethod]
    public void RoundTrip_KeepsEveryValue()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        var s = AppSettings.CreateDefault();
        s.Notifications = false;
        s.ActiveProfile = "This PC (host)";
        s.Profiles[0].SshAlias = "other-box";
        s.Profiles[0].LocalTunnelPort = 19999;
        s.Profiles[1].ServerFolder = @"D:\Games\VMP";
        s.Profiles[1].ApiPort = 9001;
        s.Profiles.Add(new Profile { Name = "Third", Mode = ProfileMode.Host, ServerFolder = @"E:\x" });

        s.Save(path);
        var back = AppSettings.Load(path);

        Assert.AreEqual(3, back.Profiles.Count);
        Assert.IsFalse(back.Notifications);
        Assert.AreEqual("This PC (host)", back.ActiveProfile);
        Assert.AreEqual("other-box", back.Profiles[0].SshAlias);
        Assert.AreEqual(19999, back.Profiles[0].LocalTunnelPort);
        Assert.AreEqual(ProfileMode.Remote, back.Profiles[0].Mode);
        Assert.AreEqual(@"D:\Games\VMP", back.Profiles[1].ServerFolder);
        Assert.AreEqual(9001, back.Profiles[1].ApiPort);
        Assert.AreEqual(ProfileMode.Host, back.Profiles[2].Mode);
        StringAssert.Contains(File.ReadAllText(path), "\"Mode\": \"Host\"");
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public void MissingEmptyOrCorruptFile_GivesDefaults_AndNeverThrows()
    {
        Directory.CreateDirectory(_dir);
        Assert.AreEqual(2, AppSettings.Load(Path.Combine(_dir, "nope.json")).Profiles.Count);

        var empty = Path.Combine(_dir, "empty.json");
        File.WriteAllText(empty, "");
        Assert.AreEqual(2, AppSettings.Load(empty).Profiles.Count);

        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ \"Profiles\": [ { \"Name\": ");
        Assert.AreEqual("Train PC", AppSettings.Load(broken).Profiles[0].Name);

        var noProfiles = Path.Combine(_dir, "none.json");
        File.WriteAllText(noProfiles, "{\"Profiles\":[],\"Notifications\":false}");
        Assert.AreEqual(2, AppSettings.Load(noProfiles).Profiles.Count);
    }

    [TestMethod]
    public void MissingFieldsInAnOlderFile_KeepTheirDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, "{\"Profiles\":[{\"Name\":\"Only\",\"SshAlias\":\"box\"}]}");

        var s = AppSettings.Load(path);

        Assert.AreEqual("box", s.Profiles[0].SshAlias);
        Assert.AreEqual(18901, s.Profiles[0].LocalTunnelPort);
        Assert.IsTrue(s.Notifications);
    }

    [TestMethod]
    public void SavedFile_HasNoPlaceForATokenOrPassword()
    {
        var path = Path.Combine(_dir, "settings.json");
        AppSettings.CreateDefault().Save(path);
        var text = File.ReadAllText(path).ToLowerInvariant();

        Assert.IsFalse(text.Contains("token"));
        Assert.IsFalse(text.Contains("password"));
        Assert.IsFalse(text.Contains("bearer"));
    }

    [TestMethod]
    public void Pick_PrefersTheRequestedName_ThenTheActiveOne_ThenTheFirst()
    {
        var s = AppSettings.CreateDefault();
        s.ActiveProfile = "this pc (host)";

        Assert.AreEqual("Train PC", s.Pick("train pc").Name);
        Assert.AreEqual("This PC (host)", s.Pick(null).Name);
        Assert.AreEqual("This PC (host)", s.Pick("missing").Name);
        s.ActiveProfile = "gone";
        Assert.AreEqual("Train PC", s.Pick(null).Name);
    }

    [TestMethod]
    public void Validate_RejectsBadAliasPortsRootAndEmptyHostFolder()
    {
        var p = Profile.CreateRemoteDefault();
        Assert.IsNotNull(p.Validate(), "a fresh profile has no control script path yet");
        p.ControlScriptPath = @"C:\tools\vmp-server-control.ps1";
        Assert.IsNull(p.Validate());
        p.SshAlias = "bad alias";
        Assert.IsNotNull(p.Validate());
        p.SshAlias = "-oProxyCommand=calc";
        Assert.IsNotNull(p.Validate());
        p.SshAlias = "trainpc";
        p.LocalTunnelPort = 70000;
        Assert.IsNotNull(p.Validate());
        p.LocalTunnelPort = 18901;
        p.RemoteServerRoot = "C:\\x\" & calc";
        Assert.IsNotNull(p.Validate());
        p.RemoteServerRoot = @"relative\path";
        Assert.IsNotNull(p.Validate());
        p.RemoteServerRoot = @"C:\Users\Sam Doe\Desktop\VMP Server";
        Assert.IsNull(p.Validate());

        var host = Profile.CreateHostTemplate();
        Assert.IsNull(host.Validate());
        host.ServerFolder = " ";
        Assert.IsNotNull(host.Validate());
        host.ServerFolder = @"C:\s";
        host.ApiPort = 0;
        Assert.IsNotNull(host.Validate());
    }

    [TestMethod]
    public void LiveSession_RefusesAnInvalidProfile_BeforeStartingAnything()
    {
        Assert.ThrowsException<ArgumentException>(() => new LiveSession(new Profile { SshAlias = "bad alias;rm" }));
        Assert.ThrowsException<ArgumentException>(() => new TunnelManager("bad alias", 1, 2));
    }
}

[TestClass]
public class SshCommandTests
{
    [DataTestMethod]
    [DataRow("trainpc", true)]
    [DataRow("train-pc.2_b", true)]
    [DataRow("A", true)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("train pc", false)]
    [DataRow("a;b", false)]
    [DataRow("a$(calc)", false)]
    [DataRow("a\"b", false)]
    [DataRow("-oProxyCommand=x", false)]
    [DataRow("-v", false)]
    [DataRow("user@host", false)]
    [DataRow("тест", false)]
    [DataRow("a\nb", false)]
    public void Alias_IsValidatedAgainstLettersDigitsUnderscoreDotDash(string alias, bool valid)
    {
        Assert.AreEqual(valid, SshCommands.IsValidAlias(alias));
    }

    [TestMethod]
    public void TunnelArgs_AreAnArray_WithTheDocumentedOptions()
    {
        var args = SshCommands.TunnelArgs("trainpc", 18901, 8901);

        CollectionAssert.AreEqual(new[]
        {
            "-N", "-T", "-L", "127.0.0.1:18901:127.0.0.1:8901",
            "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ConnectTimeout=10",
            "trainpc",
        }, args);
        Assert.AreEqual("trainpc", args[^1]);
    }

    [TestMethod]
    public void TokenReadArgs_UseAnEncodedPowerShellCommand_SoPathsWithSpacesSurvive()
    {
        var args = SshCommands.TokenReadArgs("trainpc", @"C:\Users\Sam Doe\Desktop\VMP Server\");

        CollectionAssert.AreEqual(new[]
        {
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-n", "-T", "trainpc", "powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand",
        }, args.Take(args.Length - 1).ToArray());
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(args[args.Length - 1]));
        StringAssert.Contains(script, @"ReadAllText('C:\Users\Sam Doe\Desktop\VMP Server\Config\admin-token.txt')");

        var quoted = SshCommands.TokenReadArgs("trainpc", @"C:\Users\O'Neil\VMP\");
        StringAssert.Contains(System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(quoted[quoted.Length - 1])), "O''Neil");
    }

    [TestMethod]
    public void ShutdownArgs_MatchTheDocumentedCommands()
    {
        var restart = SshCommands.ShutdownRestartArgs("trainpc");
        CollectionAssert.AreEqual(new[] { "shutdown", "/r", "/t", "30", "/c", "\"VMP Control restart\"" }, restart.Skip(restart.Length - 6).ToArray());
        Assert.AreEqual("trainpc", restart[restart.Length - 7]);

        var abort = SshCommands.ShutdownAbortArgs("trainpc");
        CollectionAssert.AreEqual(new[] { "trainpc", "shutdown", "/a" }, abort.Skip(abort.Length - 3).ToArray());
    }

    [TestMethod]
    public void BadInput_NeverReachesAnArgumentList()
    {
        Assert.ThrowsException<ArgumentException>(() => SshCommands.TunnelArgs("a b", 1, 2));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.TokenReadArgs("-oProxyCommand=x", @"C:\x"));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.TokenReadArgs("trainpc", "C:\\x\" & calc & \""));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.TokenReadArgs("trainpc", @"C:\%USERPROFILE%"));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.ShutdownRestartArgs("x;y"));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.ShutdownAbortArgs(""));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => SshCommands.TunnelArgs("trainpc", 0, 8901));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => SshCommands.TunnelArgs("trainpc", 1, 65536));
    }

    [TestMethod]
    public void ScriptArgs_TakeOnlyKnownActions_AndAddYesOnlyWhenAsked()
    {
        var withYes = SshCommands.ScriptArgs(@"C:\a b\vmp-server-control.ps1", "restart", true);
        CollectionAssert.AreEqual(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\a b\vmp-server-control.ps1", "-Action", "Restart", "-Yes" }, withYes);
        var without = SshCommands.ScriptArgs("x.ps1", "Status", false);
        Assert.AreEqual("Status", without[^1]);
        Assert.IsFalse(without.Contains("-Yes"));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.ScriptArgs("x.ps1", "Format-Disk", true));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.ScriptArgs("x.ps1", "Stop; calc", true));
        Assert.ThrowsException<ArgumentException>(() => SshCommands.ScriptArgs(" ", "Stop", true));
    }
}

[TestClass]
public class LoggingTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void TheTokenNeverAppearsInALoggedLine()
    {
        var log = new ActivityLog();
        log.SetSecret(Token);
        var seen = new List<string>();
        log.Added += e => seen.Add(e.Action + "|" + e.Result);

        log.Add("Say", "failed: server said " + Token + " is wrong");
        log.Add("Authorization: Bearer " + Token, "ok");
        log.Add("curl", "sent header Authorization: bearer " + Token.ToUpperInvariant());
        log.Add("Backup", "sha256 " + Token.Substring(0, 8)); // a prefix is not the token

        foreach (var entry in log.Snapshot()) Assert.IsFalse((entry.Action + entry.Result).Contains(Token, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(seen.Any(l => l.Contains(Token)));
        StringAssert.Contains(log.Snapshot()[0].Result, "[token]");
        StringAssert.Contains(log.Snapshot()[1].Action, "Bearer [token]");
        Assert.AreEqual("Bearer [token]", ActivityLog.Redact("Bearer anything-here", null));
    }

    [TestMethod]
    public async Task TheApiClient_NeverPutsTheTokenInAnErrorMessage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vmpcontrol-tok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Config"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "Config", "admin-token.txt"), Token + "\r\n");
            var tokens = TokenSource.ForFile(folder);
            using var client = new AdminApiClient(1, tokens); // nothing listens on port 1
            var result = await client.GetAsync<StatusDto>("status", CancellationToken.None, 2);

            Assert.AreEqual(ApiOutcome.Unreachable, result.Outcome);
            Assert.IsFalse((result.Error ?? "").Contains(Token));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

[TestClass]
public class CommandLineTests
{
    [TestMethod]
    public void Switches_AreParsed()
    {
        var o = CommandLineOptions.Parse(new[] { "--mock", "--profile", "Train PC", "--screenshot", @"C:\shots", "--minimized", "--server-folder", @"C:\srv", "--api-port", "8902", "--settings", "s.json" });

        Assert.IsTrue(o.Mock);
        Assert.IsTrue(o.Minimized);
        Assert.AreEqual("Train PC", o.Profile);
        Assert.AreEqual(@"C:\shots", o.ScreenshotFolder);
        Assert.AreEqual(@"C:\srv", o.ServerFolder);
        Assert.AreEqual(8902, o.ApiPort);
        Assert.AreEqual("s.json", o.SettingsPath);
        Assert.AreEqual(0, o.Problems.Count);
    }

    [TestMethod]
    public void NoSwitches_MeansNormalStart_AndProblemsAreReported()
    {
        var none = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.IsFalse(none.Mock || none.Minimized);
        Assert.IsNull(none.Profile);

        var bad = CommandLineOptions.Parse(new[] { "--frobnicate", "--api-port", "99999", "--screenshot" });
        Assert.AreEqual(3, bad.Problems.Count);
        Assert.IsNull(bad.ApiPort);
    }
}
