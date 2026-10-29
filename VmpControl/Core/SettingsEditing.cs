using System.Globalization;
using System.Text.Json;

namespace VmpControl.Core;

// Client-side shapes of GET /api/v1/settings and PUT /api/v1/settings (see docs/admin-api.md). Unknown and missing fields are tolerated.

public sealed class SettingsSchemaDto
{
    private List<SettingsSectionDto> _sections = new();
    public List<SettingsSectionDto> Sections { get => _sections; set => _sections = value ?? new(); }
}

public sealed class SettingsSectionDto
{
    private List<SettingDto> _settings = new();
    public string Id { get; set; }
    public string Title { get; set; }
    public List<SettingDto> Settings { get => _settings; set => _settings = value ?? new(); }
}

public sealed class SettingDto
{
    private List<string> _options = new();
    public string Key { get; set; }
    public string Label { get; set; }
    public string Description { get; set; }
    /// <summary>bool, int, double, string or enum.</summary>
    public string Type { get; set; }
    /// <summary>The JSON value. For a secret it is always null.</summary>
    public JsonElement Value { get; set; }
    public JsonElement Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public int? MaxLength { get; set; }
    public List<string> Options { get => _options; set => _options = value ?? new(); }
    public bool RestartRequired { get; set; }
    public bool Secret { get; set; }
    /// <summary>Only for a secret: whether it holds a value.</summary>
    public bool? IsSet { get; set; }
    public bool Modified { get; set; }

    public string SectionId => Key != null && Key.IndexOf('.') is var dot and > 0 ? Key.Substring(0, dot) : "";

    public string TypeName => (Type ?? "").ToLowerInvariant();

    /// <summary>The setting's current value as a .NET value (bool, long, double or string); null for a secret or a missing value.</summary>
    public object ServerValue() => Secret ? null : ToObject(Value);

    public object DefaultValue() => Secret ? null : ToObject(Default);

    private object ToObject(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number:
                if (TypeName == "int" && e.TryGetInt64(out var whole)) return whole;
                return e.TryGetDouble(out var number) ? (TypeName == "int" && number == Math.Floor(number) ? (object)(long)number : number) : null;
            default: return null;
        }
    }
}

public sealed class SettingsSaveResultDto
{
    private List<string> _applied = new(), _restart = new();
    public List<string> Applied { get => _applied; set => _applied = value ?? new(); }
    public List<string> RestartRequired { get => _restart; set => _restart = value ?? new(); }
    public bool Saved { get; set; }
}

public sealed class SettingProblemDto
{
    public string Key { get; set; }
    public string Message { get; set; }
}

public sealed class SettingsRejectedDto
{
    private List<SettingProblemDto> _errors = new();
    public string Error { get; set; }
    public List<SettingProblemDto> Errors { get => _errors; set => _errors = value ?? new(); }
}

/// <summary>
/// The pending edits of the Settings page, with no UI in it: the schema from the server, a dirty set that holds only the values that differ from the
/// server, client-side validation (the same limits the server enforces), the PUT body with only the changed keys, and the search / "modified only" filter.
/// Secrets are never shown: the schema has no value for them, a new value is held only until it is sent, and <see cref="Describe"/> never prints it.
/// </summary>
public sealed class SettingsEditModel
{
    public const string AdminApiPrefix = "adminApi.";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, object> _edits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private Dictionary<string, SettingDto> _byKey = new(StringComparer.Ordinal);

    public SettingsSchemaDto Schema { get; private set; } = new();
    public event Action Changed;

    /// <summary>Replaces the schema (after a refresh or a save). Edits for settings that still exist are kept unless they now equal the server's value.</summary>
    public void Load(SettingsSchemaDto schema)
    {
        Schema = schema ?? new SettingsSchemaDto();
        _byKey = new Dictionary<string, SettingDto>(StringComparer.Ordinal);
        foreach (var section in Schema.Sections)
        {
            foreach (var s in section.Settings)
            {
                if (!string.IsNullOrEmpty(s.Key)) _byKey[s.Key] = s;
            }
        }

        foreach (var key in _edits.Keys.ToList())
        {
            if (!_byKey.TryGetValue(key, out var s) || IsReadOnly(s) || (!s.Secret && ValuesEqual(_edits[key], s.ServerValue()))) _edits.Remove(key);
        }

        foreach (var key in _errors.Keys.ToList())
        {
            if (!_edits.ContainsKey(key)) _errors.Remove(key);
        }

        Changed?.Invoke();
    }

    public SettingDto Find(string key) => key != null && _byKey.TryGetValue(key, out var s) ? s : null;

    public IEnumerable<SettingDto> All() => Schema.Sections.SelectMany(s => s.Settings);

    /// <summary>The adminApi section is shown but never edited here: a wrong value there could lock the operator out of the API.</summary>
    public static bool IsReadOnly(SettingDto s) => s.Key != null && s.Key.StartsWith(AdminApiPrefix, StringComparison.OrdinalIgnoreCase);

    // ----- the dirty set -----

    public bool IsDirty(string key) => _edits.ContainsKey(key);
    public int DirtyCount => _edits.Count;
    public IEnumerable<string> DirtyKeys => All().Where(s => _edits.ContainsKey(s.Key)).Select(s => s.Key);

    /// <summary>The value to show: the pending edit if there is one, otherwise the server's value (null for a secret).</summary>
    public object Effective(SettingDto s) => _edits.TryGetValue(s.Key, out var v) ? v : s.ServerValue();

    /// <summary>Records a new value for a setting. A value equal to the server's removes the edit again. Returns false when the setting is read-only or unknown.</summary>
    public bool Set(string key, object value)
    {
        var s = Find(key);
        if (s == null || IsReadOnly(s)) return false;
        if (s.Secret)
        {
            // A secret changes only through an explicit non-empty new value (or ClearSecret); an empty entry means "no change".
            if (value is not string text || text.Length == 0) return false;
            _edits[key] = text;
        }
        else
        {
            var normal = Normalize(s, value);
            if (ValuesEqual(normal, s.ServerValue())) _edits.Remove(key);
            else _edits[key] = normal;
        }

        _errors.Remove(key);
        Changed?.Invoke();
        return true;
    }

    /// <summary>The explicit way to remove a secret: it sends an empty string for it.</summary>
    public bool ClearSecret(string key)
    {
        var s = Find(key);
        if (s == null || !s.Secret || IsReadOnly(s)) return false;
        _edits[key] = "";
        _errors.Remove(key);
        Changed?.Invoke();
        return true;
    }

    public void Revert(string key)
    {
        if (_edits.Remove(key) | _errors.Remove(key)) Changed?.Invoke();
    }

    public void DiscardAll()
    {
        if (_edits.Count == 0 && _errors.Count == 0) return;
        _edits.Clear();
        _errors.Clear();
        Changed?.Invoke();
    }

    /// <summary>Sets the edit to the server's default value (not saved until Save). A secret is cleared instead.</summary>
    public bool ResetToDefault(string key)
    {
        var s = Find(key);
        if (s == null || IsReadOnly(s)) return false;
        return s.Secret ? ClearSecret(key) : Set(key, s.DefaultValue());
    }

    /// <summary>True when the value that would be in effect (pending edit or server value) differs from the default. For a secret: it has a value.</summary>
    public bool IsModified(SettingDto s)
    {
        if (s.Secret)
        {
            if (_edits.TryGetValue(s.Key, out var v)) return v is string { Length: > 0 };
            return s.IsSet == true;
        }

        return !ValuesEqual(Effective(s), s.DefaultValue());
    }

    // ----- what is shown for a secret -----

    /// <summary>"set", "not set", "new value entered" or "will be cleared". Never the value.</summary>
    public string SecretState(SettingDto s)
    {
        if (_edits.TryGetValue(s.Key, out var v)) return v is string { Length: > 0 } ? "new value entered" : "will be cleared";
        return s.IsSet == true ? "set" : "not set";
    }

    /// <summary>A short text for a log line or a tooltip. Secrets are described by their state, never by their value.</summary>
    public string Describe(SettingDto s)
    {
        return s.Secret ? SecretState(s) : FormatValue(Effective(s));
    }

    public static string FormatValue(object v) => v switch
    {
        null => "(none)",
        bool b => b ? "on" : "off",
        double d => d.ToString("0.####", Inv),
        string { Length: 0 } => "(empty)",
        _ => Convert.ToString(v, Inv),
    };

    // ----- validation -----

    /// <summary>Null when the value is acceptable, otherwise the message to show next to the row (worded like the server's).</summary>
    public static string Validate(SettingDto s, object value)
    {
        switch (s.TypeName)
        {
            case "bool":
                return value is bool ? null : "must be true or false";
            case "int":
                if (value is not long whole) return value is double or decimal or float ? "must be a whole number" : "must be a number";
                return CheckRange(s, whole);
            case "double":
                return value is double or long ? CheckRange(s, Convert.ToDouble(value, Inv)) : "must be a number";
            case "enum":
                return value is string e && s.Options.Any(o => string.Equals(o, e, StringComparison.OrdinalIgnoreCase)) ? null : "must be one of: " + string.Join(", ", s.Options);
            case "string":
                if (value is not string text) return "must be text";
                return s.MaxLength is int max && text.Length > max ? $"must be at most {max} characters" : null;
            default:
                return null;
        }
    }

    private static string CheckRange(SettingDto s, double number)
    {
        if (s.Min is double min && number < min) return "must be at least " + min.ToString("0.####", Inv);
        if (s.Max is double max && number > max) return "must be at most " + max.ToString("0.####", Inv);
        return null;
    }

    /// <summary>Checks every pending edit and records the problems (shown next to the rows). Returns the number of problems.</summary>
    public int ValidateAll()
    {
        _errors.Clear();
        foreach (var key in DirtyKeys)
        {
            var s = Find(key);
            var problem = Validate(s, _edits[key]);
            if (problem != null) _errors[key] = problem;
        }

        Changed?.Invoke();
        return _errors.Count;
    }

    public string ErrorFor(string key) => _errors.TryGetValue(key, out var m) ? m : null;
    public int ErrorCount => _errors.Count;
    public IReadOnlyDictionary<string, string> Errors => _errors;

    /// <summary>Takes the per-setting problems of a rejected save (the "errors" list of the 400 response). Returns how many were recorded.</summary>
    public int ApplyServerErrors(string responseBody)
    {
        var rejected = ApiJson.TryParse<SettingsRejectedDto>(responseBody);
        if (rejected == null) return 0;
        _errors.Clear();
        foreach (var e in rejected.Errors)
        {
            if (!string.IsNullOrEmpty(e.Key)) _errors[e.Key] = e.Message ?? "rejected";
        }

        Changed?.Invoke();
        return _errors.Count;
    }

    // ----- saving -----

    /// <summary>Only the changed settings, in schema order.</summary>
    public IReadOnlyDictionary<string, object> BuildChanges()
    {
        var changes = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in DirtyKeys) changes[key] = _edits[key];
        return changes;
    }

    /// <summary>The PUT body: <c>{"changes":{...}}</c> with only the changed keys.</summary>
    public string BuildPutBody() => JsonSerializer.Serialize(new { changes = BuildChanges() }, ApiJson.Options);

    /// <summary>One line for the activity log after a save. It names the keys only, never the values (some of them are secrets).</summary>
    public static string SaveSummary(SettingsSaveResultDto result)
    {
        var n = result.Applied.Count;
        var text = $"saved {n} setting{(n == 1 ? "" : "s")}: {string.Join(", ", result.Applied)}";
        return result.RestartRequired.Count > 0 ? text + $" ({result.RestartRequired.Count} apply after a restart)" : text;
    }

    // ----- search and filter -----

    /// <summary>Settings of one section (or of all when <paramref name="sectionId"/> is null) whose label, key or description contain every word of <paramref name="search"/>. "Modified only" keeps those that differ from their default and those with a pending edit.</summary>
    public IEnumerable<SettingDto> Filter(string search, bool modifiedOnly, string sectionId)
    {
        var terms = (search ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var section in Schema.Sections)
        {
            if (sectionId != null && !string.Equals(section.Id, sectionId, StringComparison.Ordinal)) continue;
            foreach (var s in section.Settings)
            {
                if (modifiedOnly && !IsModified(s) && !IsDirty(s.Key)) continue;
                if (terms.Length > 0)
                {
                    var haystack = $"{s.Label} {s.Key} {s.Description}";
                    if (!terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                }

                yield return s;
            }
        }
    }

    public int ModifiedCount(string sectionId) => Schema.Sections.Where(x => x.Id == sectionId).SelectMany(x => x.Settings).Count(IsModified);
    public int DirtyCountIn(string sectionId) => _edits.Keys.Count(k => Find(k)?.SectionId == sectionId);
    public int ErrorCountIn(string sectionId) => _errors.Keys.Count(k => (Find(k)?.SectionId ?? k.Split('.')[0]) == sectionId);

    // ----- value helpers -----

    /// <summary>Gives a value the .NET type the setting expects (int to long, enum name to the server's spelling) so it can be compared, validated and sent.</summary>
    public static object Normalize(SettingDto s, object value)
    {
        switch (s.TypeName)
        {
            case "int":
                if (value is int or long or short or byte) return Convert.ToInt64(value, Inv);
                if (value is double or float or decimal)
                {
                    var d = Convert.ToDouble(value, Inv);
                    return d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (object)(long)d : d; // a fractional value stays a double so that validation rejects it
                }

                return value;
            case "double":
                return value is int or long or short or byte or float or decimal ? Convert.ToDouble(value, Inv) : value;
            case "enum":
                return value is string e ? s.Options.FirstOrDefault(o => string.Equals(o, e, StringComparison.OrdinalIgnoreCase)) ?? e : value;
            default:
                return value;
        }
    }

    private static bool ValuesEqual(object a, object b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is long or double && b is long or double) return Convert.ToDouble(a, Inv) == Convert.ToDouble(b, Inv);
        return false;
    }
}

/// <summary>"N changes apply after a restart": filled from the restartRequired list of a successful save, cleared when the server has been restarted.</summary>
public sealed class RestartBanner
{
    private readonly List<string> _keys = new();
    private DateTime? _startedUtc;

    public IReadOnlyList<string> Keys => _keys;
    public bool Visible => _keys.Count > 0;

    public string Text => _keys.Count switch
    {
        0 => "",
        1 => "1 change applies after a restart",
        _ => $"{_keys.Count} changes apply after a restart",
    };

    /// <summary>Records the keys a save said need a restart. A save that lists none leaves an existing banner alone.</summary>
    public void OnSaved(IEnumerable<string> restartRequiredKeys, DateTime? serverStartedUtc)
    {
        if (_keys.Count > 0 && Restarted(serverStartedUtc)) Clear();
        var added = false;
        foreach (var key in restartRequiredKeys ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrEmpty(key) && !_keys.Contains(key, StringComparer.Ordinal))
            {
                _keys.Add(key);
                added = true;
            }
        }

        if (added) _startedUtc ??= serverStartedUtc;
        if (_keys.Count == 0) _startedUtc = null;
    }

    /// <summary>Call with the server's start time from each status poll: a different start time means the server restarted and the changes are in effect.</summary>
    public void OnStatus(DateTime? serverStartedUtc)
    {
        if (_keys.Count > 0 && Restarted(serverStartedUtc)) Clear();
    }

    public void Clear()
    {
        _keys.Clear();
        _startedUtc = null;
    }

    // The server derives its start time from "now - uptime", so it jitters by a few milliseconds between polls.
    private bool Restarted(DateTime? now) => _startedUtc != null && now != null && Math.Abs((now.Value - _startedUtc.Value).TotalSeconds) > 5;
}
