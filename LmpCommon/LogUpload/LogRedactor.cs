using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LmpCommon.LogUpload
{
    /// <summary>
    /// Hides the local account name in profile paths before logs leave the PC:
    /// <c>C:\Users\Alex\...</c> becomes <c>C:\Users\&lt;user&gt;\...</c>. Also handles forward slashes, the doubled
    /// backslashes of JSON / escaped strings, <c>/home/name</c> (Linux) and <c>/Users/name</c> (macOS).
    /// </summary>
    public sealed class LogRedactor
    {
        public const string Placeholder = "<user>";

        private readonly string[] _names;
        private readonly Regex _regex;

        public LogRedactor(IEnumerable<string> userNames)
        {
            _names = (userNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n.Trim())
                .Where(n => n.Length > 0 && n != Placeholder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Longest first so "Alex Smith" wins over "Alex".
                .OrderByDescending(n => n.Length)
                .ToArray();

            if (_names.Length == 0) return;

            var alternatives = string.Join("|", _names.Select(Regex.Escape).ToArray());
            // Profile folder prefix, one or more separators, the name, and then not another name character (so a
            // different account like "Alex2" is left alone).
            _regex = new Regex(@"((?:users|home|documents and settings)[\\/]+)(?:" + alternatives + @")(?![A-Za-z0-9_\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public bool HasNames => _names.Length > 0;

        public string RedactLine(string line)
        {
            if (_regex == null || string.IsNullOrEmpty(line)) return line;

            // Fast path: most log lines do not contain the name at all.
            var found = false;
            for (var i = 0; i < _names.Length; i++)
            {
                if (line.IndexOf(_names[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    break;
                }
            }

            return found ? _regex.Replace(line, "$1" + Placeholder) : line;
        }
    }
}
