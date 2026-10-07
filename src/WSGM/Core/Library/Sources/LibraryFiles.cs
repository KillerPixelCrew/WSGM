using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>The file, path and JSON reads the Game Library's sources share.</summary>
/// <remarks>
///     Byte reads distinguish missing data from failed reads so an unread source cannot offer
///     its imported games for removal.
/// </remarks>
internal static class LibraryFiles
{
    /// <summary>The largest file a source reads whole.</summary>
    internal const long DefaultMaximumBytes = 16 * 1024 * 1024;

    /// <summary>Reads a file's text.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its text, or null when it cannot be read.</returns>
    internal static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return null;
        }
    }

    /// <summary>Reads a file's bytes, sharing it with a launcher that may be writing it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="maximumBytes">The largest file accepted.</param>
    /// <returns>Its bytes, or null when the file or directory is missing.</returns>
    /// <exception cref="IOException">The file could not be read or exceeds the accepted bound.</exception>
    internal static byte[]? ReadBytes(string path, long maximumBytes = DefaultMaximumBytes)
    {
        try
        {
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes)
            {
                throw new IOException($"Launcher file '{path}' exceeds {maximumBytes} bytes.");
            }

            using MemoryStream copy = new();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (copy.Length + read > maximumBytes)
                {
                    throw new IOException($"Launcher file '{path}' exceeds {maximumBytes} bytes.");
                }

                copy.Write(buffer, 0, read);
            }

            return copy.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            throw new IOException($"Launcher file '{path}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>Lists the files in a folder that match a pattern.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="pattern">The search pattern, such as <c>*.item</c>.</param>
    /// <returns>Their full paths, empty when the folder cannot be read.</returns>
    internal static IReadOnlyList<string> Files(string path, string pattern)
    {
        try
        {
            return Directory.GetFiles(path, pattern);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return [];
        }
    }

    /// <summary>Lists a folder's subfolders.</summary>
    /// <param name="path">The folder.</param>
    /// <returns>Their full paths, empty when the folder cannot be read.</returns>
    internal static IReadOnlyList<string> Directories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return [];
        }
    }

    /// <summary>Lists a folder's files and subfolders, with the attributes needed to skip some.</summary>
    /// <param name="path">The folder.</param>
    /// <returns>Its entries, empty when the folder cannot be read.</returns>
    internal static IReadOnlyList<FolderEntry> List(string path)
    {
        try
        {
            EnumerationOptions options = new()
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false
            };
            return new DirectoryInfo(path)
                .EnumerateFileSystemInfos("*", options)
                .Select(info => new FolderEntry(info.FullName, info is DirectoryInfo, info.Attributes,
                    info is FileInfo file ? file.Length : 0))
                .ToList();
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return [];
        }
    }

    /// <summary>A path in Windows' own form: backslashes throughout.</summary>
    /// <param name="path">A path a launcher wrote, possibly with forward slashes.</param>
    /// <returns>The path with every separator a backslash.</returns>
    internal static string WindowsPath(string path)
    {
        return path.Replace('/', '\\');
    }

    /// <summary>An install folder as a route names it: Windows separators, no trailing one.</summary>
    /// <param name="path">The folder a launcher recorded.</param>
    /// <returns>
    ///     The folder without a trailing separator, except a drive root, which keeps its own: <c>D:</c>
    ///     alone names the current folder on that drive, not its root.
    /// </returns>
    internal static string InstallFolder(string path)
    {
        return Path.TrimEndingDirectorySeparator(WindowsPath(path.Trim()));
    }

    /// <summary>A path a launcher records relative to an install folder, made absolute.</summary>
    /// <param name="root">The install folder.</param>
    /// <param name="relative">The relative path, with either separator and possibly a leading one.</param>
    /// <returns>The full path.</returns>
    internal static string Under(string root, string relative)
    {
        return Path.Combine(root, WindowsPath(relative).TrimStart('\\'));
    }

    /// <summary>Reads one string property of a JSON object.</summary>
    /// <param name="element">The object, or any other element.</param>
    /// <param name="name">The property.</param>
    /// <param name="ignoreCase">Whether the property name is matched without regard to case.</param>
    /// <returns>The string, or empty when the element is not an object or the property is not a string.</returns>
    internal static string JsonText(JsonElement element, string name, bool ignoreCase = false)
    {
        return JsonProperty(element, name, ignoreCase) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>Reads one property of a JSON object.</summary>
    /// <param name="element">The object, or any other element.</param>
    /// <param name="name">The property.</param>
    /// <param name="ignoreCase">Whether the property name is matched without regard to case.</param>
    /// <returns>The property's value, or null when the element is not an object or has no such property.</returns>
    internal static JsonElement? JsonProperty(JsonElement element, string name, bool ignoreCase = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!ignoreCase)
        {
            return element.TryGetProperty(name, out var exact) ? exact : null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>Reads one array-of-strings property of a JSON object.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>Its string items, empty when it is missing or not an array.</returns>
    internal static IReadOnlyList<string> JsonStrings(JsonElement element, string name)
    {
        if (JsonProperty(element, name) is not { ValueKind: JsonValueKind.Array } value)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }

    /// <summary>Reads one value from an INI-style file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="section">The section, compared without regard to case.</param>
    /// <param name="key">The key, compared without regard to case.</param>
    /// <returns>The first matching value as written, or null.</returns>
    internal static string? IniValue(string text, string section, string key)
    {
        return IniFile.ReadTextValue(text, key, section);
    }

    /// <summary>Reads one value from a file Qt or MultiMC's INI writer produced.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="key">The key.</param>
    /// <returns>The first value outside any section or in <c>[General]</c>, unquoted and unescaped, or null.</returns>
    /// <remarks>
    ///     Older MultiMC-family files have no section at all; Qt's writer puts the same keys under
    ///     <c>[General]</c>. Both escape backslashes, and Qt quotes a value with special characters.
    /// </remarks>
    internal static string? QtIniValue(string text, string key)
    {
        var raw = IniFile.Values(text, key)
            .Where(pair => pair.Section.Length == 0
                           || pair.Section.Equals("General", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        return raw is null ? null : Unescape(raw);
    }

    /// <summary>Whether an exception means only that a file or folder cannot be read.</summary>
    private static bool IsUnreadable(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException
            or NotSupportedException;
    }

    private static string Unescape(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            value = value[1..^1];
        }

        StringBuilder result = new(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                result.Append(value[i]);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n':
                    result.Append('\n');
                    break;
                case 't':
                    result.Append('\t');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 'x':
                    // Qt 5 wrote non-ASCII characters as \x and up to four hex digits.
                    var digits = 0;
                    while (digits < 4 && i + 1 + digits < value.Length && Uri.IsHexDigit(value[i + 1 + digits]))
                    {
                        digits++;
                    }

                    if (digits == 0)
                    {
                        result.Append('x');
                        break;
                    }

                    result.Append((char)int.Parse(
                        value.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += digits;
                    break;
                default:
                    result.Append(next);
                    break;
            }
        }

        return result.ToString();
    }
}
