// The settings file of the port: what QSettings (IniFormat, UserScope, "PianoBooster",
// "Piano Booster") does for CSettings in PianoBooster (C++), kept in a JSON file.
// Part of the .NET port of PianoBooster. GPLv3.
//
// The keys are the same as in the C++ ("Group/Key", e.g. "Midi/Output"), in one flat JSON object.
// The values keep their type (text, number, true/false, list of texts, rectangle) and are
// converted when they are read, as QVariant does (toString(), toInt(), toBool(), toStringList()).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PianoBooster.Data;

/// <summary>A rectangle (QRect), for the geometry of the window.</summary>
public readonly record struct SettingsRect(int X, int Y, int Width, int Height)
{
    /// <summary>QRect::isValid(): a positive width and height.</summary>
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>The settings in a JSON file (the C++ QSettings).</summary>
public sealed class SettingsStore
{
    public const string FileBaseName = "Piano Booster";

    private readonly SortedDictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>The settings of the user: <c>%APPDATA%\PianoBooster\Piano Booster.json</c> on Windows,
    /// <c>~/.config/PianoBooster/Piano Booster.json</c> elsewhere, the folder where QSettings keeps
    /// <c>Piano Booster.ini</c>. If there is no JSON file yet, the settings of the C++ program are imported.</summary>
    public static SettingsStore OpenUserSettings()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PianoBooster");
        return Open(Path.Combine(folder, FileBaseName + ".json"));
    }

    /// <summary>Opens a settings file (it is created when something is saved). If it does not
    /// exist, the QSettings file with the same name and ".ini" is imported, if there is one.</summary>
    public static SettingsStore Open(string fileName)
    {
        var store = new SettingsStore(fileName);
        if (File.Exists(fileName))
            store.Load();
        else
        {
            string ini = Path.ChangeExtension(fileName, ".ini");
            if (File.Exists(ini))
            {
                foreach ((string key, JsonNode? value) in QtIniReader.Read(ini))
                    store._values[key] = value;
                store.ImportedFrom = ini;
            }
        }
        return store;
    }

    public SettingsStore(string? fileName)
    {
        FileName = fileName;
    }

    /// <summary>The file (null: kept only in memory).</summary>
    public string? FileName { get; }
    /// <summary>The C++ settings file imported when the JSON file did not exist yet.</summary>
    public string? ImportedFrom { get; private set; }
    /// <summary>Saves after each change (QSettings saves a moment later). Off: call <see cref="Save"/>.</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>The folder of the file (where the C++ keeps library.db).</summary>
    public string? Folder => FileName == null ? null : Path.GetDirectoryName(Path.GetFullPath(FileName));

    public bool Contains(string key)
    {
        lock (_lock)
            return _values.ContainsKey(key);
    }

    public IReadOnlyList<string> AllKeys()
    {
        lock (_lock)
            return _values.Keys.ToList();
    }

    public void Remove(string key)
    {
        lock (_lock)
            if (!_values.Remove(key))
                return;
        Changed();
    }

    public void SetValue(string key, string value) => Set(key, JsonValue.Create(value));
    public void SetValue(string key, int value) => Set(key, JsonValue.Create(value));
    public void SetValue(string key, double value) => Set(key, JsonValue.Create(value));
    public void SetValue(string key, bool value) => Set(key, JsonValue.Create(value));

    public void SetValue(string key, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(JsonValue.Create(value));
        Set(key, array);
    }

    public void SetValue(string key, SettingsRect rect) =>
        Set(key, new JsonObject { ["x"] = rect.X, ["y"] = rect.Y, ["width"] = rect.Width, ["height"] = rect.Height });

    private void Set(string key, JsonNode? value)
    {
        lock (_lock)
        {
            if (_values.TryGetValue(key, out JsonNode? old) && JsonNode.DeepEquals(old, value))
                return;
            _values[key] = value;
        }
        Changed();
    }

    /// <summary>QVariant::toString(): a list gives its items separated by '|' (as MidiDeviceBase.GetStringList
    /// expects), true/false give "true"/"false", a missing value gives the default.</summary>
    public string GetString(string key, string defaultValue = "")
    {
        JsonNode? node = Get(key);
        if (node == null)
            return defaultValue;
        return node switch
        {
            JsonArray array => string.Join("|", array.Select(ItemText)),
            JsonObject => "",
            _ => ItemText(node),
        };
    }

    /// <summary>QVariant::toInt(): a number or a text with a number (a decimal is not an int); otherwise 0.
    /// A missing value gives the default.</summary>
    public int GetInt(string key, int defaultValue = 0)
    {
        JsonNode? node = Get(key);
        if (node == null)
            return defaultValue;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool b))
                return b ? 1 : 0;
            if (value.TryGetValue(out int i))
                return i;
            if (value.TryGetValue(out double d))
                return d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : 0;
            if (value.TryGetValue(out string? s) &&
                int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
        }
        return 0;
    }

    /// <summary>QVariant::toDouble().</summary>
    public double GetDouble(string key, double defaultValue = 0.0)
    {
        JsonNode? node = Get(key);
        if (node == null)
            return defaultValue;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out double d))
                return d;
            if (value.TryGetValue(out string? s) &&
                double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return parsed;
        }
        return 0.0;
    }

    /// <summary>QVariant::toBool(): a text is false if it is empty, "0" or "false" (in any case).</summary>
    public bool GetBool(string key, bool defaultValue = false)
    {
        JsonNode? node = Get(key);
        if (node == null)
            return defaultValue;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool b))
                return b;
            if (value.TryGetValue(out double d))
                return d != 0;
            if (value.TryGetValue(out string? s))
                return !(s.Length == 0 || s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    /// <summary>QVariant::toStringList(): a single text is a list of one item.</summary>
    public List<string> GetStringList(string key)
    {
        JsonNode? node = Get(key);
        return node switch
        {
            null => new List<string>(),
            JsonArray array => array.Select(ItemText).ToList(),
            JsonObject => new List<string>(),
            _ => new List<string> { ItemText(node) },
        };
    }

    /// <summary>QVariant::toRect(): an invalid (empty) rectangle if it is not one.</summary>
    public SettingsRect GetRect(string key)
    {
        if (Get(key) is JsonObject o && TryInt(o["x"], out int x) && TryInt(o["y"], out int y) &&
            TryInt(o["width"], out int w) && TryInt(o["height"], out int h))
            return new SettingsRect(x, y, w, h);
        return default;
    }

    /// <summary>QVariant::toPoint() (x, y), or the default.</summary>
    public (int X, int Y) GetPoint(string key, (int X, int Y) defaultValue)
    {
        if (Get(key) is JsonObject o && TryInt(o["x"], out int x) && TryInt(o["y"], out int y))
            return (x, y);
        return defaultValue;
    }

    /// <summary>QVariant::toSize() (width, height), or the default.</summary>
    public (int Width, int Height) GetSize(string key, (int Width, int Height) defaultValue)
    {
        if (Get(key) is JsonObject o && TryInt(o["width"], out int w) && TryInt(o["height"], out int h))
            return (w, h);
        return defaultValue;
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    private JsonNode? Get(string key)
    {
        lock (_lock)
            return _values.TryGetValue(key, out JsonNode? node) ? node : null;
    }

    private static string ItemText(JsonNode? node)
    {
        if (node is not JsonValue value)
            return "";
        if (value.TryGetValue(out string? s))
            return s;
        if (value.TryGetValue(out bool b))
            return b ? "true" : "false";
        if (value.TryGetValue(out int i))
            return i.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue(out double d))
            return d.ToString(CultureInfo.InvariantCulture);
        return value.ToJsonString();
    }

    private void Changed()
    {
        if (AutoSave)
            Save();
    }

    private void Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FileName!, Encoding.UTF8)) is JsonObject root)
                foreach ((string key, JsonNode? value) in root)
                    _values[key] = value?.DeepClone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = e.Message; // like QSettings: start with no settings
        }
    }

    /// <summary>Why the file could not be read, or null.</summary>
    public string? LoadError { get; private set; }

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes the file (to a temporary file first, so that it is never left half written).</summary>
    public void Save()
    {
        if (FileName == null)
            return;
        string text;
        lock (_lock)
        {
            var root = new JsonObject();
            foreach ((string key, JsonNode? value) in _values)
                root[key] = value?.DeepClone();
            text = root.ToJsonString(s_writeOptions);
        }
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(FileName))!;
            Directory.CreateDirectory(folder);
            string temp = FileName + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, FileName, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveError = e.Message;
        }
    }

    /// <summary>Why the file could not be written the last time, or null.</summary>
    public string? SaveError { get; private set; }
}
