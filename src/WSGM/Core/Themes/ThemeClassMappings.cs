using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>
///     The class-name translations that let a theme written against readable class names style a
///     client whose class names are content hashes.
/// </summary>
/// <remarks>
///     <para>
///         Steam's class names change with every client build; a theme names them as its author saw
///         them. DeckThemes publishes a translation table, <c>stable.json</c> or <c>beta.json</c>, of
///         the form <c>{ "uid": ["oldest", …, "current"] }</c>, and CSS Loader maps every older
///         spelling to the current one before injecting (<c>initialize_class_mappings</c> and
///         <c>Inject.load</c> in b1bc683). This is that mapping, and the two rewrites it makes: a
///         class selector, <c>.Name</c>, and a class attribute selector,
///         <c>[class*="Name"]</c> and its <c>^=</c>, <c>|=</c> and <c>~=</c> forms.
///     </para>
///     <para>
///         The table is the one thing about CSS Loader compatibility that has to be kept current by
///         someone else. Without it a theme still loads; it just names classes the client no longer
///         has.
///     </para>
/// </remarks>
public sealed partial class ThemeClassMappings
{
    private readonly Dictionary<string, string> _mappings;

    /// <summary>Creates an empty table: every name is left as written.</summary>
    public ThemeClassMappings()
        : this(new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    private ThemeClassMappings(Dictionary<string, string> mappings)
    {
        _mappings = mappings;
    }

    /// <summary>A table with nothing in it.</summary>
    public static ThemeClassMappings Empty { get; } = new();

    /// <summary>How many older names the table maps.</summary>
    public int Count => _mappings.Count;

    /// <summary>Reads DeckThemes' translation document.</summary>
    /// <param name="json">The document text.</param>
    /// <returns>The table.</returns>
    /// <exception cref="JsonException">The document is not the expected shape.</exception>
    public static ThemeClassMappings Parse(string json)
    {
        Dictionary<string, string> mappings = new(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The class translations are not an object.");
        }

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            List<string> versions = [];
            foreach (var version in entry.Value.EnumerateArray())
            {
                if (version.ValueKind == JsonValueKind.String && version.GetString() is { Length: > 0 } text)
                {
                    versions.Add(text);
                }
            }

            if (versions.Count < 2)
            {
                continue;
            }

            var latest = versions[^1];
            for (var index = 0; index < versions.Count - 1; index++)
            {
                mappings[versions[index]] = latest;
            }
        }

        return new ThemeClassMappings(mappings);
    }

    /// <summary>The current spelling of a class name, or the name itself when the table has none.</summary>
    /// <param name="className">A class name as a theme wrote it.</param>
    /// <returns>The name to inject.</returns>
    public string Translate(string className)
    {
        return _mappings.TryGetValue(className, out var latest) ? latest : className;
    }

    /// <summary>Rewrites every class a stylesheet names to its current spelling.</summary>
    /// <param name="css">The stylesheet as the theme ships it.</param>
    /// <returns>The stylesheet to inject.</returns>
    public string Rewrite(string css)
    {
        if (_mappings.Count == 0 || css.Length == 0)
        {
            return css;
        }

        var selectors = ClassSelector().Replace(css, match => "." + Translate(match.Groups[1].Value));
        return AttributeSelector().Replace(selectors, match =>
            match.Groups[1].Value + Translate(match.Groups[2].Value) + match.Groups[3].Value);
    }

    // CSS Loader splits on `(\.[_a-zA-Z]+[_a-zA-Z0-9-]*)` and maps the token after the dot.
    [GeneratedRegex(@"\.([_a-zA-Z]+[_a-zA-Z0-9-]*)")]
    private static partial Regex ClassSelector();

    // And on `(\[class[*^|~]="[_a-zA-Z0-9-]*"\])`, mapping the quoted name.
    [GeneratedRegex(@"(\[class[*^|~]=\"")([_a-zA-Z0-9-]*)(\""\])")]
    private static partial Regex AttributeSelector();
}
