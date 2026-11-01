using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSGM.Core;

/// <summary>Edits existing keys of a tool's configuration file without disturbing anything else.</summary>
/// <remarks>
///     <para>
///         The shared <c>IniFile</c> adds missing keys and sections and normalizes encoding, which is wrong
///         for a file another program owns and rewrites itself. This editor changes only keys that already
///         exist, in the first matching section, using the first matching key. A missing key means the
///         setting does not exist in that tool version and is reported, never created.
///     </para>
///     <para>
///         It preserves the byte-order mark and encoding, every line ending, leading whitespace and an
///         inline <c>;</c> or <c>#</c> comment, and writes nothing when no value changed. A read-only flag
///         is cleared for the write and restored afterwards.
///     </para>
/// </remarks>
internal static class GameToolIni
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Reads the current raw value of each requested key.</summary>
    /// <param name="path">The configuration file.</param>
    /// <param name="keys">Section and key pairs.</param>
    /// <returns>Values keyed by <c>section/key</c> (lowercase); a missing key has no entry.</returns>
    internal static Dictionary<string, string> Read(string path, IEnumerable<(string Section, string Key)> keys)
    {
        var (text, _, _) = Decode(File.ReadAllBytes(path));
        var lines = Split(text);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var (section, key) in keys)
        {
            var index = Find(lines, section, key);
            if (index >= 0)
            {
                values[Id(section, key)] = Parse(lines[index].Content).Value.Trim();
            }
        }

        return values;
    }

    /// <summary>Applies edits to existing keys.</summary>
    /// <param name="path">The configuration file.</param>
    /// <param name="edits">The edits.</param>
    /// <returns>The edits whose key does not exist; they were not applied.</returns>
    internal static IReadOnlyList<GameToolIniEdit> Apply(string path, IReadOnlyList<GameToolIniEdit> edits)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encoding, preamble) = Decode(bytes);
        var lines = Split(text);
        List<GameToolIniEdit> missing = [];
        var changed = false;
        foreach (var edit in edits)
        {
            if (edit.Value.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("An INI value contains a line delimiter.");
            }

            var index = Find(lines, edit.Section, edit.Key);
            if (index < 0)
            {
                missing.Add(edit);
                continue;
            }

            var parsed = Parse(lines[index].Content);
            if (parsed.Value.Trim().Equals(edit.Value, StringComparison.Ordinal))
            {
                continue;
            }

            var replacement = parsed.Prefix + edit.Value + (parsed.Comment.Length > 0 ? " " + parsed.Comment : "");
            lines[index] = lines[index] with { Content = replacement };
            changed = true;
        }

        if (!changed)
        {
            return missing;
        }

        var output = new StringBuilder(text.Length + 32);
        foreach (var line in lines)
        {
            output.Append(line.Content).Append(line.Ending);
        }

        var attributes = File.GetAttributes(path);
        var readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
        if (readOnly)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        try
        {
            AtomicFile.Write(path, stream =>
            {
                stream.Write(preamble);
                stream.Write(encoding.GetBytes(output.ToString()));
                return true;
            }, true);
        }
        finally
        {
            if (readOnly && File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }
        }

        return missing;
    }

    /// <summary>The dictionary key <see cref="Read" /> uses for a section and key.</summary>
    /// <param name="section">The section.</param>
    /// <param name="key">The key.</param>
    /// <returns>The normalized identity.</returns>
    internal static string Id(string section, string key)
    {
        return Normalize(section) + "/" + Normalize(key);
    }

    private static int Find(List<Line> lines, string section, string key)
    {
        var wantedSection = Normalize(section);
        var wantedKey = Normalize(key);
        var inSection = false;
        var seenSection = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Content.Trim();
            if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                if (seenSection)
                {
                    // Only the first occurrence of a section counts.
                    return -1;
                }

                inSection = Normalize(trimmed[1..trimmed.IndexOf(']')]) == wantedSection;
                seenSection = inSection;
                continue;
            }

            if (!inSection || trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            var equals = lines[index].Content.IndexOf('=');
            if (equals > 0 && Normalize(lines[index].Content[..equals]) == wantedKey)
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Prefix, string Value, string Comment) Parse(string content)
    {
        var equals = content.IndexOf('=');
        var prefix = content[..(equals + 1)];
        var rest = content[(equals + 1)..];
        var comment = "";
        for (var index = 0; index < rest.Length; index++)
        {
            if (rest[index] is ';' or '#' && (index == 0 || char.IsWhiteSpace(rest[index - 1])))
            {
                comment = rest[index..];
                rest = rest[..index];
                break;
            }
        }

        // Keep the whitespace that followed '=' as part of the prefix.
        var leading = rest.Length - rest.TrimStart().Length;
        return (prefix + rest[..leading], rest[leading..].TrimEnd(), comment);
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(character => !char.IsWhiteSpace(character) && character is not ('"' or '\''))
            .ToArray()).ToLowerInvariant();
    }

    private static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        foreach (var encoding in new Encoding[]
                 {
                     new UTF8Encoding(true), new UTF32Encoding(false, true), new UnicodeEncoding(false, true),
                     new UnicodeEncoding(true, true)
                 })
        {
            var preamble = encoding.GetPreamble();
            if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
            {
                return (encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding,
                    preamble);
            }
        }

        try
        {
            return (StrictUtf8.GetString(bytes), StrictUtf8, []);
        }
        catch (DecoderFallbackException)
        {
            // Latin-1 maps every byte to one character and back, so an unknown code page round-trips.
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, []);
        }
    }

    private static List<Line> Split(string text)
    {
        List<Line> lines = [];
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                var end = index > start && text[index - 1] == '\r' ? index - 1 : index;
                lines.Add(new Line(text[start..end], text[end..(index + 1)]));
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(new Line(text[start..], ""));
        }

        return lines;
    }

    private sealed record Line(string Content, string Ending);
}
