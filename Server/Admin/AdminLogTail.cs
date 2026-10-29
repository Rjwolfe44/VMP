using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Server.Admin
{
    /// <summary>Reading the end of a log file that the server is still appending to, and making text safe to show.</summary>
    public static class AdminLogTail
    {
        public const int DefaultMaxBytes = 512 * 1024;

        /// <summary>Control characters (C0, DEL, C1, tab included) become spaces so a log line can never carry terminal escapes or fake extra lines.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            StringBuilder builder = null;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var control = c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F);
                if (control && builder == null)
                {
                    builder = new StringBuilder(text.Length);
                    builder.Append(text, 0, i);
                }

                if (builder != null) builder.Append(control ? ' ' : c);
            }

            return builder == null ? text : builder.ToString();
        }

        /// <summary>
        /// The last <paramref name="maxLines"/> lines of the file, read without blocking the writer (FileShare.ReadWrite). Only the last
        /// <paramref name="maxBytes"/> bytes are looked at. A missing file gives an empty list.
        /// </summary>
        public static List<string> ReadTail(string path, int maxLines, int maxBytes = DefaultMaxBytes)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(path) || maxLines < 1 || !File.Exists(path)) return lines;

            byte[] buffer;
            var partialStart = false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var length = stream.Length;
                var start = Math.Max(0, length - maxBytes);
                partialStart = start > 0;
                stream.Seek(start, SeekOrigin.Begin);
                buffer = new byte[(int)(length - start)];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }

                if (read < buffer.Length) Array.Resize(ref buffer, read);
            }

            var text = Encoding.UTF8.GetString(buffer);
            var all = text.Split('\n');
            var first = partialStart ? 1 : 0;
            var last = all.Length - 1;
            if (last >= first && all[last].Length == 0) last--;

            for (var i = Math.Max(first, last - maxLines + 1); i <= last; i++) lines.Add(Clean(all[i].TrimEnd('\r')));
            return lines;
        }
    }
}
