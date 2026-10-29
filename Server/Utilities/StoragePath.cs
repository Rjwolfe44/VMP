using System;
using System.IO;
using System.Linq;

namespace Server.Utilities
{
    public static class StoragePath
    {
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) ||
                name.Any(c => c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0))
                return false;

            var stem = name.Split('.')[0].ToUpperInvariant();
            return stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL" &&
                   !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                         stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                     stem[3] >= '1' && stem[3] <= '9');
        }
    }
}
