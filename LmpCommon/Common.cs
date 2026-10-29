using LmpCommon.Compression;
using LmpCommon.Stats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LmpCommon
{
    public class Common
    {
        /// <summary>
        /// Compresses a message payload in place with the message codec (zstd at a level chosen by the compression policy; QuickLZ where this
        /// process cannot compress with zstd; unchanged when the payload is too small or does not shrink by at least 8 bytes). A payload that
        /// already is compressed is left alone, so a message that is serialized once per recipient is compressed only once.
        /// </summary>
        /// <param name="statsName">Message type name under which the raw and compressed sizes are counted in <see cref="VmpTrafficStats"/>; null counts nothing.</param>
        public static void ThreadSafeCompress(object lockObj, ref byte[] data, ref int numBytes, string statsName = null)
        {
            lock (lockObj)
            {
                MessageCodec.Compress(ref data, ref numBytes, statsName);
            }
        }

        /// <summary>
        /// Restores a payload that <see cref="ThreadSafeCompress"/> (or an older QuickLZ-only build) produced. A zstd frame, a QuickLZ payload and a
        /// raw payload are told apart by their first bytes. Throws <see cref="InvalidOperationException"/> for a corrupt frame or one that
        /// declares more than <see cref="CompressionLimits.MaxDecodedBytes"/>.
        /// </summary>
        /// <param name="statsName">See <see cref="ThreadSafeCompress"/>; counted only when the payload really was compressed.</param>
        public static void ThreadSafeDecompress(object lockObj, ref byte[] data, int length, out int numBytes, string statsName = null)
        {
            lock (lockObj)
            {
                MessageCodec.Decompress(ref data, length, out numBytes, statsName);
            }
        }

        public static T[] TrimArray<T>(T[] array, int size)
        {
            var newArray = new T[size];
            Array.Copy(array, newArray, size);
            return newArray;
        }

        public static bool PlatformIsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        /// <summary>
        /// Compare two ienumerables and return if they are the same or not IGNORING the order
        /// </summary>
        public static bool ScrambledEquals<T>(IEnumerable<T> list1, IEnumerable<T> list2)
        {
            var list1Enu = list1 as T[] ?? list1.ToArray();
            var list2Enu = list2 as T[] ?? list2.ToArray();
            if (list1Enu.Length != list2Enu.Length)
            {
                return false;
            }

            var cnt = new Dictionary<T, int>();
            foreach (var s in list1Enu)
            {
                if (cnt.ContainsKey(s))
                {
                    cnt[s]++;
                }
                else
                {
                    cnt.Add(s, 1);
                }
            }
            foreach (var s in list2Enu)
            {
                if (cnt.ContainsKey(s))
                {
                    cnt[s]--;
                }
                else
                {
                    return false;
                }
            }
            return cnt.Values.All(c => c == 0);
        }

        public string CalculateSha256StringHash(string input)
        {
            return CalculateSha256Hash(Encoding.UTF8.GetBytes(input));
        }

        public static string CalculateSha256FileHash(string fileName)
        {
            return CalculateSha256Hash(File.ReadAllBytes(fileName));
        }

        public static string CalculateSha256Hash(byte[] data)
        {
            using (var provider = new SHA256Managed())
            {
                var hashedBytes = provider.ComputeHash(data);
                return BitConverter.ToString(hashedBytes);
            }
        }

        public static string ConvertConfigStringToGuidString(string configNodeString)
        {
            if (configNodeString == null || configNodeString.Length != 32)
                return null;
            var returnString = new string[5];
            returnString[0] = configNodeString.Substring(0, 8);
            returnString[1] = configNodeString.Substring(8, 4);
            returnString[2] = configNodeString.Substring(12, 4);
            returnString[3] = configNodeString.Substring(16, 4);
            returnString[4] = configNodeString.Substring(20);
            return string.Join("-", returnString);
        }

        public static Guid ConvertConfigStringToGuid(string configNodeString)
        {
            try
            {
                return new Guid(ConvertConfigStringToGuidString(configNodeString));
            }
            catch (Exception)
            {
                return Guid.Empty;
            }
        }
    }
}