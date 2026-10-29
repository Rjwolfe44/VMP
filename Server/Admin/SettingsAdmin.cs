using Server.Settings;
using Server.Settings.Base;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Server.Admin
{
    public sealed class SettingsInfo
    {
        public List<SettingsSectionInfo> Sections { get; set; } = new List<SettingsSectionInfo>();
    }

    public sealed class SettingsSectionInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public List<SettingInfo> Settings { get; set; } = new List<SettingInfo>();
    }

    public sealed class SettingInfo
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        /// <summary>Null for a secret.</summary>
        public object Value { get; set; }
        public object Default { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public int? MaxLength { get; set; }
        public string[] Options { get; set; }
        public bool RestartRequired { get; set; }
        public bool Secret { get; set; }
        /// <summary>Only for a secret: whether it holds a value.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsSet { get; set; }
        public bool Modified { get; set; }
    }

    public sealed class SettingsProblem
    {
        public string Key { get; set; }
        public string Message { get; set; }
    }

    /// <summary>The settings part of the admin API: what the handler needs, so tests can swap it.</summary>
    public interface ISettingsAdmin
    {
        AdminApiResponse GetSettings();
        /// <param name="changes">The "changes" object of the request: setting key to new value.</param>
        AdminApiResponse PutSettings(JsonElement changes);
    }

    /// <summary>
    /// Describes every setting (from the definition classes and their XmlComment texts) and applies validated changes to the live settings objects.
    /// A change request is checked completely first and applied all or not at all; then the file is saved once. The adminApi section can not be
    /// changed here: a wrong value there could lock the operator out of the API.
    /// </summary>
    public sealed class SettingsAdmin : ISettingsAdmin
    {
        public const string ReadOnlySection = "adminApi";
        public const int MaxLoggedValueChars = 80;

        private sealed class Entry
        {
            public ISettings Section;
            public SettingMeta Meta;
        }

        private readonly object _gate = new object();
        private readonly IReadOnlyList<ISettings> _sections;
        private readonly Func<bool> _save;
        private readonly Action<string> _log;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <param name="sections">The settings sections; the order is the order of the schema.</param>
        /// <param name="save">Writes the settings file; returns false when it can not be saved.</param>
        /// <param name="log">Gets one line per changed setting.</param>
        public SettingsAdmin(IEnumerable<ISettings> sections, Func<bool> save, Action<string> log)
        {
            _sections = SchemaOrder(sections);
            _save = save;
            _log = log ?? (_ => { });
            foreach (var section in _sections)
            {
                foreach (var meta in SettingsCatalog.Describe(section.DefinitionType))
                {
                    var entry = new Entry { Section = section, Meta = meta };
                    _entries.Add(entry);
                    _byKey[meta.Key] = entry;
                }
            }
        }

        /// <summary>Every setting that exists, in schema order (for tests that check nothing is missing).</summary>
        public int SettingCount => _entries.Count;

        private static List<ISettings> SchemaOrder(IEnumerable<ISettings> sections)
        {
            var order = new[] { "general", "connection", "gameplay", "interval", "warp", "craft", "screenshot", "log", "debug", "website", "masterServer", "dedicatedServer", "adminApi" };
            int Rank(ISettings s)
            {
                var index = Array.IndexOf(order, s.SectionId);
                return index >= 0 ? index : order.Length;
            }

            return sections.OrderBy(Rank).ThenBy(s => s.SectionId, StringComparer.Ordinal).ToList();
        }

        public AdminApiResponse GetSettings()
        {
            lock (_gate)
            {
                var result = new SettingsInfo();
                foreach (var section in _sections)
                {
                    var defaults = section.CreateDefaults();
                    var info = new SettingsSectionInfo();
                    foreach (var entry in _entries.Where(e => e.Section == section))
                    {
                        var meta = entry.Meta;
                        var value = meta.Property.GetValue(section.Store);
                        var defaultValue = meta.Property.GetValue(defaults);
                        info.Id = meta.SectionId;
                        info.Title = meta.SectionTitle;
                        info.Settings.Add(new SettingInfo
                        {
                            Key = meta.Key,
                            Label = meta.Label,
                            Description = meta.Description,
                            Type = meta.Type,
                            Value = meta.Secret ? null : SettingsCatalog.ToApiValue(value),
                            Default = meta.Secret ? null : SettingsCatalog.ToApiValue(defaultValue),
                            Min = meta.Min,
                            Max = meta.Max,
                            MaxLength = meta.MaxLength,
                            Options = meta.Options,
                            RestartRequired = meta.RestartRequired,
                            Secret = meta.Secret,
                            IsSet = meta.Secret ? (bool?)!string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture)) : null,
                            Modified = !Equals(value, defaultValue),
                        });
                    }

                    if (info.Id == null)
                    {
                        info.Id = section.SectionId;
                        info.Title = SettingsCatalog.SplitWords(char.ToUpperInvariant(section.SectionId[0]) + section.SectionId.Substring(1));
                    }

                    result.Sections.Add(info);
                }

                return AdminApiResponse.Ok(result);
            }
        }

        public AdminApiResponse PutSettings(JsonElement changes)
        {
            if (changes.ValueKind != JsonValueKind.Object) return AdminApiResponse.Error(400, "changes must be an object");

            lock (_gate)
            {
                var requested = changes.EnumerateObject().ToList();
                if (requested.Count == 0) return AdminApiResponse.Error(400, "changes must contain at least one setting");

                if (requested.Any(p => p.Name.StartsWith(ReadOnlySection + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    return AdminApiResponse.Error(403, "the adminApi settings can not be changed through the API; edit Config/vmp-server.json and restart the server");
                }

                var problems = new List<SettingsProblem>();
                var plan = new List<KeyValuePair<Entry, object>>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in requested)
                {
                    if (!_byKey.TryGetValue(property.Name, out var entry))
                    {
                        problems.Add(new SettingsProblem { Key = property.Name, Message = "unknown setting" });
                        continue;
                    }

                    if (!seen.Add(entry.Meta.Key))
                    {
                        problems.Add(new SettingsProblem { Key = entry.Meta.Key, Message = "listed more than once" });
                        continue;
                    }

                    if (!TryConvert(entry.Meta, property.Value, out var value, out var message))
                    {
                        problems.Add(new SettingsProblem { Key = entry.Meta.Key, Message = message });
                        continue;
                    }

                    plan.Add(new KeyValuePair<Entry, object>(entry, value));
                }

                if (problems.Count > 0) return Rejected(problems);

                var changed = new List<KeyValuePair<Entry, object>>();
                try
                {
                    foreach (var item in plan)
                    {
                        var old = item.Key.Meta.Property.GetValue(item.Key.Section.Store);
                        if (Equals(old, item.Value)) continue;
                        changed.Add(new KeyValuePair<Entry, object>(item.Key, old));
                        item.Key.Meta.Property.SetValue(item.Key.Section.Store, item.Value);
                    }

                    if (!_save())
                    {
                        Revert(changed);
                        return AdminApiResponse.Error(409, "the settings could not be saved because the settings file was not readable at start, so nothing was changed");
                    }
                }
                catch (Exception e)
                {
                    Revert(changed);
                    _log("[Settings] a change through the admin API was rolled back, the settings file could not be written: " + e.Message);
                    return AdminApiResponse.Error(500, "the settings could not be saved, nothing was changed");
                }

                foreach (var item in changed)
                {
                    var meta = item.Key.Meta;
                    var now = meta.Property.GetValue(item.Key.Section.Store);
                    _log($"[Settings] {meta.Key}: {Display(meta, item.Value)} -> {Display(meta, now)} (changed through the admin API)");
                }

                var keys = plan.Select(p => p.Key.Meta.Key).ToList();
                return AdminApiResponse.Ok(new
                {
                    applied = keys,
                    restartRequired = plan.Where(p => p.Key.Meta.RestartRequired).Select(p => p.Key.Meta.Key).ToList(),
                    saved = true,
                });
            }
        }

        private static void Revert(List<KeyValuePair<Entry, object>> changed)
        {
            foreach (var item in changed) item.Key.Meta.Property.SetValue(item.Key.Section.Store, item.Value);
        }

        private static AdminApiResponse Rejected(List<SettingsProblem> problems)
        {
            var summary = "settings rejected, nothing was changed: " + string.Join("; ", problems.Select(p => p.Key + " (" + p.Message + ")"));
            return new AdminApiResponse
            {
                StatusCode = 400,
                Json = JsonSerializer.Serialize(new { error = summary, errors = problems }, AdminApiJson.Options),
            };
        }

        private static string Display(SettingMeta meta, object value)
        {
            if (meta.Secret) return "***";
            var text = AdminLogTail.Clean(Convert.ToString(SettingsCatalog.ToApiValue(value), CultureInfo.InvariantCulture) ?? string.Empty);
            if (text.Length > MaxLoggedValueChars) text = text.Substring(0, MaxLoggedValueChars) + "...";
            return meta.Type == "string" ? "'" + text + "'" : text;
        }

        private static bool TryConvert(SettingMeta meta, JsonElement json, out object value, out string message)
        {
            value = null;
            message = null;
            var clr = meta.Property.PropertyType;
            switch (meta.Type)
            {
                case "bool":
                    if (json.ValueKind != JsonValueKind.True && json.ValueKind != JsonValueKind.False)
                    {
                        message = "must be true or false";
                        return false;
                    }

                    value = json.GetBoolean();
                    return true;

                case "int":
                {
                    if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt64(out var whole))
                    {
                        message = "must be a whole number";
                        return false;
                    }

                    if (meta.Min.HasValue && whole < meta.Min.Value)
                    {
                        message = "must be at least " + meta.Min.Value.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    var max = meta.Max ?? (clr == typeof(int) ? int.MaxValue : clr == typeof(long) ? long.MaxValue : clr == typeof(short) ? short.MaxValue : byte.MaxValue);
                    if (whole > max)
                    {
                        message = "must be at most " + max.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    value = Convert.ChangeType(whole, clr, CultureInfo.InvariantCulture);
                    return true;
                }

                case "double":
                {
                    if (json.ValueKind != JsonValueKind.Number || !json.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
                    {
                        message = "must be a number";
                        return false;
                    }

                    if (meta.Min.HasValue && number < meta.Min.Value)
                    {
                        message = "must be at least " + meta.Min.Value.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    if (meta.Max.HasValue && number > meta.Max.Value)
                    {
                        message = "must be at most " + meta.Max.Value.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }

                    if (clr == typeof(float))
                    {
                        var single = (float)number;
                        if (float.IsInfinity(single))
                        {
                            message = "is too large";
                            return false;
                        }

                        value = single;
                    }
                    else
                    {
                        value = number;
                    }

                    return true;
                }

                case "string":
                {
                    if (json.ValueKind != JsonValueKind.String)
                    {
                        message = "must be a string";
                        return false;
                    }

                    var text = json.GetString() ?? string.Empty;
                    if (meta.MaxLength.HasValue && text.Length > meta.MaxLength.Value)
                    {
                        message = "is too long: at most " + meta.MaxLength.Value.ToString(CultureInfo.InvariantCulture) + " characters (got " + text.Length.ToString(CultureInfo.InvariantCulture) + ")";
                        return false;
                    }

                    value = text;
                    return true;
                }

                case "enum":
                {
                    var name = json.ValueKind == JsonValueKind.String ? json.GetString() : null;
                    var match = meta.Options.FirstOrDefault(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        message = "must be one of: " + string.Join(", ", meta.Options);
                        return false;
                    }

                    value = Enum.Parse(clr, match);
                    return true;
                }

                default:
                    message = "unsupported setting type";
                    return false;
            }
        }
    }
}
