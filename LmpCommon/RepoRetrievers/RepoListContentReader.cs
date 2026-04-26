using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace LmpCommon.RepoRetrievers
{
    /// <summary>
    /// Loads repository-backed list files from GitHub first and falls back to shipped local copies if the network copy
    /// is unavailable. This keeps server browsing and master-server registration working even when GitHub raw is down
    /// or the user is running from packaged artifacts instead of the repo root.
    /// </summary>
    public static class RepoListContentReader
    {
        public static bool TryReadNormalizedLines(string remoteUrl, string localFileName, out string[] lines)
        {
            lines = Array.Empty<string>();

            var content = TryReadRemoteText(remoteUrl) ?? TryReadLocalText(localFileName);
            if (content == null)
                return false;

            lines = content
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal))
                .ToArray();

            return true;
        }

        private static string TryReadRemoteText(string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return null;

            try
            {
                ServicePointManager.ServerCertificateValidationCallback = GithubCertification.MyRemoteCertificateValidationCallback;
                using (var client = new WebClient())
                using (var stream = client.OpenRead(remoteUrl))
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string TryReadLocalText(string localFileName)
        {
            foreach (var candidate in GetCandidatePaths(localFileName))
            {
                try
                {
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate);
                }
                catch (Exception)
                {
                    // Ignore broken candidate paths and continue.
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidatePaths(string localFileName)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddCandidatePaths(candidates, Environment.CurrentDirectory, localFileName);
            AddCandidatePaths(candidates, AppDomain.CurrentDomain.BaseDirectory, localFileName);

            try
            {
                AddCandidatePaths(candidates, Path.GetDirectoryName(typeof(RepoListContentReader).Assembly.Location), localFileName);
            }
            catch (Exception)
            {
                // Ignore missing assembly locations and keep other candidates.
            }

            return candidates;
        }

        private static void AddCandidatePaths(ISet<string> candidates, string baseDirectory, string localFileName)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                return;

            string currentDirectory;
            try
            {
                currentDirectory = Path.GetFullPath(baseDirectory);
            }
            catch (Exception)
            {
                return;
            }

            for (var depth = 0; depth < 3 && !string.IsNullOrWhiteSpace(currentDirectory); depth++)
            {
                candidates.Add(Path.Combine(currentDirectory, localFileName));
                candidates.Add(Path.Combine(currentDirectory, "MasterServersList", localFileName));
                candidates.Add(Path.Combine(currentDirectory, "GameData", ModLayoutConstants.GameDataModFolder, "MasterServersList", localFileName));

                var parentDirectory = Directory.GetParent(currentDirectory);
                if (parentDirectory == null)
                    break;

                currentDirectory = parentDirectory.FullName;
            }
        }
    }
}