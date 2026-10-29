using System;
using System.Text;

namespace LmpCommon.LogUpload
{
    /// <summary>
    /// Turns the first line of a log message into a stable key, so a thousand repeats of the same error (with different
    /// numbers or ids in them) count as one signature. A plain character loop: it runs on the thread that logs the error,
    /// and a log flood can be tens of thousands of lines per second.
    /// </summary>
    public static class LogSignature
    {
        public const int MaxLength = 120;

        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(Math.Min(text.Length, MaxLength));
            var lastWasHash = false;
            var lastWasSpace = false;
            for (var i = 0; i < text.Length && sb.Length < MaxLength; i++)
            {
                var c = text[i];
                if (c == '\n' || c == '\r') break;

                if (IsHex(c) && (i == 0 || !IsWordChar(text[i - 1])) && IsGuidAt(text, i))
                {
                    sb.Append("<guid>");
                    i += 35;
                    lastWasHash = false;
                    lastWasSpace = false;
                    continue;
                }

                if (c >= '0' && c <= '9')
                {
                    if (!lastWasHash) sb.Append('#');
                    lastWasHash = true;
                    lastWasSpace = false;
                    continue;
                }

                lastWasHash = false;
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                lastWasSpace = false;
                sb.Append(c);
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static bool IsWordChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
        }

        private static bool IsGuidAt(string text, int start)
        {
            if (text.Length - start < 36) return false;
            for (var i = 0; i < 36; i++)
            {
                var c = text[start + i];
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (c != '-') return false;
                }
                else if (!IsHex(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
