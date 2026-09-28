using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests.Core.Themes;

/// <summary>What CSS Loader reads out of a theme.json, and what it refuses, mirrored field for field.</summary>
public sealed class ThemeManifestTests
{
    private static ThemeManifestData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ThemeManifest.Parse(document.RootElement);
    }

    [Fact]
    public void ReadsEveryFieldWithCssLoadersDefaults()
    {
        var manifest = Parse("""
                             {
                               "name": "Dark Deck", "display_name": "Dark Deck (BPM)", "author": "Squishy", "version": "v2.1",
                               "manifest_version": 9, "flags": ["keep_dependencies"],
                               "tabs": { "default": ["bigpicture"] },
                               "inject": { "shared.css": ["bigpicture", "QuickAccess"], "--accent": ["#ff0000", "QuickAccess"] },
                               "dependencies": { "Rounded Corners": { "Radius": "Large" } },
                               "patches": {
                                 "Accent": { "type": "dropdown", "default": "Orange",
                                   "values": { "Orange": { "orange.css": ["QuickAccess"] }, "Blue": {} },
                                   "components": [{ "name": "Highlight", "type": "color-picker", "on": "Orange",
                                     "default": "#FF9D3D", "css_variable": "highlight", "tabs": ["QuickAccess"] }] }
                               }
                             }
                             """);

        Assert.Equal("Dark Deck", manifest.Name);
        Assert.Equal("Dark Deck (BPM)", manifest.DisplayName);
        Assert.Equal("Dark Deck", manifest.Id);
        Assert.Equal("v2.1", manifest.Version);
        Assert.Equal("Squishy", manifest.Author);
        Assert.Equal(9, manifest.Require);
        Assert.Equal(["KEEP_DEPENDENCIES"], manifest.Flags);
        Assert.Equal(["bigpicture"], manifest.TabMappings["default"]);
        Assert.Equal(2, manifest.Injects.Count);
        Assert.Equal("--accent", manifest.Injects[1].Key);
        Assert.Equal(["#ff0000", "QuickAccess"], manifest.Injects[1].Tabs);
        Assert.Equal("Large", manifest.Dependencies["Rounded Corners"]["Radius"]!.GetValue<string>());
        var patch = Assert.Single(manifest.Patches);
        Assert.Equal("Accent", patch.Name);
        Assert.Equal("Orange", patch.Default);
        Assert.Equal(["Orange", "Blue"], patch.Options.Select(option => option.Key));
        var component = Assert.Single(patch.Components);
        Assert.Equal("--highlight", component.CssVariable);
        Assert.Equal("Orange", component.On);
    }

    [Fact]
    public void DefaultsMatchCssLoaderWhenFieldsAreAbsent()
    {
        var manifest = Parse("""{ "name": "Plain", "patches": { "Style": { "A": {}, "B": {} } } }""");

        Assert.Equal("Plain", manifest.Id);
        Assert.Equal("v1.0", manifest.Version);
        Assert.Equal("", manifest.Author);
        Assert.Equal(1, manifest.Require);
        Assert.Null(manifest.DisplayName);
        // The version 1 patch format: every key beside `default` is an option, the first the default.
        var patch = Assert.Single(manifest.Patches);
        Assert.Equal("A", patch.Default);
        Assert.Equal("dropdown", patch.Type);
        Assert.Equal(["A", "B"], patch.Options.Select(option => option.Key));
    }

    [Fact]
    public void ADependencyKeepsAnOptionWithComponentValuesAsWritten()
    {
        // A profile writes a dependency's patch as {value, components}; that JSON must survive so the
        // components' colours are set when the profile is enabled.
        var manifest = Parse("""
                             { "name": "Night.profile", "flags": ["PRESET"],
                               "dependencies": { "Dark Deck": { "Accent": { "value": "Orange", "components": { "Highlight": "#123456" } } } } }
                             """);

        var value = manifest.Dependencies["Dark Deck"]["Accent"]!.AsObject();
        Assert.Equal("Orange", value["value"]!.GetValue<string>());
        Assert.Equal("#123456", value["components"]!["Highlight"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{ "manifest_version": 9 }""", "'name'")]
    [InlineData("""{ "name": "x", "manifest_version": 10 }""", "A newer version of the CssLoader is required")]
    [InlineData("""{ "name": "x", "patches": { "Empty": { "default": "A" } } }""", "less than 1 value present")]
    [InlineData("""{ "name": "x", "patches": { "P": { "default": "C", "values": { "A": {}, "B": {} } } } }""",
        "'C' does not exist as a patch option")]
    [InlineData(
        """{ "name": "x", "patches": { "P": { "values": { "A": {} }, "components": [{ "name": "c", "type": "slider", "on": "A", "default": "#fff", "css_variable": "v", "tabs": [] }] } } }""",
        "Unknown component type 'slider'")]
    [InlineData(
        """{ "name": "x", "patches": { "P": { "values": { "A": {} }, "components": [{ "name": "c", "type": "color-picker", "on": "Z", "default": "#fff", "css_variable": "v", "tabs": [] }] } } }""",
        "Component references non-existent value")]
    [InlineData("[]", "not an object")]
    public void RefusesWhatCssLoaderRefuses(string json, string reason)
    {
        var refused = Assert.Throws<ThemeManifestException>(() => Parse(json));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithOnlyAStylesheetIsAThemeInjectedEverywhere()
    {
        var manifest = ThemeManifest.PlainStylesheetManifest("My Folder");

        Assert.Equal("My Folder", manifest.Name);
        Assert.Equal("v1.0", manifest.Version);
        var inject = Assert.Single(manifest.Injects);
        Assert.Equal("theme.css", inject.Key);
        Assert.Equal([".*"], inject.Tabs);
    }
}
