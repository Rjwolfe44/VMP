using System;
using System.IO;
using System.IO.Compression;

namespace LmpUpdater
{
    public static class SafeZipExtractor
    {
        public static void Extract(string archivePath, string destination)
        {
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                // Validate the whole archive before writing any entry. Treat both separators
                // as paths even when running on Unix, where backslash is otherwise a filename.
                foreach (var entry in archive.Entries)
                    GetTarget(root, entry.FullName);

                foreach (var entry in archive.Entries)
                {
                    var target = GetTarget(root, entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                        input.CopyTo(output);
                }
            }
        }

        private static string GetTarget(string root, string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf(':') >= 0 || name.StartsWith("/", StringComparison.Ordinal) ||
                name.StartsWith("\\", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid update archive path: " + name);

            var normalized = name.Replace('\\', '/');
            foreach (var component in normalized.Split('/'))
                if (component == "..")
                    throw new InvalidDataException("Update archive contains parent traversal: " + name);

            var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update archive escapes its extraction folder: " + name);
            return target;
        }
    }
}
