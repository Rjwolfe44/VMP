using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LmpCommon.KerbalKonstructs;

/// <summary>
/// Kerbal Konstructs static sharing (docs/kk-live-sharing.md) over the real protocol: player A edits a launch site on
/// a pack static, an old client without the feature uploads in between, A edits it again, and C joins late.
/// The client side (Harmony hooks in KSP) is not simulated; the uploads carry the VmpSharedStatics node the client
/// appends after KK's OnSave, and C's checks are what the client's OnLoad hook would read.
/// </summary>
static class SharedStaticsScript
{
    private const string Module = ScenarioScript.Module;
    private const string Key = "uuid:7d2c3f1e-0000-4000-8000-00000000pad2";

    private static int _failures;

    private static void Report(string step, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {step}  {detail}");
    }

    /// <summary>One shared entry as the client builds it from the instance node KK wrote to its cfg file.</summary>
    public static string Entry(string siteName, string facilityType, string description)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\t" + KkSharedStaticsFormat.EntryName);
        sb.AppendLine("\t{");
        sb.AppendLine("\t\t" + KkSharedStaticsFormat.FormatField + " = " + KkSharedStaticsFormat.FormatVersion);
        sb.AppendLine("\t\t" + KkSharedStaticsFormat.KeyField + " = " + Key);
        sb.AppendLine("\t\tModelName = KSC_LaunchPad_level_3");
        sb.AppendLine("\t\tUUID = " + KkSharedStaticsFormat.UuidOf(Key));
        sb.AppendLine("\t\tCelestialBody = Kerbin");
        sb.AppendLine("\t\tRelativePosition = 412.5,2.1,-87.25");
        sb.AppendLine("\t\tOrientation = 0,90,0");
        sb.AppendLine("\t\tGroup = KSC");
        sb.AppendLine("\t\tLaunchSite");
        sb.AppendLine("\t\t{");
        sb.AppendLine("\t\t\tFacilityType = KKLaunchSite");
        sb.AppendLine("\t\t\tLaunchSiteName = " + siteName);
        sb.AppendLine("\t\t\tLaunchPadTransform = PlatformPlane");
        sb.AppendLine("\t\t\tLaunchSiteDescription = " + description);
        sb.AppendLine("\t\t\tOpenCloseState = Open");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tFacility");
        sb.AppendLine("\t\t{");
        sb.AppendLine("\t\t\tFacilityType = " + facilityType);
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t}");
        return sb.ToString();
    }

    public static string WithShared(string blob, params string[] entries)
    {
        var sb = new StringBuilder(StripShared(blob).TrimEnd('\r', '\n')).AppendLine();
        sb.AppendLine(KkSharedStaticsFormat.ContainerName);
        sb.AppendLine("{");
        foreach (var e in entries) sb.Append(e);
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>What an old client sends: KK rebuilds its node on save, so the VMP container is simply absent.</summary>
    public static string StripShared(string blob) =>
        Regex.Replace(blob, @"(?ms)^[ \t]*" + KkSharedStaticsFormat.ContainerName + @"\s*\{.*", "").TrimEnd() + Environment.NewLine;

    private static string SetMissionCount(string text, int count) =>
        Regex.Replace(text, @"(LAUNCHPAD\s*\{\s*MissionCount\s*=\s*)\d+", "${1}" + count);

    private static string MissionCount(string text)
    {
        var m = Regex.Match(text ?? "", @"LAUNCHPAD\s*\{\s*MissionCount\s*=\s*(\d+)");
        return m.Success ? m.Groups[1].Value : "<missing>";
    }

    /// <summary>The VmpSharedStatics part of a module text (it is the last node), or "".</summary>
    private static string Container(string text)
    {
        var m = Regex.Match(text ?? "", @"(?ms)^[ \t]*" + KkSharedStaticsFormat.ContainerName + @"\s*\{.*");
        return m.Success ? m.Value : "";
    }

    private static int EntryCount(string text) =>
        Regex.Matches(text ?? "", @"(?m)^\s*" + KkSharedStaticsFormat.EntryName + @"\s*$").Count;

    private static string SiteName(string text)
    {
        var m = Regex.Match(text ?? "", @"LaunchSiteName\s*=\s*(\S+)");
        return m.Success ? m.Groups[1].Value : "<missing>";
    }

    private static string FacilityType(string text)
    {
        var m = Regex.Match(text ?? "", @"(?ms)^\s*Facility\s*\{\s*FacilityType\s*=\s*(\S+)");
        return m.Success ? m.Groups[1].Value : "<missing>";
    }

    private static string Summary(string text) =>
        $"entries={EntryCount(text)} site={SiteName(text)} facility={FacilityType(text)} lastLaunchSiteUsed={ScenarioScript.LastSite(text)}";

    public static int Run(string host, int port, string universeFile, int saveWaitSeconds)
    {
        var timeout = TimeSpan.FromSeconds(15);
        using var a = new ScenarioPeer("KK_A");
        using var old = new ScenarioPeer("KK_OLD");

        string err = a.ConnectAndHandshake(host, port, timeout) ?? a.FetchScenarios(timeout);
        Report("1a client A joined + fetched", err == null, err ?? Summary(a.LocalKerbalKonstructs));
        if (err != null) return 1;
        err = old.ConnectAndHandshake(host, port, timeout) ?? old.FetchScenarios(timeout);
        Report("1b old client joined + fetched", err == null, err ?? Summary(old.LocalKerbalKonstructs));
        if (err != null) return 1;
        Report("1c nothing shared yet", EntryCount(a.LocalKerbalKonstructs) == 0, Summary(a.LocalKerbalKonstructs));

        // A edits a pack static in KK's launch site and facility editors: the save carries the shared entry.
        var aText = WithShared(ScenarioScript.SetSaveTime(a.LocalKerbalKonstructs, "150"), Entry("KSC_PAD_2", "Hangar", "first"));
        a.UploadModule(Module, aText);
        a.Idle(TimeSpan.FromSeconds(1));
        Report("2  A uploaded its edit of KSC_PAD_2", true, Summary(aText));

        // An old client (no sharing code) saves an unrelated change; its upload has no VmpSharedStatics at all.
        var oldText = StripShared(ScenarioScript.SetLastSite(old.LocalKerbalKonstructs, "KSC2"));
        old.UploadModule(Module, oldText);
        old.Idle(TimeSpan.FromSeconds(1));
        Report("3  old client uploaded lastLaunchSiteUsed=KSC2 without the container", !oldText.Contains(KkSharedStaticsFormat.ContainerName), Summary(oldText));

        // A changes its mind: same static, different facility and description. Last writer wins for the whole entry.
        var aText2 = WithShared(ScenarioScript.SetSaveTime(aText, "180"), Entry("KSC_PAD_2", "Merchant", "second"));
        a.UploadModule(Module, aText2);
        a.Idle(TimeSpan.FromSeconds(1));
        Report("4  A uploaded a second edit (facility Merchant)", true, Summary(aText2));

        // A hostile (or broken) entry with VmpKey written twice, in an upload that also counts one launch. Sent twice,
        // as the 30 s sync would: the entry is refused, the launch is counted exactly once, later uploads still merge.
        var hostile = Entry("HOSTILE", "Hangar", "x").Replace("\t\tModelName", "\t\t" + KkSharedStaticsFormat.KeyField + " = uuid:second\n\t\tModelName");
        var aText3 = WithShared(SetMissionCount(ScenarioScript.SetSaveTime(aText2, "190"), 1), Entry("KSC_PAD_2", "Merchant", "second"), hostile);
        a.UploadModule(Module, aText3);
        a.Idle(TimeSpan.FromSeconds(1));
        a.UploadModule(Module, aText3);
        a.Idle(TimeSpan.FromSeconds(1));
        Report("4b A uploaded an entry with two VmpKey values (twice) plus one launch", true, "");

        using var c = new ScenarioPeer("KK_C");
        err = c.ConnectAndHandshake(host, port, timeout) ?? c.FetchScenarios(timeout);
        Report("5a late joiner C joined + fetched", err == null, err ?? Summary(c.LocalKerbalKonstructs));
        if (err != null) return 1;
        var cText = c.LocalKerbalKonstructs;
        Report("5b C receives exactly one shared static", EntryCount(cText) == 1, Summary(cText));
        Report("5c C gets A's launch site with its latest facility", SiteName(cText) == "KSC_PAD_2" && FacilityType(cText) == "Merchant" && cText.Contains("LaunchSiteDescription = second"),
            Summary(cText));
        Report("5d the old client's change is kept too", ScenarioScript.LastSite(cText) == "KSC2", "lastLaunchSiteUsed=" + ScenarioScript.LastSite(cText));
        Report("5f the hostile entry was refused and the launch counted once", !cText.Contains("HOSTILE") && MissionCount(cText) == "1",
            "LAUNCHPAD MissionCount=" + MissionCount(cText));
        var container = Container(cText);
        Report("5e nothing of the first edit is left inside the entry", container.Length > 0 && !container.Contains("Hangar") && !container.Contains("= first"), "");

        // C (a new client) sends back what it received unchanged: that must not create a copy or change anything.
        c.UploadModule(Module, ScenarioScript.SetSaveTime(cText, "200"));
        c.Idle(TimeSpan.FromSeconds(1));
        using var d = new ScenarioPeer("KK_D");
        err = d.ConnectAndHandshake(host, port, timeout) ?? d.FetchScenarios(timeout);
        Report("6  C's echo leaves one unchanged entry (seen by D)", err == null && EntryCount(d.LocalKerbalKonstructs) == 1 && FacilityType(d.LocalKerbalKonstructs) == "Merchant",
            err ?? Summary(d.LocalKerbalKonstructs));

        if (!string.IsNullOrEmpty(universeFile))
        {
            var timer = Stopwatch.StartNew();
            string disk = null;
            while (timer.Elapsed < TimeSpan.FromSeconds(saveWaitSeconds))
            {
                try { disk = File.ReadAllText(universeFile); } catch (IOException) { }
                if (disk != null && FacilityType(disk) == "Merchant") break;
                Thread.Sleep(500);
            }
            if (disk != null && FacilityType(disk) == "Merchant")
                Report("7  the server's scenario file holds the shared static", EntryCount(disk) == 1 && SiteName(disk) == "KSC_PAD_2", $"after {timer.Elapsed.TotalSeconds:F1} s: " + Summary(disk));
            else
                Console.WriteLine($"SKIP  7  disk file not updated within {saveWaitSeconds} s ({(disk == null ? "unreadable" : Summary(disk))})");
        }

        Console.WriteLine(_failures == 0 ? "RESULT: all steps PASS" : $"RESULT: {_failures} step(s) FAIL");
        return _failures == 0 ? 0 : 1;
    }
}
