using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WSGM.Core;

/// <summary>A theme folder that CSS Loader would refuse, with its reason.</summary>
/// <param name="message">The reason, in CSS Loader's words where it has them.</param>
public sealed class ThemeManifestException(string message) : Exception(message);

/// <summary>The flags a <c>theme.json</c> may carry, upper-cased as CSS Loader reads them.</summary>
public static class ThemeFlags
{
    /// <summary>The theme is a profile: a generated set of dependencies and their patch values.</summary>
    public const string Preset = "PRESET";

    /// <summary>Disabling the theme leaves its dependencies enabled.</summary>
    public const string KeepDependencies = "KEEP_DEPENDENCIES";

    /// <summary>Enabling the theme asks whether to enable its dependencies.</summary>
    public const string OptionalDependencies = "OPTIONAL_DEPENDENCIES";

    /// <summary>The theme wants CSS Loader's navigation patch, which WSGM does not have.</summary>
    public const string RequireNavPatch = "REQUIRE_NAV_PATCH";
}

/// <summary>What CSS Loader reads out of a theme folder, before any file is loaded.</summary>
/// <remarks>
///     Mirrors <c>css_theme.py</c> (b1bc683): the same defaults, the same keys, and the same
///     refusals. A folder without <c>theme.json</c> but with <c>theme.css</c> is a theme too, injected
///     everywhere; anything else is not a theme.
/// </remarks>
public static class ThemeManifest
{
    /// <summary>The highest manifest version CSS Loader 9 understands, which is what WSGM mirrors.</summary>
    public const int SupportedVersion = 9;

    /// <summary>The manifest file's name inside a theme folder.</summary>
    public const string FileName = "theme.json";

    /// <summary>The stylesheet a folder without a manifest is injected from.</summary>
    public const string PlainStylesheet = "theme.css";

    /// <summary>Reads a manifest document.</summary>
    /// <param name="root">The parsed <c>theme.json</c>.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ThemeManifestException">CSS Loader would refuse the manifest.</exception>
    public static ThemeManifestData Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ThemeManifestException("theme.json is not an object");
        }

        if (!root.TryGetProperty("name", out var nameProperty) || nameProperty.ValueKind != JsonValueKind.String
                                                               || string.IsNullOrWhiteSpace(nameProperty.GetString()))
        {
            throw new ThemeManifestException("'name'");
        }

        var name = nameProperty.GetString()!;
        var displayName = ThemeJson.OptionalString(root, "display_name");
        var id = ThemeJson.OptionalString(root, "id") ?? name;
        var version = ThemeJson.OptionalString(root, "version") ?? "v1.0";
        var author = ThemeJson.OptionalString(root, "author") ?? string.Empty;
        var require = 1;
        if (root.TryGetProperty("manifest_version", out var versionProperty))
        {
            require = versionProperty.ValueKind switch
            {
                JsonValueKind.Number when versionProperty.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(versionProperty.GetString(), out var parsed) => parsed,
                _ => throw new ThemeManifestException("'manifest_version' is not a number")
            };
        }

        if (require > SupportedVersion)
        {
            throw new ThemeManifestException(
                $"A newer version of the CssLoader is required to load this theme (Read manifest version {require} but only up to {SupportedVersion} is supported)");
        }

        List<string> flags = [];
        if (root.TryGetProperty("flags", out var flagsProperty) && flagsProperty.ValueKind == JsonValueKind.Array)
        {
            flags.AddRange(flagsProperty.EnumerateArray()
                .Where(flag => flag.ValueKind == JsonValueKind.String)
                .Select(flag => flag.GetString()!.ToUpperInvariant()));
        }

        Dictionary<string, IReadOnlyList<string>> tabs = new(StringComparer.Ordinal);
        if (root.TryGetProperty("tabs", out var tabsProperty) && tabsProperty.ValueKind == JsonValueKind.Object)
        {
            foreach (var mapping in tabsProperty.EnumerateObject())
            {
                tabs[mapping.Name] = StringList(mapping.Value);
            }
        }

        Dictionary<string, IReadOnlyDictionary<string, JsonNode>> dependencies = new(StringComparer.Ordinal);
        if (root.TryGetProperty("dependencies", out var dependenciesProperty)
            && dependenciesProperty.ValueKind == JsonValueKind.Object)
        {
            foreach (var dependency in dependenciesProperty.EnumerateObject())
            {
                Dictionary<string, JsonNode> values = new(StringComparer.Ordinal);
                if (dependency.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var patch in dependency.Value.EnumerateObject())
                    {
                        // The value as written: an option, or an option with its components' values.
                        values[patch.Name] = JsonNode.Parse(patch.Value.GetRawText()) ?? JsonValue.Create(string.Empty);
                    }
                }

                dependencies[dependency.Name] = values;
            }
        }

        var injects = root.TryGetProperty("inject", out var injectProperty)
            ? InjectMap(injectProperty)
            : [];

        List<ThemePatchManifest> patches = [];
        if (root.TryGetProperty("patches", out var patchesProperty)
            && patchesProperty.ValueKind == JsonValueKind.Object)
        {
            patches.AddRange(patchesProperty.EnumerateObject().Select(patch => ParsePatch(patch.Name, patch.Value)));
        }

        return new ThemeManifestData(
            name, displayName, id, version, author, require, flags, tabs, dependencies, injects, patches);
    }

    /// <summary>The manifest of a folder that has only <c>theme.css</c>.</summary>
    /// <param name="folderName">The folder's name, which names the theme.</param>
    /// <returns>A manifest injecting that stylesheet into every window.</returns>
    public static ThemeManifestData PlainStylesheetManifest(string folderName)
    {
        return new ThemeManifestData(
            folderName,
            null,
            folderName,
            "v1.0",
            string.Empty,
            1,
            [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyDictionary<string, JsonNode>>(StringComparer.Ordinal),
            [new ThemeInjectManifest(PlainStylesheet, [".*"])],
            []);
    }

    private static ThemePatchManifest ParsePatch(string name, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ThemeManifestException($"In patch '{name}' there is less than 1 value present");
        }

        var defaultValue = json.TryGetProperty("default", out var defaultProperty)
            ? ValueText(defaultProperty)
            : null;
        var type = ThemeJson.OptionalString(json, "type") ?? "dropdown";
        List<KeyValuePair<string, IReadOnlyList<ThemeInjectManifest>>> options = [];
        var version2 = json.TryGetProperty("values", out var valuesProperty);
        if (version2)
        {
            if (valuesProperty.ValueKind == JsonValueKind.Object)
            {
                options.AddRange(valuesProperty.EnumerateObject().Select(option =>
                    new KeyValuePair<string, IReadOnlyList<ThemeInjectManifest>>(
                        option.Name, InjectMap(option.Value))));
            }
        }
        else
        {
            // The version 1 format: every key beside `default` is an option.
            options.AddRange(json.EnumerateObject()
                .Where(property => property.Name != "default")
                .Select(option => new KeyValuePair<string, IReadOnlyList<ThemeInjectManifest>>(
                    option.Name, InjectMap(option.Value))));
        }

        if (options.Count == 0)
        {
            throw new ThemeManifestException($"In patch '{name}' there is less than 1 value present");
        }

        defaultValue ??= options[0].Key;
        if (options.All(option => option.Key != defaultValue))
        {
            throw new ThemeManifestException(
                $"In patch '{name}', '{defaultValue}' does not exist as a patch option");
        }

        List<ThemeComponentManifest> components = [];
        if (json.TryGetProperty("components", out var componentsProperty)
            && componentsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in componentsProperty.EnumerateArray())
            {
                var parsed = ParseComponent(component);
                if (options.All(option => option.Key != parsed.On))
                {
                    throw new ThemeManifestException("Component references non-existent value");
                }

                components.Add(parsed);
            }
        }

        return new ThemePatchManifest(name, type, defaultValue, options, components);
    }

    private static ThemeComponentManifest ParseComponent(JsonElement json)
    {
        // Intentionally strict, as CSS Loader is: a component missing a field is a broken theme.
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new ThemeManifestException("A component is not an object");
        }

        var name = ThemeJson.RequiredString(json, "name");
        var type = ThemeJson.RequiredString(json, "type");
        if (type is not ("color-picker" or "image-picker"))
        {
            throw new ThemeManifestException($"Unknown component type '{type}'");
        }

        var defaultValue = ThemeJson.RequiredString(json, "default");
        var on = ThemeJson.RequiredString(json, "on");
        var variable = ThemeJson.RequiredString(json, "css_variable");
        if (!variable.StartsWith("--", StringComparison.Ordinal))
        {
            variable = "--" + variable;
        }

        if (!json.TryGetProperty("tabs", out var tabsProperty))
        {
            throw new ThemeManifestException("'tabs'");
        }

        return new ThemeComponentManifest(name, type, on, defaultValue, variable, StringList(tabsProperty));
    }

    private static IReadOnlyList<ThemeInjectManifest> InjectMap(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return
            [.. json.EnumerateObject().Select(entry => new ThemeInjectManifest(entry.Name, StringList(entry.Value)))];
    }

    private static IReadOnlyList<string> StringList(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. json.EnumerateArray().Select(ValueText)];
    }

    private static string ValueText(JsonElement json)
    {
        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString() ?? string.Empty,
            JsonValueKind.Number => json.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => json.GetRawText()
        };
    }
}

/// <summary>One entry of an <c>inject</c> map: a stylesheet, or a CSS variable, and the windows it is for.</summary>
/// <param name="Key">
///     The stylesheet's path relative to the theme folder, or a variable name starting with <c>--</c>,
///     in which case the first tab is the variable's value.
/// </param>
/// <param name="Tabs">The tab names or aliases, expanded by <see cref="ThemeTargets" />.</param>
public sealed record ThemeInjectManifest(string Key, IReadOnlyList<string> Tabs);

/// <summary>A colour or image the user picks for a patch option.</summary>
/// <param name="Name">The component's label.</param>
/// <param name="Type"><c>color-picker</c> or <c>image-picker</c>.</param>
/// <param name="On">The patch option the component belongs to.</param>
/// <param name="Default">The default colour or image path.</param>
/// <param name="CssVariable">The variable the value is written to, with its <c>--</c>.</param>
/// <param name="Tabs">The windows the variable is set in.</param>
public sealed record ThemeComponentManifest(
    string Name,
    string Type,
    string On,
    string Default,
    string CssVariable,
    IReadOnlyList<string> Tabs);

/// <summary>One patch: a named choice between sets of injects.</summary>
/// <param name="Name">The patch's label.</param>
/// <param name="Type"><c>dropdown</c>, <c>checkbox</c>, <c>slider</c> or <c>none</c>; anything else is a dropdown.</param>
/// <param name="Default">The option chosen until the user picks another.</param>
/// <param name="Options">The options in manifest order, each with what it injects.</param>
/// <param name="Components">The colours and images picked under the patch.</param>
public sealed record ThemePatchManifest(
    string Name,
    string Type,
    string Default,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<ThemeInjectManifest>>> Options,
    IReadOnlyList<ThemeComponentManifest> Components);

/// <summary>Everything a theme's manifest declares.</summary>
/// <param name="Name">The theme's identity among installed themes.</param>
/// <param name="DisplayName">What the user sees, or null to show the name.</param>
/// <param name="Id">The store id, or the name when the manifest has none.</param>
/// <param name="Version">The version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Require">The manifest version the theme was written for.</param>
/// <param name="Flags">The theme's flags, upper-cased.</param>
/// <param name="TabMappings">The theme's own tab aliases.</param>
/// <param name="Dependencies">
///     Themes this one needs, each with the patch values it sets on them: an option, or an option
///     with its components' values, as the manifest wrote it.
/// </param>
/// <param name="Injects">What the theme always injects while enabled.</param>
/// <param name="Patches">The theme's patches.</param>
public sealed record ThemeManifestData(
    string Name,
    string? DisplayName,
    string Id,
    string Version,
    string Author,
    int Require,
    IReadOnlyList<string> Flags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TabMappings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode>> Dependencies,
    IReadOnlyList<ThemeInjectManifest> Injects,
    IReadOnlyList<ThemePatchManifest> Patches);
