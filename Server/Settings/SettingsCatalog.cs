using LmpCommon.Xml;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Server.Settings
{
    /// <summary>What the server knows about one setting (one public property of a *SettingsDefinition class).</summary>
    public sealed class SettingMeta
    {
        public string SectionId;
        public string SectionTitle;
        public string Name;
        /// <summary>"section.Property", e.g. general.ServerName.</summary>
        public string Key;
        public string Label;
        public string Description;
        /// <summary>bool, int, double, string or enum.</summary>
        public string Type;
        public PropertyInfo Property;
        public double? Min;
        public double? Max;
        public int? MaxLength;
        public string[] Options;
        public bool RestartRequired;
        public bool Secret;
    }

    /// <summary>Reflection helpers for the settings definitions: section ids, labels, ranges, the live-setting list and the deep compare used by the import check.</summary>
    public static class SettingsCatalog
    {
        private static readonly Regex WordBoundary = new Regex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant);
        private static readonly Regex SecretName = new Regex("Password|Passwd|Token|Secret|ApiKey", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex MaxCharsBefore = new Regex(@"\bmax(?:imum)?\s+(?<n>\d+)\s+char", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex MaxCharsAfter = new Regex(@"\b(?<n>\d+)\s+chars?\s+max", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex MaxValue = new Regex(@"\bmax value is\s+(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex MinValue = new Regex(@"\bmin value is\s+(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Sections whose settings are read each time they are used, so a change needs no restart. Everything else needs one.</summary>
        private static readonly string[] LiveSections = { "screenshot", "craft", "gameplay", "compression" };

        /// <summary>
        /// Single settings that are read when they are used. general.MaxPlayers is not here: the connection limit of the network library is
        /// copied from it at start. general.ModControl, ports, addresses and the like are used at start, too.
        /// </summary>
        private static readonly string[] LiveKeys =
        {
            "general.ServerName", "general.ServerMotd", "general.Password", "general.AdminPassword", "general.Cheats", "general.AllowSackKerbals",
            // The backup timer and retention numbers are read again each time a backup or a prune runs.
            "interval.ArchiveBackupIntervalMinutes", "interval.BackupKeepNewest", "interval.BackupKeepDailyDays", "interval.BackupIncludeScreenshots",
            "interval.BackupKeepScreenshotArchives", "interval.BackupMaxTotalMegabytes", "interval.BackupLegacyZipKeep",
        };

        public static string SectionIdOf(Type definition)
        {
            var name = definition.Name;
            if (name.EndsWith("SettingsDefinition", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "SettingsDefinition".Length);
            return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>"ServerName" becomes "Server Name".</summary>
        public static string SplitWords(string pascalCase) => WordBoundary.Replace(pascalCase, " ");

        public static bool IsSecretName(string name) => !string.IsNullOrEmpty(name) && SecretName.IsMatch(name);

        public static bool IsLive(string sectionId, string name)
        {
            return LiveSections.Contains(sectionId, StringComparer.OrdinalIgnoreCase)
                   || LiveKeys.Contains(sectionId + "." + name, StringComparer.OrdinalIgnoreCase);
        }

        public static PropertyInfo[] PropertiesOf(Type definition)
        {
            return definition.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken)
                .ToArray();
        }

        public static List<SettingMeta> Describe(Type definition)
        {
            var sectionId = SectionIdOf(definition);
            var sectionTitle = SplitWords(char.ToUpperInvariant(sectionId[0]) + sectionId.Substring(1));
            var result = new List<SettingMeta>();
            foreach (var property in PropertiesOf(definition))
            {
                var description = property.GetCustomAttribute<XmlCommentAttribute>()?.Value ?? string.Empty;
                var clr = property.PropertyType;
                var meta = new SettingMeta
                {
                    SectionId = sectionId,
                    SectionTitle = sectionTitle,
                    Name = property.Name,
                    Key = sectionId + "." + property.Name,
                    Label = SplitWords(property.Name),
                    Description = description,
                    Property = property,
                    Secret = IsSecretName(property.Name),
                    RestartRequired = !IsLive(sectionId, property.Name),
                };

                if (clr == typeof(bool))
                {
                    meta.Type = "bool";
                }
                else if (clr == typeof(int) || clr == typeof(byte) || clr == typeof(short) || clr == typeof(long))
                {
                    meta.Type = "int";
                    meta.Min = 0;
                    if (clr == typeof(byte)) meta.Max = byte.MaxValue;
                    if (property.Name.EndsWith("Port", StringComparison.Ordinal))
                    {
                        meta.Min = 1;
                        meta.Max = 65535;
                    }

                    var maxValue = MaxValue.Match(description);
                    if (maxValue.Success) meta.Max = double.Parse(maxValue.Groups["n"].Value, CultureInfo.InvariantCulture);
                    var minValue = MinValue.Match(description);
                    if (minValue.Success) meta.Min = double.Parse(minValue.Groups["n"].Value, CultureInfo.InvariantCulture);
                }
                else if (clr == typeof(float) || clr == typeof(double))
                {
                    meta.Type = "double";
                    meta.Min = 0;
                }
                else if (clr == typeof(string))
                {
                    meta.Type = "string";
                    var length = MaxCharsBefore.Match(description);
                    if (!length.Success) length = MaxCharsAfter.Match(description);
                    if (length.Success) meta.MaxLength = int.Parse(length.Groups["n"].Value, CultureInfo.InvariantCulture);
                }
                else if (clr.IsEnum)
                {
                    meta.Type = "enum";
                    meta.Options = Enum.GetNames(clr);
                }
                else
                {
                    throw new NotSupportedException($"Setting {meta.Key} has the type {clr.Name}, which the settings schema does not support");
                }

                result.Add(meta);
            }

            return result;
        }

        /// <summary>The value as it goes into JSON: enums by name, floats as the shortest decimal that reads back as the same float.</summary>
        public static object ToApiValue(object value)
        {
            switch (value)
            {
                case null: return null;
                case float f: return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                case byte b: return (int)b;
                case short s: return (int)s;
                case Enum e: return e.ToString();
                default: return value;
            }
        }

        /// <summary>
        /// Property-by-property comparison of two settings objects (reflection, recursive into objects and lists). Used to prove that what was read
        /// back from vmp-server.json is what was read from the XML files. The text of a difference never contains the value of a credential.
        /// </summary>
        public static bool DeepEquals(object expected, object actual, out string difference, string path = "")
        {
            difference = null;
            if (ReferenceEquals(expected, actual)) return true;
            if (expected == null || actual == null)
            {
                difference = path + ": one side is null";
                return false;
            }

            var type = expected.GetType();
            if (type != actual.GetType())
            {
                difference = path + ": " + type.Name + " vs " + actual.GetType().Name;
                return false;
            }

            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) || type == typeof(TimeSpan))
            {
                if (expected.Equals(actual)) return true;
                var leaf = path.Substring(path.LastIndexOf('.') + 1);
                difference = IsSecretName(leaf) ? path + ": values differ" : path + ": " + Show(expected) + " vs " + Show(actual);
                return false;
            }

            if (expected is IEnumerable left && actual is IEnumerable right)
            {
                var a = left.Cast<object>().ToList();
                var b = right.Cast<object>().ToList();
                if (a.Count != b.Count)
                {
                    difference = path + ": " + a.Count + " items vs " + b.Count;
                    return false;
                }

                for (var i = 0; i < a.Count; i++)
                {
                    if (!DeepEquals(a[i], b[i], out difference, path + "[" + i + "]")) return false;
                }

                return true;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                var next = string.IsNullOrEmpty(path) ? property.Name : path + "." + property.Name;
                if (!DeepEquals(property.GetValue(expected), property.GetValue(actual), out difference, next)) return false;
            }

            return true;
        }

        private static string Show(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return text.Length > 60 ? "'" + text.Substring(0, 60) + "...'" : "'" + text + "'";
        }
    }
}
