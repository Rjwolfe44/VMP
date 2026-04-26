namespace LmpGlobal
{
    /// <summary>
    /// Public URLs, GitHub update feeds, and (optional) master-list raw files. Point these at this fork; AppVeyor
    /// project API slug is separate from the GitHub repo name and must be updated if you re-home CI.
    /// </summary>
    public static class RepoConstants
    {
        private const string GithubOwner = "Rjwolfe44";
        private const string GithubRepo = "VMP";
        private const string GithubBranch = "main";
        private static string GithubRepoBaseUrl => $"https://github.com/{GithubOwner}/{GithubRepo}";
        private static string GithubRawBaseUrl => $"https://raw.githubusercontent.com/{GithubOwner}/{GithubRepo}/{GithubBranch}";

        /// <summary>
        /// When true, the client and server can query the GitHub Releases API for a newer VMP build. Requires a
        /// <b>published</b> (non-draft) "latest" release; draft uploads are invisible to
        /// <c>/repos/.../releases/latest</c>.
        /// </summary>
        public static bool GithubReleaseUpdateChecksEnabled => true;
        public static string OfficialWebsite => GithubRepoBaseUrl;
        public static string RepoUrl => GithubRepoBaseUrl;
        public static string LatestGithubReleaseUrl => $"{GithubRepoBaseUrl}/releases/latest";
        public static string MasterServersListShortUrl => $"{GithubRepoBaseUrl}/blob/{GithubBranch}/MasterServersList/MasterServersList.txt";
        public static string MasterServersListUrl => $"{GithubRawBaseUrl}/MasterServersList/MasterServersList.txt";
        public static string DedicatedServersListUrl => $"{GithubRawBaseUrl}/MasterServersList/DedicatedServersList.txt";
        public static string BannedIpListUrl => $"{GithubRawBaseUrl}/MasterServersList/BannedIpList.txt";
        /// <summary>GitHub "releases/latest": the newest published (non-prerelease) release. Drafts are not included — a draft does not
        /// become "latest" until you publish the release, so the client may not show an update until then.</summary>
        public static string ApiLatestGithubReleaseUrl => $"https://api.github.com/repos/{GithubOwner}/{GithubRepo}/releases/latest";
        public static string AppveyorUrl => string.Empty;
    }
}
