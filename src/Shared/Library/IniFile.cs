// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Small INI reads and idempotent edits that retain unrelated settings and comments.</summary>
public static class IniFile
{
    /// <summary>Reads the first matching key, optionally restricted to a section.</summary>
    /// <param name="path">The settings file.</param>
    /// <param name="key">The case-insensitive key.</param>
    /// <param name="section">The section, or null to accept any section.</param>
    /// <returns>The trimmed raw value, or null when the file or key cannot be read.</returns>
    public static string? ReadValue(string path, string key, string? section = null)
    {
        try
        {
            return ReadTextValue(File.ReadAllText(path), key, section);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a key from text already acquired by the caller.</summary>
    /// <param name="text">The file contents.</param>
    /// <param name="key">The case-insensitive key.</param>
    /// <param name="section">The section, or null to accept any section.</param>
    /// <returns>The first trimmed raw value or null.</returns>
    public static string? ReadTextValue(string text, string key, string? section = null)
    {
        return Values(text, key).Where(value => section is null
                                                || value.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Value).FirstOrDefault();
    }

    /// <summary>Parses the first value of each key from text in one pass.</summary>
    /// <param name="text">The already acquired file contents.</param>
    /// <returns>Case-insensitive keys and their trimmed raw values.</returns>
    public static Dictionary<string, string> ReadTextValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (equals > 0 && !line.StartsWith(';') && !line.StartsWith('#'))
            {
                values.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
        }

        return values;
    }

    /// <summary>Sets one key while preserving other lines, section order and the existing newline convention.</summary>
    /// <param name="path">The settings file.</param>
    /// <param name="key">The key to replace or add.</param>
    /// <param name="value">The raw value to write.</param>
    /// <param name="section">The section to edit, or null to edit the first existing key or add globally.</param>
    public static void SetValue(string path, string key, string value, string? section = null)
    {
        SetValues(path, new Dictionary<string, string> { [key] = value }, section);
    }

    /// <summary>Sets multiple keys in one read, line pass and atomic write, preserving unrelated settings.</summary>
    /// <param name="path">The settings file.</param>
    /// <param name="values">Keys and raw values to replace or add.</param>
    /// <param name="section">The section to edit, or null to accept the first matching key.</param>
    public static void SetValues(string path, IReadOnlyDictionary<string, string> values, string? section = null)
    {
        foreach (var (key, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            if (key.Contains('=') || key.IndexOfAny(['\r', '\n']) >= 0 || value.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("An INI key or value contains a line delimiter.");
            }
        }

        if (section?.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
        {
            throw new ArgumentException("An INI key, section or value contains a line delimiter.");
        }

        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Length == 0
            ? new List<string>()
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var trailingNewline = text.EndsWith('\n');
        if (trailingNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var currentSection = "";
        var insertAt = section is null ? 0 : -1;
        var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (section is not null && currentSection.Equals(section, StringComparison.OrdinalIgnoreCase)
                                        && insertAt < 0)
                {
                    insertAt = index;
                }

                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            if (section is not null && !currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            var equals = lines[index].IndexOf('=');
            if (equals > 0 && pending.Remove(lines[index][..equals].Trim(), out var value))
            {
                if (lines[index][(equals + 1)..].Trim() != value)
                {
                    lines[index] = lines[index][..(equals + 1)] + value;
                    changed = true;
                }
            }
        }

        if (pending.Count > 0 && section is not null && insertAt < 0)
        {
            if (!currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("[" + section + "]");
            }

            insertAt = lines.Count;
        }

        foreach (var (key, value) in pending)
        {
            lines.Insert(Math.Max(insertAt++, 0), key + "=" + value);
            changed = true;
        }

        if (changed)
        {
            Write(path, string.Join(newline, lines) + (trailingNewline ? newline : ""));
        }
    }

    internal static IEnumerable<(string Section, string Value)> Values(string text, string key)
    {
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                yield return (section, line[(equals + 1)..].Trim());
            }
        }
    }

    private static void Write(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteText(path, text, false);
    }
}
