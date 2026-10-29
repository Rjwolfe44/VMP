using LmpCommon.Xml;
using Server.Context;
using Server.Log;
using Server.Settings.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Server.Settings
{
    /// <summary>Where the settings of a start came from.</summary>
    public enum SettingsSource
    {
        /// <summary>Not loaded yet.</summary>
        None,
        /// <summary>Config/vmp-server.json was read.</summary>
        Json,
        /// <summary>The old XML files were read, copied into vmp-server.json, verified and moved to a legacy-xml folder.</summary>
        ImportedFromXml,
        /// <summary>The old XML files are in use but the import was aborted (a file could not be read or the check failed). Nothing was written.</summary>
        ImportAborted,
        /// <summary>No settings existed; the defaults were used and written.</summary>
        Defaults,
        /// <summary>vmp-server.json could not be read (whole or in part). The defaults are in use for what failed and the file is never overwritten.</summary>
        JsonUnreadable,
    }

    /// <summary>
    /// The one settings file of the server, Config/vmp-server.json, holding every section (see <see cref="ISettings.SectionId"/>) as JSON.
    /// Start: (a) the file exists, so it is read; (b) else, if any of the old per-section XML files exists, they are read exactly as before, written
    /// to the new file, verified by reading the new file back and comparing every property of every section, and only then moved (never deleted)
    /// to Config/legacy-xml-yyyyMMdd-HHmmss; (c) else the defaults are used and written.
    /// Rollback: stop the server, delete vmp-server.json and copy the XML files from the legacy folder back into Config.
    /// Save writes the whole document through a temp file and keeps one vmp-server.json.bak of the version it replaced.
    /// </summary>
    public sealed class SettingsFile
    {
        public const string FileName = "vmp-server.json";
        public const string BackupFileName = "vmp-server.json.bak";
        public const string TempFileName = "vmp-server.json.tmp";
        public const string ImportCheckFileName = "vmp-server.json.import-check";
        public const string LegacyFolderPrefix = "legacy-xml-";

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static SettingsFile _current;
        private readonly object _gate = new object();
        private readonly string _configDirectory;
        private readonly List<ISettings> _sections;

        public SettingsFile(string configDirectory, IEnumerable<ISettings> sections)
        {
            _configDirectory = configDirectory;
            // General first and Gameplay last: the default of Gameplay depends on the difficulty in General.
            _sections = sections
                .OrderBy(s => s.SectionId == "general" ? 0 : s.SectionId == "gameplay" ? 2 : 1)
                .ThenBy(s => s.SectionId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The file of the running server (Config folder of the server).</summary>
        public static SettingsFile Current
        {
            get => _current ?? (_current = Create(ServerContext.ConfigDirectory));
            set => _current = value;
        }

        public static SettingsFile Create(string configDirectory) => new SettingsFile(configDirectory, DiscoverSections());

        /// <summary>One instance of every settings class of the server.</summary>
        public static List<ISettings> DiscoverSections()
        {
            return Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => typeof(ISettings).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(t => (ISettings)Activator.CreateInstance(t))
                .ToList();
        }

        public IReadOnlyList<ISettings> Sections => _sections;
        public string ConfigDirectory => _configDirectory;
        public string FilePath => Path.Combine(_configDirectory, FileName);
        public string BackupPath => Path.Combine(_configDirectory, BackupFileName);

        public SettingsSource Source { get; private set; }

        /// <summary>True when Save must not write because the file in Config could not be understood (it would be overwritten with defaults).</summary>
        public bool SavesBlocked { get; private set; }

        /// <summary>The legacy-xml-* folder created by the import (null when no import happened).</summary>
        public string LegacyFolder { get; private set; }

        public int ImportedFileCount { get; private set; }

        public Action<string> LogInfo { get; set; } = LunaLog.Normal;
        public Action<string> LogWarning { get; set; } = LunaLog.Warning;
        public Action<string> LogError { get; set; } = LunaLog.Error;
        public Func<DateTime> Now { get; set; } = () => DateTime.Now;

        /// <summary>Test hook: called with the path of the freshly written import copy before it is read back for verification.</summary>
        public Action<string> AfterImportWrite { get; set; }

        /// <summary>Loads every section once (later calls do nothing) and applies the values to the SettingsStore of each section.</summary>
        public SettingsSource Load()
        {
            lock (_gate)
            {
                if (Source != SettingsSource.None) return Source;

                Directory.CreateDirectory(_configDirectory);
                if (File.Exists(FilePath)) LoadFromJson();
                else if (_sections.Any(s => File.Exists(LegacyPath(s)))) ImportLegacyXml();
                else LoadDefaults();
                return Source;
            }
        }

        /// <summary>Writes the whole document. Returns false (and writes nothing) when saving is blocked; throws when the disk write fails.</summary>
        public bool Save()
        {
            lock (_gate)
            {
                if (Source == SettingsSource.None) Load();
                if (SavesBlocked)
                {
                    LogWarning($"Settings were not saved: {FileName} could not be read at start, so it is left untouched. Fix or remove it and restart the server.");
                    return false;
                }

                WriteIfChanged(Serialize());
                return true;
            }
        }

        /// <summary>The document as JSON text: one object per section, in section order.</summary>
        public string Serialize()
        {
            var root = new JsonObject();
            foreach (var section in _sections)
            {
                root[section.SectionId] = JsonSerializer.SerializeToNode(section.Store, section.DefinitionType, JsonOptions);
            }

            return root.ToJsonString(JsonOptions);
        }

        private string LegacyPath(ISettings section) => Path.Combine(_configDirectory, section.LegacyFilename);

        private void LoadFromJson()
        {
            JsonObject root;
            try
            {
                root = ParseRoot(File.ReadAllText(FilePath, Utf8NoBom));
            }
            catch (Exception e)
            {
                LogError($"Error while trying to read {FilePath}: {e.Message}. Default settings will be used and the file will not be changed. Fix or remove the file and restart the server.");
                foreach (var section in _sections) section.Store = section.CreateDefaults();
                Source = SettingsSource.JsonUnreadable;
                SavesBlocked = true;
                return;
            }

            var failed = false;
            foreach (var section in _sections)
            {
                try
                {
                    section.Store = ReadSection(root, section, false);
                }
                catch (Exception e)
                {
                    failed = true;
                    LogError($"Error while trying to read the \"{section.SectionId}\" section of {FilePath}: {e.Message}. Default settings are used for it and the file will not be changed. Fix the section (or remove it) and restart the server.");
                    section.Store = section.CreateDefaults();
                }
            }

            Source = failed ? SettingsSource.JsonUnreadable : SettingsSource.Json;
            SavesBlocked = failed;
            if (!failed) SaveAtStart(); // adds settings that are new in this version, with their defaults
        }

        private void LoadDefaults()
        {
            foreach (var section in _sections) section.Store = section.CreateDefaults();
            Source = SettingsSource.Defaults;
            if (SaveAtStart()) LogInfo($"No settings found, created Config/{FileName} with the default values.");
        }

        private bool SaveAtStart()
        {
            try
            {
                WriteIfChanged(Serialize());
                return true;
            }
            catch (Exception e)
            {
                LogError($"Could not write {FilePath}: {e.Message}. The settings stay in memory only.");
                return false;
            }
        }

        private void ImportLegacyXml()
        {
            var failures = new List<string>();
            foreach (var section in _sections)
            {
                object value = null;
                var path = LegacyPath(section);
                if (!File.Exists(path))
                {
                    value = section.CreateDefaults(); // what the old code did for a missing file: create it with the defaults
                }
                else
                {
                    try
                    {
                        value = LunaXmlSerializer.ReadXmlFromPath(section.DefinitionType, path);
                        if (value == null) failures.Add(section.LegacyFilename + ": empty result");
                    }
                    catch (Exception e)
                    {
                        failures.Add(section.LegacyFilename + ": " + e.Message);
                    }
                }

                section.Store = value ?? section.CreateDefaults();
            }

            if (failures.Count > 0)
            {
                AbortImport("these files could not be read: " + string.Join("; ", failures));
                return;
            }

            var check = Path.Combine(_configDirectory, ImportCheckFileName);
            string problem = null;
            try
            {
                WriteBytes(check, Utf8NoBom.GetBytes(Serialize()));
                AfterImportWrite?.Invoke(check);
                var reloaded = ParseRoot(File.ReadAllText(check, Utf8NoBom));
                foreach (var section in _sections)
                {
                    var copy = ReadSection(reloaded, section, true);
                    if (!SettingsCatalog.DeepEquals(section.Store, copy, out var difference, section.SectionId))
                    {
                        problem = "the copy in " + FileName + " differs from the XML value at " + difference;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                problem = "the copy could not be verified: " + e.Message;
            }

            if (problem != null)
            {
                TryDelete(check);
                AbortImport(problem);
                return;
            }

            try
            {
                File.Move(check, FilePath);
            }
            catch (Exception e)
            {
                TryDelete(check);
                AbortImport("could not create " + FileName + ": " + e.Message);
                return;
            }

            Source = SettingsSource.ImportedFromXml;
            MoveLegacyFiles();
        }

        private void MoveLegacyFiles()
        {
            var existing = _sections.Where(s => File.Exists(LegacyPath(s))).ToList();
            var folder = Path.Combine(_configDirectory, LegacyFolderPrefix + Now().ToString("yyyyMMdd-HHmmss"));
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(_configDirectory, LegacyFolderPrefix + Now().ToString("yyyyMMdd-HHmmss") + "-" + n);

            var moved = 0;
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var section in existing)
                {
                    File.Move(LegacyPath(section), Path.Combine(folder, section.LegacyFilename));
                    moved++;
                }
            }
            catch (Exception e)
            {
                LogWarning($"Settings were imported into Config/{FileName}, but not all old XML files could be moved to {Path.GetFileName(folder)}: {e.Message}. They are no longer used.");
            }

            LegacyFolder = folder;
            ImportedFileCount = moved;
            LogInfo($"Imported {moved} settings file(s) from Config into Config/{FileName} (every value was read back and checked). The old XML files were moved, not deleted, to "
                    + $"Config/{Path.GetFileName(folder)}. To roll back: stop the server, delete Config/{FileName} and copy the files from that folder back into Config.");
        }

        private void AbortImport(string reason)
        {
            Source = SettingsSource.ImportAborted;
            SavesBlocked = true;
            LogError($"Importing the old settings XML files into {FileName} was aborted: {reason}. Nothing was written or moved; the server keeps running on the values of the XML files. "
                     + "Settings cannot be saved until this is solved.");
        }

        private static JsonObject ParseRoot(string text)
        {
            var node = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node as JsonObject ?? throw new InvalidDataException("the file does not hold a JSON object");
        }

        /// <summary>A section that is missing gets its defaults (so settings new in a version appear), unless strict is set.</summary>
        private static object ReadSection(JsonObject root, ISettings section, bool strict)
        {
            if (!root.TryGetPropertyValue(section.SectionId, out var node) || node == null)
            {
                if (strict) throw new InvalidDataException("section \"" + section.SectionId + "\" is missing");
                return section.CreateDefaults();
            }

            if (!(node is JsonObject)) throw new InvalidDataException("section \"" + section.SectionId + "\" is not an object");
            return JsonSerializer.Deserialize(node, section.DefinitionType, JsonOptions) ?? throw new InvalidDataException("section \"" + section.SectionId + "\" is empty");
        }

        private void WriteIfChanged(string text)
        {
            var bytes = Utf8NoBom.GetBytes(text);
            Directory.CreateDirectory(_configDirectory);
            if (File.Exists(FilePath) && File.ReadAllBytes(FilePath).AsSpan().SequenceEqual(bytes)) return;

            var temp = Path.Combine(_configDirectory, TempFileName);
            try
            {
                WriteBytes(temp, bytes);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath, true);
                else File.Move(temp, FilePath);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void WriteBytes(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
