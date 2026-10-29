using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings;
using Server.Settings.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>Helpers for the settings tests. The settings classes keep their values in statics, so a test snapshots and restores them.</summary>
    internal static class SettingsTestSupport
    {
        public static string NewTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "vmp-settings-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>A file object with its log lines collected in <paramref name="log"/> (nothing goes to the real server log).</summary>
        public static SettingsFile NewFile(string dir, List<string> log, DateTime? now = null)
        {
            var file = SettingsFile.Create(dir);
            file.LogInfo = line => log.Add("INFO " + line);
            file.LogWarning = line => log.Add("WARN " + line);
            file.LogError = line => log.Add("ERROR " + line);
            file.Now = () => now ?? new DateTime(2026, 10, 28, 12, 30, 45);
            return file;
        }

        public static void ResetToDefaults(IEnumerable<ISettings> sections)
        {
            foreach (var section in sections.OrderBy(s => s.SectionId == "general" ? 0 : s.SectionId == "gameplay" ? 2 : 1)) section.Store = section.CreateDefaults();
        }

        /// <summary>Gives every property of a settings object a value that differs from the current one.</summary>
        public static object Mutate(object definition)
        {
            foreach (var property in SettingsCatalog.PropertiesOf(definition.GetType()))
            {
                var value = property.GetValue(definition);
                object changed;
                switch (value)
                {
                    case bool b: changed = !b; break;
                    case byte by: changed = (byte)((by + 11) % 256); break;
                    case int i: changed = i + 7; break;
                    case float f: changed = f + 1.25f; break;
                    case string _: changed = "v-" + property.Name + " \"q\" <&> ünï\nline2"; break;
                    case Enum e:
                        var all = Enum.GetValues(e.GetType()).Cast<object>().ToList();
                        changed = all[(all.IndexOf(e) + 1) % all.Count];
                        break;
                    default: throw new NotSupportedException(property.Name);
                }

                property.SetValue(definition, changed);
            }

            return definition;
        }

        /// <summary>Writes one XML file per section with every value changed, using the real serializer, and returns what the real reader reads back.</summary>
        public static Dictionary<string, object> WriteLegacyConfig(string dir, IReadOnlyList<ISettings> sections)
        {
            ResetToDefaults(sections);
            var expected = new Dictionary<string, object>();
            foreach (var section in sections)
            {
                var path = Path.Combine(dir, section.LegacyFilename);
                LunaXmlSerializer.WriteToXmlFile(Mutate(section.CreateDefaults()), path);
                expected[section.SectionId] = LunaXmlSerializer.ReadXmlFromPath(section.DefinitionType, path);
            }

            return expected;
        }

        public static void AssertStoresEqual(IReadOnlyList<ISettings> sections, Dictionary<string, object> expected, string what)
        {
            foreach (var section in sections)
            {
                Assert.IsTrue(SettingsCatalog.DeepEquals(expected[section.SectionId], section.Store, out var difference, section.SectionId), what + ": " + difference);
            }
        }

        /// <summary>Restores the static settings objects when disposed.</summary>
        public sealed class StoreSnapshot : IDisposable
        {
            private readonly List<ISettings> _sections = SettingsFile.DiscoverSections();
            private readonly object[] _values;

            public StoreSnapshot()
            {
                _values = _sections.Select(s => s.Store).ToArray();
            }

            public void Dispose()
            {
                for (var i = 0; i < _sections.Count; i++) _sections[i].Store = _values[i];
            }
        }
    }
}
