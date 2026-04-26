namespace LmpGlobal
{
    /// <summary>
    /// Public URLs, GitHub update feeds, and (optional) master-list raw files. Point these at this fork; AppVeyor
    /// project API slug is separate from the GitHub repo name and must be updated if you re-home CI.
    /// </summary>
    public static class RepoConstants
    {
        /// <summary>
        /// When true, the client and server can query the GitHub Releases API for a newer VMP build. Requires a
        /// <b>published</b> (non-draft) "latest" release; draft uploads are invisible to
        /// <c>/repos/.../releases/latest</c>.
        /// </summary>
        public static bool GithubReleaseUpdateChecksEnabled => false;
        public static string OfficialWebsite => "https://example.invalid/VladMultiplayer";
        public static string RepoUrl => "https://example.invalid/VladMultiplayer";
        public static string LatestGithubReleaseUrl => "https://example.invalid/VladMultiplayer/releases/latest";
        public static string MasterServersListShortUrl => "https://example.invalid/VladMultiplayer/MasterServersList.txt";
        public static string MasterServersListUrl => "https://example.invalid/VladMultiplayer/MasterServersList.txt";
        public static string DedicatedServersListUrl => "https://example.invalid/VladMultiplayer/DedicatedServersList.txt";
        public static string BannedIpListUrl => "https://example.invalid/VladMultiplayer/BannedIpList.txt";
        /// <summary>GitHub "releases/latest": the newest published (non-prerelease) release. Drafts are not included — a draft does not
        /// become "latest" until you publish the release, so the client may not show an update until then.</summary>
        public static string ApiLatestGithubReleaseUrl => "https://example.invalid/VladMultiplayer/releases/latest.json";
        public static string AppveyorUrl => "https://example.invalid/VladMultiplayer/ci";
    }
}
