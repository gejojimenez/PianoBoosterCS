// Reads the settings file of PianoBooster (C++): the .ini file of QSettings::IniFormat, so that
// the settings of the C++ program are kept the first time the port runs.
// The format follows QSettingsPrivate::iniUnescapedKey(), iniUnescapedStringList() and
// stringToVariant() of Qt. Part of the .NET port of PianoBooster. GPLv3.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace PianoBooster.Data;

public static class QtIniReader
{
    /// <summary>The settings of a QSettings .ini file, with the keys as QSettings gives them
    /// ("Group/Key"; those of [General] without a group). Values Qt keeps in binary (@Variant,
    /// @ByteArray) are left out.</summary>
    public static List<(string Key, JsonNode? Value)> Read(string fileName)
    {
        var result = new List<(string, JsonNode?)>();
        string section = "";
        string[] lines = File.ReadAllLines(fileName, Encoding.UTF8);
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            // a backslash at the end of the line continues the value on the next line
            while (line.EndsWith('\\') && !line.EndsWith("\\\\", StringComparison.Ordinal) && n + 1 < lines.Length)
                line = line[..^1] + lines[++n];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#')
                continue;
            if (trimmed[0] == '[')
            {
                int end = trimmed.IndexOf(']');
                string name = UnescapeKey(trimmed[1..(end < 0 ? trimmed.Length : end)]);
                section = name.Equals("General", StringComparison.OrdinalIgnoreCase) ? "" : name;
                continue;
            }
            int equals = trimmed.IndexOf('=');
            if (equals <= 0)
                continue;
            string key = UnescapeKey(trimmed[..equals].Trim());
            if (section.Length > 0)
                key = section + "/" + key;
            if (TryConvert(UnescapeStringList(trimmed[(equals + 1)..]), out JsonNode? value))
                result.Add((key, value));
        }
        return result;
    }

    /// <summary>%XX and %UXXXX escapes; a backslash is a '/' (a subgroup).</summary>
    private static string UnescapeKey(string key)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (c == '\\')
                sb.Append('/');
            else if (c == '%' && i + 5 < key.Length && key[i + 1] == 'U' &&
                     int.TryParse(key.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u))
            {
                sb.Append((char)u);
                i += 5;
            }
            else if (c == '%' && i + 2 < key.Length &&
                     int.TryParse(key.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x))
            {
                sb.Append((char)x);
                i += 2;
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A value: texts separated by commas (a list if there are more than one), each one
    /// maybe in quotes, with C escapes.</summary>
    private static List<string> UnescapeStringList(string text)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        int i = 0;
        // leading spaces are not part of the value
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        int trailingSpaces = 0; // spaces outside quotes at the end of the current item
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                trailingSpaces = 0;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                char e = text[++i];
                switch (e)
                {
                    case 'a': current.Append('\a'); break;
                    case 'b': current.Append('\b'); break;
                    case 'f': current.Append('\f'); break;
                    case 'n': current.Append('\n'); break;
                    case 'r': current.Append('\r'); break;
                    case 't': current.Append('\t'); break;
                    case 'v': current.Append('\v'); break;
                    case 'x':
                    {
                        int start = i + 1, end = start;
                        while (end < text.Length && Uri.IsHexDigit(text[end]))
                            end++;
                        if (end > start)
                            current.Append((char)int.Parse(text.AsSpan(start, end - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i = end - 1;
                        break;
                    }
                    case >= '0' and <= '7':
                    {
                        int start = i, end = i;
                        while (end < text.Length && end - start < 3 && text[end] >= '0' && text[end] <= '7')
                            end++;
                        current.Append((char)Convert.ToInt32(text[start..end], 8));
                        i = end - 1;
                        break;
                    }
                    default: current.Append(e); break; // \" \' \\ \?
                }
                trailingSpaces = 0;
            }
            else if (c == ',' && !inQuotes)
            {
                current.Length -= trailingSpaces;
                items.Add(current.ToString());
                current.Clear();
                trailingSpaces = 0;
                while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                    i++;
            }
            else if (c == ';' && !inQuotes)
                break; // a comment
            else
            {
                current.Append(c);
                trailingSpaces = !inQuotes && char.IsWhiteSpace(c) ? trailingSpaces + 1 : 0;
            }
        }
        current.Length -= trailingSpaces;
        items.Add(current.ToString());
        return items;
    }

    private static bool TryConvert(List<string> items, out JsonNode? value)
    {
        value = null;
        if (items.Count > 1)
        {
            var array = new JsonArray();
            foreach (string item in items)
                array.Add(JsonValue.Create(item.StartsWith("@@", StringComparison.Ordinal) ? item[1..] : item));
            value = array;
            return true;
        }
        string text = items[0];
        if (!text.StartsWith('@'))
        {
            value = JsonValue.Create(text);
            return true;
        }
        if (text.StartsWith("@@", StringComparison.Ordinal))
        {
            value = JsonValue.Create(text[1..]);
            return true;
        }
        if (text == "@Invalid()")
        {
            value = new JsonArray(); // an empty QStringList is written as an invalid QVariant
            return true;
        }
        int[]? numbers = Numbers(text, "@Rect(");
        if (numbers is { Length: 4 })
        {
            value = new JsonObject { ["x"] = numbers[0], ["y"] = numbers[1], ["width"] = numbers[2], ["height"] = numbers[3] };
            return true;
        }
        numbers = Numbers(text, "@Size(");
        if (numbers is { Length: 2 })
        {
            value = new JsonObject { ["width"] = numbers[0], ["height"] = numbers[1] };
            return true;
        }
        numbers = Numbers(text, "@Point(");
        if (numbers is { Length: 2 })
        {
            value = new JsonObject { ["x"] = numbers[0], ["y"] = numbers[1] };
            return true;
        }
        return false; // @ByteArray, @Variant...: binary data of Qt
    }

    private static int[]? Numbers(string text, string prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(')'))
            return null;
        string[] parts = text[prefix.Length..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        return numbers;
    }
}
