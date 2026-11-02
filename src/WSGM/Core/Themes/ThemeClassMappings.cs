using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly int _count;
    private readonly Func<string>? _reread;
    private readonly Dictionary<string, string>? _strong;
    private readonly WeakReference<Dictionary<string, string>>? _weak;

    /// <summary>Creates an empty table: every name is left as written.</summary>
    public ThemeClassMappings()
        : this(new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    private ThemeClassMappings(Dictionary<string, string> mappings)
    {
        _strong = mappings;
        _count = mappings.Count;
    }

    private ThemeClassMappings(Dictionary<string, string> mappings, Func<string> reread)
    {
        _weak = new WeakReference<Dictionary<string, string>>(mappings);
        _reread = reread;
        _count = mappings.Count;
    }

    /// <summary>A table with nothing in it.</summary>
    public static ThemeClassMappings Empty { get; } = new();

    /// <summary>How many older names the table maps.</summary>
    public int Count => _count;

    /// <summary>The same table, held only while a rewrite uses it and read again from its source after that.</summary>
    /// <param name="reread">Reads the document again, normally from the saved translations file.</param>
    /// <returns>A table that lets the collector reclaim its entries between rewrites.</returns>
    /// <remarks>
    ///     DeckThemes' table maps some fifty thousand names, about 7 MB once parsed, and a stylesheet is
    ///     rewritten once and then cached by its inject. Keeping the parsed table for the life of the
    ///     process held that memory for nothing.
    /// </remarks>
    public ThemeClassMappings Releasable(Func<string> reread)
    {
        ArgumentNullException.ThrowIfNull(reread);
        return _strong is { } mappings ? new ThemeClassMappings(mappings, reread) : this;
    }

    private Dictionary<string, string> Mappings()
    {
        if (_strong is { } strong)
        {
            return strong;
        }

        if (_weak!.TryGetTarget(out var held))
        {
            return held;
        }

        Dictionary<string, string> read;
        try
        {
            read = Parse(_reread!())._strong!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Themes: the saved class translations could not be read again: {ex.Message}");
            read = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        _weak.SetTarget(read);
        return read;
    }

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
        return Mappings().TryGetValue(className, out var latest) ? latest : className;
    }

    /// <summary>Rewrites every class a stylesheet names to its current spelling.</summary>
    /// <param name="css">The stylesheet as the theme ships it.</param>
    /// <returns>The stylesheet to inject.</returns>
    public string Rewrite(string css)
    {
        if (_count == 0 || css.Length == 0)
        {
            return css;
        }

        // One strong reference for the whole rewrite.
        var mappings = Mappings();
        var selectors = ClassSelector().Replace(css, match => "." + Translate(mappings, match.Groups[1].Value));
        return AttributeSelector().Replace(selectors, match =>
            match.Groups[1].Value + Translate(mappings, match.Groups[2].Value) + match.Groups[3].Value);
    }

    private static string Translate(Dictionary<string, string> mappings, string className)
    {
        return mappings.TryGetValue(className, out var latest) ? latest : className;
    }

    // CSS Loader splits on `(\.[_a-zA-Z]+[_a-zA-Z0-9-]*)` and maps the token after the dot.
    [GeneratedRegex(@"\.([_a-zA-Z]+[_a-zA-Z0-9-]*)")]
    private static partial Regex ClassSelector();

    // And on `(\[class[*^|~]="[_a-zA-Z0-9-]*"\])`, mapping the quoted name.
    [GeneratedRegex(@"(\[class[*^|~]=\"")([_a-zA-Z0-9-]*)(\""\])")]
    private static partial Regex AttributeSelector();
}
