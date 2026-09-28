using System.Text.Json.Nodes;
using WSGM.Core;

namespace WSGM.Tests.Core.Themes;

/// <summary>
///     The loader's rules, mirrored from CSS Loader: which folders are themes, the cascade order, what
///     enabling and disabling do to dependencies, and what a profile writes.
/// </summary>
public sealed class ThemeLoaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.themes." + Guid.NewGuid().ToString("N"));

    public ThemeLoaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private void WriteTheme(string folder, string manifest, params (string Name, string Css)[] files)
    {
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "theme.json"), manifest);
        foreach (var (name, css) in files)
        {
            File.WriteAllText(Path.Combine(path, name), css);
        }
    }

    private ThemeLoader Loaded()
    {
        var loader = new ThemeLoader(_root);
        loader.Load();
        return loader;
    }

    [Fact]
    public void ReadsThemesRefusesWhatIsNotOneAndSuffixesADuplicateName()
    {
        WriteTheme("a", """{ "name": "Same" }""", ("theme.css", "a{}"));
        WriteTheme("b", """{ "name": "Same" }""", ("theme.css", "b{}"));
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        File.WriteAllText(Path.Combine(_root, "plain", "theme.css"), ".x{}");
        Directory.CreateDirectory(Path.Combine(_root, "junk"));
        File.WriteAllText(Path.Combine(_root, "junk", "readme.txt"), "");
        WriteTheme("newer", """{ "name": "Newer", "manifest_version": 42 }""");

        var loader = Loaded();

        Assert.Equal(["Same", "Same_0", "plain"],
            loader.Themes.Select(theme => theme.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Same", loader.Find("Same_0")!.DisplayName);
        Assert.Equal(["junk", "newer"], loader.LastLoadErrors.Select(error => error.Folder).Order());
        Assert.Contains("newer version of the CssLoader",
            loader.LastLoadErrors.Single(error => error.Folder == "newer").Error);
    }

    [Fact]
    public void EnablingInjectsTheThemesBlocksWithTheirTargetsAndSavesTheState()
    {
        WriteTheme("dark", """
                           { "name": "Dark", "inject": { "dark.css": ["QuickAccess"], "--accent": ["red", "bigpicture"] },
                             "patches": { "Style": { "default": "Round", "values": { "Round": { "round.css": ["MainMenu"] }, "Square": {} } } } }
                           """, ("dark.css", ".a{}"), ("round.css", ".r{}"));
        var loader = Loaded();
        Assert.Empty(loader.ActiveStyles());

        Assert.Null(loader.SetThemeState("Dark", true));

        var styles = loader.ActiveStyles();
        Assert.Equal([".a{}", ":root { --accent: red; }", ".r{}"], styles.Select(style => style.Css));
        Assert.Equal(["QuickAccess.*"], styles[0].Targets);
        Assert.Equal(
            ["~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~", ThemeTargets.BigPictureWindowName],
            styles[1].Targets);
        Assert.Equal(["MainMenu.*"], styles[2].Targets);
        Assert.All(styles, style => Assert.Equal(16, style.Hash.Length));

        // The saved state is CSS Loader's own file, so a reload finds the theme still on.
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "dark", "config_USER.json")))!.AsObject();
        Assert.True(saved["active"]!.GetValue<bool>());
        Assert.Equal("Round", saved["Style"]!.GetValue<string>());
        var again = Loaded();
        Assert.True(again.Find("Dark")!.Enabled);
        Assert.Equal(3, again.ActiveStyles().Count);
    }

    [Fact]
    public void APatchChoosesItsBlocksAndAComponentWritesItsVariable()
    {
        WriteTheme("dark", """
                           { "name": "Dark", "patches": { "Style": { "type": "dropdown", "default": "Round",
                             "values": { "Round": { "round.css": ["QuickAccess"] }, "Square": { "square.css": ["QuickAccess"] } },
                             "components": [{ "name": "Tint", "type": "color-picker", "on": "Square", "default": "#102030",
                               "css_variable": "tint", "tabs": ["QuickAccess"] }] } } }
                           """, ("round.css", ".r{}"), ("square.css", ".s{}"));
        var loader = Loaded();
        loader.SetThemeState("Dark", true);
        Assert.Equal([".r{}"], loader.ActiveStyles().Select(style => style.Css));

        Assert.Null(loader.SetPatch("Dark", "Style", "Square"));

        Assert.Equal(
            [".s{}", ":root { --tint: #102030; --tint_r: 16; --tint_g: 32; --tint_b: 48; --tint_rgb: 16, 32, 48; }"],
            loader.ActiveStyles().Select(style => style.Css));

        Assert.Null(loader.SetComponent("Dark", "Style", "Tint", "hsla(120, 100%, 50%, 1)"));
        Assert.Contains("--tint: hsla(120, 100%, 50%, 1); --tint_r: 0; --tint_g: 255; --tint_b: 0;",
            loader.ActiveStyles()[1].Css);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "dark", "config_USER.json")))!.AsObject();
        Assert.Equal("Square", saved["Style"]!["value"]!.GetValue<string>());
        Assert.Equal("hsla(120, 100%, 50%, 1)", saved["Style"]!["components"]!["Tint"]!.GetValue<string>());

        Assert.Equal("Did not find patch 'Nope' for theme 'Dark'", loader.SetPatch("Dark", "Nope", "x"));
        Assert.Equal("Did not find theme 'Gone'", loader.SetPatch("Gone", "Style", "x"));
    }

    [Fact]
    public void DependenciesComeFirstInTheCascadeAndFollowTheirDependant()
    {
        WriteTheme("base",
            """{ "name": "Base", "patches": { "Radius": { "default": "Small", "values": { "Small": { "s.css": ["QuickAccess"] }, "Large": { "l.css": ["QuickAccess"] } } } } }""",
            ("s.css", ".small{}"), ("l.css", ".large{}"));
        WriteTheme("top",
            """{ "name": "Top", "inject": { "top.css": ["QuickAccess"] }, "dependencies": { "Base": { "Radius": "Large" } } }""",
            ("top.css", ".top{}"));
        WriteTheme("other",
            """{ "name": "Other", "inject": { "o.css": ["QuickAccess"] }, "dependencies": { "Base": {} } }""",
            ("o.css", ".other{}"));
        var loader = Loaded();

        Assert.Null(loader.SetThemeState("Top", true));

        // Base was enabled with Large, and it is injected before Top so Top's rules win.
        Assert.True(loader.Find("Base")!.Enabled);
        Assert.Equal([".large{}", ".top{}"], loader.ActiveStyles().Select(style => style.Css));

        // Disabling Top disables Base, which nothing else on needs; with Other on, Base stays.
        loader.SetThemeState("Top", false);
        Assert.False(loader.Find("Base")!.Enabled);
        loader.SetThemeState("Other", true);
        loader.SetThemeState("Top", true);
        loader.SetThemeState("Top", false);
        Assert.True(loader.Find("Base")!.Enabled);
        Assert.Equal([".large{}", ".other{}"], loader.ActiveStyles().Select(style => style.Css));
    }

    [Fact]
    public void KeepDependenciesLeavesThemOnAndAPriorityFileMovesAThemeLater()
    {
        WriteTheme("base", """{ "name": "Base", "inject": { "b.css": ["QuickAccess"] } }""", ("b.css", ".b{}"));
        WriteTheme("top",
            """{ "name": "Top", "flags": ["KEEP_DEPENDENCIES"], "inject": { "t.css": ["QuickAccess"] }, "dependencies": { "Base": {} } }""",
            ("t.css", ".t{}"));
        WriteTheme("last", """{ "name": "Last", "inject": { "z.css": ["QuickAccess"] } }""", ("z.css", ".z{}"));
        File.WriteAllText(Path.Combine(_root, "base", "PRIORITY"), "5");
        var loader = Loaded();
        loader.SetThemeState("Top", true);
        loader.SetThemeState("Last", true);

        // Base's priority 5 minus one for Top puts it after Last and Top, which score 0 and keep
        // their folder order.
        Assert.Equal([".z{}", ".t{}", ".b{}"], loader.ActiveStyles().Select(style => style.Css));

        loader.SetThemeState("Top", false);
        Assert.True(loader.Find("Base")!.Enabled, "KEEP_DEPENDENCIES leaves the dependency on");
    }

    [Fact]
    public void AProfileIsWrittenAsAThemeOfDependenciesAndEnablesThemWithTheirValues()
    {
        WriteTheme("dark",
            """{ "name": "Dark", "inject": { "d.css": ["QuickAccess"] }, "patches": { "Style": { "default": "A", "values": { "A": {}, "B": { "b.css": ["QuickAccess"] } } } } }""",
            ("d.css", ".d{}"), ("b.css", ".b{}"));
        WriteTheme("light", """{ "name": "Light", "inject": { "l.css": ["QuickAccess"] } }""", ("l.css", ".l{}"));
        var loader = Loaded();
        loader.SetThemeState("Dark", true);
        loader.SetPatch("Dark", "Style", "B");

        Assert.Null(loader.GeneratePreset("Night"));

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "Night.profile", "theme.json")))!.AsObject();
        Assert.Equal("Night", manifest["display_name"]!.GetValue<string>());
        Assert.Equal("Night.profile", manifest["name"]!.GetValue<string>());
        Assert.Equal("PRESET", manifest["flags"]![0]!.GetValue<string>());
        Assert.Equal("B", manifest["dependencies"]!["Dark"]!["Style"]!.GetValue<string>());
        Assert.Null(manifest["dependencies"]!["Light"]);

        // After a reload the profile turns Dark on with B, as it was saved.
        loader = Loaded();
        loader.SetThemeState("Dark", false);
        loader.SetPatch("Dark", "Style", "A");
        loader.SetThemeState("Night.profile", true);
        Assert.True(loader.Find("Night.profile")!.IsPreset);
        Assert.True(loader.Find("Dark")!.Enabled);
        Assert.Equal("B", loader.Find("Dark")!.Patches[0].Value);
        Assert.Equal("Theme 'Dark' already exists", loader.GeneratePreset("Dark"));
    }

    [Fact]
    public void DeletingRemovesTheFolderAndAMissingStylesheetIsLeftOutOfTheCascade()
    {
        WriteTheme("dark",
            """{ "name": "Dark", "inject": { "missing.css": ["QuickAccess"], "d.css": ["QuickAccess"] } }""",
            ("d.css", ".d{}"));
        var loader = Loaded();
        loader.SetThemeState("Dark", true);

        Assert.Equal([".d{}"], loader.ActiveStyles().Select(style => style.Css));

        Assert.Null(loader.DeleteTheme("Dark"));
        Assert.False(Directory.Exists(Path.Combine(_root, "dark")));
        Assert.Empty(loader.Themes);
        Assert.Equal("Could not find theme Dark", loader.DeleteTheme("Dark"));
    }

    [Fact]
    public void NewTranslationsAreAppliedOnTheNextRead()
    {
        WriteTheme("dark", """{ "name": "Dark", "inject": { "d.css": ["QuickAccess"] } }""", ("d.css", ".old_Row{}"));
        var loader = Loaded();
        loader.SetThemeState("Dark", true);
        Assert.Equal(".old_Row{}", loader.ActiveStyles()[0].Css);
        var before = loader.ActiveStyles()[0].Hash;

        loader.SetMappings(ThemeClassMappings.Parse("""{ "1": ["old_Row", "new_Row"] }"""));

        var style = loader.ActiveStyles()[0];
        Assert.Equal(".new_Row{}", style.Css);
        Assert.NotEqual(before, style.Hash);
    }

    [Fact]
    public void ThemesThatDependOnEachOtherLoadEnableAndDisable()
    {
        WriteTheme("a", """{ "name": "A", "dependencies": { "B": {} }, "inject": { "a.css": ["All"] } }""",
            ("a.css", "a{}"));
        WriteTheme("b", """{ "name": "B", "dependencies": { "A": {} }, "inject": { "b.css": ["All"] } }""",
            ("b.css", "b{}"));
        WriteTheme("self", """{ "name": "Self", "dependencies": { "Self": {} } }""");

        var loader = Loaded();

        Assert.Equal(["A", "B", "Self"], loader.Themes.Select(theme => theme.Name).Order());
        Assert.Null(loader.SetThemeState("A", true));
        Assert.True(loader.Find("B")!.Enabled);
        Assert.Null(loader.SetThemeState("A", false));
        Assert.Null(loader.SetThemeState("Self", true));
        Assert.Null(loader.SetThemeState("Self", false));
    }
}
