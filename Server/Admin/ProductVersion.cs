using System;
using System.Diagnostics;
using System.Reflection;

namespace Server.Admin
{
    /// <summary>The full build string of an assembly (for Server.dll e.g. 0.2.0-train.20261023.10), not just major.minor.build.</summary>
    public static class ProductVersion
    {
        public static string Of(Assembly assembly)
        {
            string product = null;
            try
            {
                if (!string.IsNullOrEmpty(assembly.Location)) product = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
            }
            catch (Exception)
            {
                // Single-file or in-memory assembly: fall through to the other sources.
            }

            return Pick(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, product, assembly.GetName().Version?.ToString());
        }

        /// <summary>The first candidate that has text, trimmed; "unknown" when none has.</summary>
        public static string Pick(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
            }

            return "unknown";
        }
    }
}
