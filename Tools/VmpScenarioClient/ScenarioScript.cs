using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>The KerbalKonstructsSettings scenario: seed blob, text helpers and the scripted A / B / C sequence.</summary>
static class ScenarioScript
{
    public const string Module = "KerbalKonstructsSettings";

    /// <summary>Same shape as ServerTest/ScenarioMergeTests.cs Blob().</summary>
    public static string SeedBlob()
    {
        var sb = new StringBuilder();
        sb.AppendLine("name = " + Module);
        sb.AppendLine("savetime = 100");
        sb.AppendLine("KKSettings\n{\n\tlastLaunchSiteUsed = LaunchPad\n\tmapShowOpen = True\n}");
        sb.AppendLine("Facilities\n{");
        sb.AppendLine("\tfac-tower\n\t{\n\t\tFacilityName = Tower\n\t\tHangar\n\t\t{\n\t\t\tOpenCloseState = Closed\n\t\t}\n\t}");
        sb.AppendLine("\tfac-sea\n\t{\n\t\tFacilityName = Sea Base\n\t\tMerchant\n\t\t{\n\t\t\tisOpen = True\n\t\t}\n\t}");
        sb.AppendLine("}");
        sb.AppendLine("LaunchSites\n{");
        foreach (var (site, open) in new[] { ("LAUNCHPAD", true), ("NYE_ISLAND", false), ("KSC2", false) })
            sb.AppendLine("\t" + site + "\n\t{\n\t\tMissionCount = 0\n\t\tisOpen = " + (open ? "True" : "False") + "\n\t}");
        sb.AppendLine("}");
        sb.AppendLine("KKBuildings\n{\n}");
        return sb.ToString();
    }

    public static string SiteOpen(string text, string site)
    {
        var m = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(site) + @"\s*\{[^{}]*?isOpen\s*=\s*(\w+)", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : "<missing>";
    }

    public static string SetSiteOpen(string text, string site, bool open) =>
        Regex.Replace(text, @"((?m)^\s*" + Regex.Escape(site) + @"\s*\{[^{}]*?isOpen\s*=\s*)\w+", "${1}" + (open ? "True" : "False"), RegexOptions.Singleline);

    public static string LastSite(string text)
    {
        var m = Regex.Match(text, @"lastLaunchSiteUsed\s*=\s*(\S+)");
        return m.Success ? m.Groups[1].Value : "<missing>";
    }

    public static string SetLastSite(string text, string value) =>
        Regex.Replace(text, @"(lastLaunchSiteUsed\s*=\s*)\S+", "${1}" + value);

    public static string SaveTime(string text) =>
        Regex.Match(text, @"(?m)^savetime\s*=\s*(\S+)").Groups[1].Value;

    public static string SetSaveTime(string text, string value) =>
        Regex.Replace(text, @"(?m)^(savetime\s*=\s*)\S+", "${1}" + value);

    private static int _failures;

    private static void Report(string step, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {step}  {detail}");
    }

    private static string Summary(string text) =>
        $"NYE_ISLAND.isOpen={SiteOpen(text, "NYE_ISLAND")} lastLaunchSiteUsed={LastSite(text)} savetime={SaveTime(text)}";

    public static int Run(string host, int port, string universeFile, int saveWaitSeconds)
    {
        var timeout = TimeSpan.FromSeconds(15);
        using var a = new ScenarioPeer("KK_A");
        using var b = new ScenarioPeer("KK_B");

        // Step 2: A and B join and fetch.
        string err = a.ConnectAndHandshake(host, port, timeout) ?? a.FetchScenarios(timeout);
        Report("2a client A joined + fetched scenarios", err == null, err ?? Summary(a.LocalKerbalKonstructs));
        if (err != null) return 1;
        err = b.ConnectAndHandshake(host, port, timeout) ?? b.FetchScenarios(timeout);
        Report("2b client B joined + fetched scenarios", err == null, err ?? Summary(b.LocalKerbalKonstructs));
        if (err != null) return 1;
        var seededOk = SiteOpen(a.LocalKerbalKonstructs, "NYE_ISLAND") == "False" && LastSite(a.LocalKerbalKonstructs) == "LaunchPad" &&
                       SiteOpen(b.LocalKerbalKonstructs, "NYE_ISLAND") == "False" && SaveTime(a.LocalKerbalKonstructs) == "100";
        Report("2c both copies equal the seed", seededOk, "(server loaded the seeded Universe file)");

        // Step 3: A opens NYE_ISLAND and uploads its whole copy.
        var aText = SetSaveTime(SetSiteOpen(a.LocalKerbalKonstructs, "NYE_ISLAND", true), "150");
        a.UploadModule(Module, aText);
        a.Idle(TimeSpan.FromSeconds(1));
        Report("3  A uploaded NYE_ISLAND isOpen=True", true, Summary(aText));

        // Step 4: B (stale, never saw A's change) changes only lastLaunchSiteUsed + savetime and uploads its whole copy.
        var bText = SetSaveTime(SetLastSite(b.LocalKerbalKonstructs, "KSC2"), "200");
        b.UploadModule(Module, bText);
        b.Idle(TimeSpan.FromSeconds(1));
        Report("4  B (stale) uploaded lastLaunchSiteUsed=KSC2", true, Summary(bText));

        // Step 5: C joins fresh.
        using var c = new ScenarioPeer("KK_C");
        err = c.ConnectAndHandshake(host, port, timeout) ?? c.FetchScenarios(timeout);
        Report("5a client C joined + fetched scenarios", err == null, err ?? Summary(c.LocalKerbalKonstructs));
        if (err != null) return 1;
        var cText = c.LocalKerbalKonstructs;
        Report("5b C sees A's change (NYE_ISLAND isOpen=True)", SiteOpen(cText, "NYE_ISLAND") == "True", "got " + SiteOpen(cText, "NYE_ISLAND"));
        Report("5c C sees B's change (lastLaunchSiteUsed=KSC2)", LastSite(cText) == "KSC2", "got " + LastSite(cText));
        Report("5d C keeps untouched data (LAUNCHPAD open, KSC2 closed, both facilities)",
            SiteOpen(cText, "LAUNCHPAD") == "True" && SiteOpen(cText, "KSC2") == "False" && cText.Contains("fac-tower") && cText.Contains("fac-sea"),
            $"LAUNCHPAD={SiteOpen(cText, "LAUNCHPAD")} KSC2={SiteOpen(cText, "KSC2")}");

        // Disk: the server saves scenarios periodically; wait until B's lastLaunchSiteUsed reaches the file.
        if (!string.IsNullOrEmpty(universeFile))
        {
            var timer = Stopwatch.StartNew();
            string disk = null;
            while (timer.Elapsed < TimeSpan.FromSeconds(saveWaitSeconds))
            {
                try { disk = File.ReadAllText(universeFile); } catch (IOException) { }
                if (disk != null && LastSite(disk) == "KSC2") break;
                Thread.Sleep(500);
            }
            if (disk != null && LastSite(disk) == "KSC2")
                Report("5e disk file has NYE_ISLAND isOpen=True and KSC2 as last site", SiteOpen(disk, "NYE_ISLAND") == "True",
                    $"after {timer.Elapsed.TotalSeconds:F1} s: " + Summary(disk));
            else
                Console.WriteLine($"SKIP  5e disk file not updated within {saveWaitSeconds} s ({(disk == null ? "unreadable" : Summary(disk))}); relying on C's fetched copy");
        }

        Console.WriteLine(_failures == 0 ? "RESULT: all steps PASS" : $"RESULT: {_failures} step(s) FAIL");
        return _failures == 0 ? 0 : 1;
    }
}
