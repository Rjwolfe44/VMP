using System.Text;
using System.Text.RegularExpressions;

namespace VmpControl.Core;

/// <summary>
/// The logic of the Backups tab with no window in it: which names the server will delete, which one is protected, the summary and retention lines,
/// and the texts of the confirmation dialogs. The server decides in the end (it answers 400 for a name it does not know and 409 for the protected one);
/// this mirrors its rules so the window can say so before asking.
/// </summary>
public static class BackupManagement
{
    /// <summary>How many file names a confirmation dialog lists before it says "and N more".</summary>
    public const int MaxListed = 12;

    private static readonly Regex DeletableRx = new(@"\A(?:Universe-[0-9]{8}-[0-9]{6}\.(?:tar\.zst|zip)(?:\.sha256)?|Screenshots-[0-9]{8}-[0-9]{6}\.tar)\z", RegexOptions.CultureInvariant);
    private static readonly Regex ArchiveRx = new(@"\AUniverse-(?<ts>[0-9]{8}-[0-9]{6})\.tar\.zst\z", RegexOptions.CultureInvariant);

    /// <summary>True for exactly the names the server deletes (case-sensitive, no separators): Universe-&lt;time&gt;.tar.zst, .zip, their .sha256, Screenshots-&lt;time&gt;.tar.</summary>
    public static bool IsDeletableName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 64 && DeletableRx.IsMatch(name);

    /// <summary>The newest Universe-*.tar.zst by the time in its name (the server protects this one), or null.</summary>
    public static string NewestArchive(IEnumerable<BackupEntryDto> backups)
    {
        string best = null, bestTime = null;
        foreach (var b in backups ?? Enumerable.Empty<BackupEntryDto>())
        {
            var m = ArchiveRx.Match(b?.Name ?? string.Empty);
            if (!m.Success) continue;
            var time = m.Groups["ts"].Value;
            if (best == null || string.CompareOrdinal(time, bestTime) > 0 || (time == bestTime && string.CompareOrdinal(b.Name, best) > 0))
            {
                best = b.Name;
                bestTime = time;
            }
        }

        return best;
    }

    /// <summary>"14 backups, 112.4 MB of 2048 MB cap" or "14 backups, 112.4 MB, no cap".</summary>
    public static string Summary(BackupsDto d)
    {
        if (d == null) return string.Empty;
        var count = d.Policy != null || d.Count > 0 ? d.Count : d.Backups.Count;
        var total = d.Policy != null || d.TotalBytes > 0 ? d.TotalBytes : d.Backups.Sum(b => b.Bytes);
        var head = $"{count} backup{(count == 1 ? "" : "s")}, {Fmt.Bytes(total)}";
        if (d.Policy == null) return head;
        return d.CapBytes > 0 ? $"{head} of {d.CapBytes / (1024 * 1024)} MB cap" : head + ", no cap";
    }

    public static string RetentionLine(BackupPolicyDto p)
    {
        if (p == null) return "Retention: this server does not report its retention settings (update the server).";
        var zips = p.LegacyZipKeep > 0 ? $"old .zip backups: newest {p.LegacyZipKeep} kept" : "old .zip backups: not pruned automatically";
        var cap = p.MaxTotalMegabytes > 0 ? $"size cap {p.MaxTotalMegabytes} MB" : "no size cap";
        return $"Retention: newest {p.KeepNewest} archives, plus the newest of each of the last {p.KeepDailyDays} days; {p.KeepScreenshotArchives} screenshot archives; {zips}; {cap}.";
    }

    public sealed class DeletePlan
    {
        /// <summary>Names that will be sent to the server to delete.</summary>
        public List<string> Names { get; } = new();
        /// <summary>The newest archive when it was selected: shown with a note and not deleted from here.</summary>
        public List<string> Protected { get; } = new();
        /// <summary>Selected names the server would refuse (not a backup file name).</summary>
        public List<string> Unknown { get; } = new();
        public long Bytes { get; set; }
        public bool IsEmpty => Names.Count == 0;
    }

    public static DeletePlan PlanDelete(BackupsDto all, IEnumerable<string> selected)
    {
        var plan = new DeletePlan();
        var newest = NewestArchive(all?.Backups);
        var sizes = (all?.Backups ?? new List<BackupEntryDto>()).Where(b => b?.Name != null).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First().Bytes);
        foreach (var name in (selected ?? Enumerable.Empty<string>()).Distinct())
        {
            if (!IsDeletableName(name)) plan.Unknown.Add(name);
            else if (name == newest) plan.Protected.Add(name);
            else
            {
                plan.Names.Add(name);
                plan.Bytes += sizes.TryGetValue(name, out var bytes) ? bytes : 0;
            }
        }

        return plan;
    }

    /// <summary>Why nothing can be deleted, for the line under the toolbar.</summary>
    public static string EmptyReason(DeletePlan plan, int selectedCount)
    {
        if (selectedCount == 0) return "Select one or more backups first (Ctrl or Shift click for several).";
        if (plan.Protected.Count > 0 && plan.Unknown.Count == 0) return "The newest archive backup is protected and is never deleted from here. Select older backups.";
        return "Nothing selected can be deleted from here.";
    }

    private static void AppendList(StringBuilder sb, IReadOnlyList<string> names, string suffix = "")
    {
        foreach (var name in names.Take(MaxListed)) sb.Append("  ").Append(name).Append(suffix).Append('\n');
        if (names.Count > MaxListed) sb.Append("  ... and ").Append(names.Count - MaxListed).Append(" more\n");
    }

    public static string DeleteConfirmText(DeletePlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("Delete ").Append(plan.Names.Count).Append(plan.Names.Count == 1 ? " backup" : " backups").Append(" (").Append(Fmt.Bytes(plan.Bytes)).Append(") from the server? This cannot be undone.\n\n");
        AppendList(sb, plan.Names);
        if (plan.Protected.Count > 0)
        {
            sb.Append('\n');
            AppendList(sb, plan.Protected, "  - newest backup, protected: kept");
        }

        return sb.ToString().TrimEnd();
    }

    public static string PruneConfirmText(BackupPruneResultDto dry, bool includeZips)
    {
        var sb = new StringBuilder();
        sb.Append("Pruning applies the retention settings").Append(includeZips ? " and also trims the old .zip backups" : "").Append(".\nThis deletes ")
            .Append(dry.Deleted.Count).Append(dry.Deleted.Count == 1 ? " file" : " files").Append(" and frees ").Append(Fmt.Bytes(dry.FreedBytes)).Append(":\n\n");
        AppendList(sb, dry.Deleted.Select(d => d.Name + "  (" + Fmt.Bytes(d.Bytes) + (string.IsNullOrEmpty(d.Reason) ? "" : ", " + ReasonText(d.Reason)) + ")").ToList());
        if (dry.StillOverCap) sb.Append("\nThe folder stays above the cap: the newest archive and at least 3 archives are always kept.");
        sb.Append("\nDelete them?");
        return sb.ToString().TrimEnd();
    }

    public static string ReasonText(string reason) => reason switch
    {
        "keep-rules" => "over the keep limit",
        "legacy-zip-keep" => "old .zip",
        "size-cap" => "size cap",
        _ => reason ?? "",
    };

    /// <summary>The line for a prune that found nothing to do.</summary>
    public static string NothingToPrune(BackupPruneResultDto dry, bool includeZips)
        => "Nothing to prune: the retention settings" + (includeZips ? " (old .zip backups included)" : "") + " are already met."
           + (dry.StillOverCap ? " The folder is above its cap, but the newest archive and at least 3 archives are always kept." : "");
}
