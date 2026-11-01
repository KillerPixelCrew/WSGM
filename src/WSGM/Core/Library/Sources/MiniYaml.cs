using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Reads the block-style subset of YAML the Ubisoft product cache is written in.</summary>
/// <remarks>
///     Mappings and sequences nested by indentation, plain and quoted scalars. Flow collections are
///     kept as their text and block scalars as empty, since discovery reads neither. Anything it
///     cannot place is skipped, so a malformed document yields less rather than failing. Nesting
///     deeper than <see cref="MaximumDepth" /> is skipped too: every level is a recursive call, and a
///     corrupted cache must not overflow the stack, which no handler can catch.
/// </remarks>
internal static class MiniYaml
{
    /// <summary>The deepest nesting read; the cache's own documents nest a handful of levels.</summary>
    internal const int MaximumDepth = 64;

    /// <summary>Parses a document.</summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>
    ///     A <see cref="Dictionary{TKey,TValue}" /> for a mapping, a <see cref="List{T}" /> for a sequence,
    ///     a string for a scalar, or null for an empty document.
    /// </returns>
    public static object? Parse(string text)
    {
        List<Line> lines = [];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            var content = line.TrimStart(' ');
            if (content.Length == 0 || content.StartsWith('#') || content is "---" or "...")
            {
                continue;
            }

            lines.Add(new Line(line.Length - content.Length, content));
        }

        var index = 0;
        return lines.Count == 0 ? null : Block(lines, ref index, lines[0].Indent, 0);
    }

    /// <summary>A mapping's nested mapping, or null.</summary>
    /// <param name="map">The mapping, or null.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value when it is a mapping.</returns>
    public static Dictionary<string, object?>? ChildMap(Dictionary<string, object?>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) ? value as Dictionary<string, object?> : null;
    }

    /// <summary>A mapping's nested sequence, or null.</summary>
    /// <param name="map">The mapping, or null.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value when it is a sequence.</returns>
    public static List<object?>? ChildList(Dictionary<string, object?>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) ? value as List<object?> : null;
    }

    /// <summary>A mapping's scalar, or empty.</summary>
    /// <param name="map">The mapping, or null.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value when it is a scalar, otherwise empty.</returns>
    public static string ChildText(Dictionary<string, object?>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) && value is string text ? text : string.Empty;
    }

    private static object? Block(List<Line> lines, ref int index, int indent, int depth)
    {
        if (depth > MaximumDepth)
        {
            while (index < lines.Count && lines[index].Indent >= indent)
            {
                index++;
            }

            return null;
        }

        return IsItem(lines[index].Text)
            ? Sequence(lines, ref index, indent, depth)
            : Mapping(lines, ref index, indent, depth);
    }

    private static Dictionary<string, object?> Mapping(List<Line> lines, ref int index, int indent, int depth)
    {
        Dictionary<string, object?> map = new(StringComparer.Ordinal);
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Indent < indent)
            {
                break;
            }

            if (line.Indent > indent || IsItem(line.Text) || !TrySplitKey(line.Text, out var key, out var value))
            {
                // A continuation or a line this reader cannot place.
                index++;
                continue;
            }

            index++;
            if (value.Length == 0)
            {
                var nested = index < lines.Count
                             && (lines[index].Indent > indent
                                 || (lines[index].Indent == indent && IsItem(lines[index].Text)));
                map[key] = nested ? Block(lines, ref index, lines[index].Indent, depth + 1) : null;
            }
            else if (value[0] is '|' or '>')
            {
                while (index < lines.Count && lines[index].Indent > indent)
                {
                    index++;
                }

                map[key] = string.Empty;
            }
            else
            {
                map[key] = Scalar(value);
            }
        }

        return map;
    }

    private static List<object?> Sequence(List<Line> lines, ref int index, int indent, int depth)
    {
        List<object?> list = [];
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Indent > indent)
            {
                index++;
                continue;
            }

            if (line.Indent < indent || !IsItem(line.Text))
            {
                break;
            }

            var rest = line.Text[1..].TrimStart(' ');
            if (rest.Length == 0)
            {
                index++;
                list.Add(index < lines.Count && lines[index].Indent > indent
                    ? Block(lines, ref index, lines[index].Indent, depth + 1)
                    : null);
            }
            else if (IsItem(rest) || TrySplitKey(rest, out _, out _))
            {
                // "- key: value" opens a mapping whose keys line up with the first one.
                var nested = indent + line.Text.Length - rest.Length;
                lines[index] = new Line(nested, rest);
                list.Add(Block(lines, ref index, nested, depth + 1));
            }
            else
            {
                index++;
                list.Add(Scalar(rest));
            }
        }

        return list;
    }

    private static bool IsItem(string text)
    {
        return text == "-" || text.StartsWith("- ", StringComparison.Ordinal);
    }

    private static bool TrySplitKey(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        string after;
        if (text[0] is '"' or '\'')
        {
            var close = text.IndexOf(text[0], 1);
            if (close < 0)
            {
                return false;
            }

            key = text[1..close];
            after = text[(close + 1)..];
        }
        else
        {
            var colon = text.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                if (!text.EndsWith(':'))
                {
                    return false;
                }

                colon = text.Length - 1;
            }

            key = text[..colon].TrimEnd();
            after = text[colon..];
        }

        if (key.Length == 0 || !after.StartsWith(':') || (after.Length > 1 && after[1] != ' '))
        {
            return false;
        }

        value = after[1..].Trim();
        return true;
    }

    private static string Scalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private readonly record struct Line(int Indent, string Text);
}
