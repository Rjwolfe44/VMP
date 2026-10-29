using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Server.Admin
{
    /// <summary>
    /// The bearer token of the admin API: 32 random bytes as 64 hex characters in Config/admin-token.txt, created only when the file is
    /// missing. It is never logged and never part of a backup. An existing file that is too short is an error (the API stays off) rather
    /// than something that gets silently replaced or accepted.
    /// </summary>
    public static class AdminApiToken
    {
        public const string FileName = "admin-token.txt";
        public const int MinLength = 32;

        public static string PathIn(string configDirectory)
        {
            return Path.Combine(configDirectory, FileName);
        }

        public static string LoadOrCreate(string configDirectory, out bool created)
        {
            created = false;
            Directory.CreateDirectory(configDirectory);
            var path = PathIn(configDirectory);

            if (!File.Exists(path))
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = Encoding.ASCII.GetBytes(token);
                        stream.Write(bytes, 0, bytes.Length);
                    }

                    created = true;
                    RestrictToOwner(path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Someone else created it a moment ago; use theirs.
                }
            }

            var existing = File.ReadAllText(path).Trim();
            if (existing.Length < MinLength)
            {
                throw new InvalidDataException(FileName + " must hold a token of at least " + MinLength + " characters; delete the file to get a new one");
            }

            return existing;
        }

        private static void RestrictToOwner(string path)
        {
            try
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // Best effort; on Windows the file inherits the permissions of the server folder.
            }
        }
    }
}
