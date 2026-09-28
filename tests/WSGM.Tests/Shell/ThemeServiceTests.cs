using System.Net;
using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Core.Themes;

namespace WSGM.Tests.Shell;

/// <summary>
///     The themes' one owner: what it publishes for the page, the Quick Access section and the
///     cascade, and how a section's rows map back onto the loader.
/// </summary>
public sealed class ThemeServiceTests : IDisposable
{
    private readonly ThemesConfig _config = new();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.themesvc." + Guid.NewGuid().ToString("N"));

    private readonly List<Action<ThemesConfig>> _writes = [];

    public ThemeServiceTests()
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

    /// <summary>A service over a store that answers nothing, so no test reaches the network.</summary>
    private ThemeService Service(bool started = true)
    {
        var handler =
            new ThemeStoreClientTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = new ThemeService(
            new ThemeLoader(_root),
            new ThemeStoreClient(handler, "https://store.example"),
            () => _config,
            change =>
            {
                _writes.Add(change);
                change(_config);
            },
            () => null);
        if (started)
        {
            service.Start();
        }

        return service;
    }

    [Fact]
    public async Task TheQuickAccessSectionListsProfilesThemesAndTheirPatchesUnderThem()
    {
        WriteTheme("dark", """
                           { "name": "Dark", "author": "Squishy", "version": "v2.1", "inject": { "d.css": ["QuickAccess"] },
                             "patches": {
                               "Accent": { "type": "dropdown", "default": "Orange", "values": { "Orange": {}, "Blue": {} },
                                 "components": [{ "name": "Tint", "type": "color-picker", "on": "Orange", "default": "#102030", "css_variable": "tint", "tabs": ["QuickAccess"] }] },
                               "Glow": { "type": "checkbox", "default": "No", "values": { "Yes": {}, "No": {} } },
                               "Blur": { "type": "slider", "default": "Low", "values": { "Off": {}, "Low": {}, "High": {} } } } }
                           """, ("d.css", ".d{}"));
        WriteTheme("night",
            """{ "name": "Night.profile", "display_name": "Night", "flags": ["PRESET"], "dependencies": { "Dark": {} } }""");
        using var service = Service();
        await service.SetEnabledAsync("Dark", true, CancellationToken.None);

        var item = service.ReadExtensionsItem();

        Assert.Equal("wsgm.themes", item.Id);
        Assert.Equal("1 of 1 enabled", item.Detail);
        Assert.Equal(["Browse themes…", "Manage…", "Refresh"], item.Actions!.Select(action => action.Label));
        var settings = item.Settings!;
        Assert.Equal("profile", settings[0].Key);
        Assert.Equal(["None", "Night"], settings[0].Choices);
        Assert.Equal("None", settings[0].TextValue);
        Assert.Equal(("theme:Dark", "boolean", true, "v2.1 · Squishy"),
            (settings[1].Key, settings[1].Kind, settings[1].BooleanValue, settings[1].Description));
        Assert.Equal(("patch:Dark:Accent", "text", "Orange", "theme:Dark"),
            (settings[2].Key, settings[2].Kind, settings[2].TextValue, settings[2].Parent));
        Assert.Equal(("component:Dark:Accent:Tint", "color", "#102030"),
            (settings[3].Key, settings[3].Kind, settings[3].TextValue));
        Assert.Equal(("patch:Dark:Glow", "boolean", false),
            (settings[4].Key, settings[4].Kind, settings[4].BooleanValue));
        Assert.Equal(("patch:Dark:Blur", "number", 1.0), (settings[5].Key, settings[5].Kind, settings[5].NumberValue));
        Assert.Equal(["Off", "Low", "High"], settings[5].Choices);
    }

    [Fact]
    public async Task ASectionRowsValueReachesTheLoaderInTheLoadersOwnWords()
    {
        WriteTheme("dark", """
                           { "name": "Dark", "inject": { "d.css": ["QuickAccess"] },
                             "patches": {
                               "Accent": { "type": "dropdown", "default": "Orange", "values": { "Orange": {}, "Blue": { "b.css": ["QuickAccess"] } },
                                 "components": [{ "name": "Tint", "type": "color-picker", "on": "Blue", "default": "#102030", "css_variable": "tint", "tabs": ["QuickAccess"] }] },
                               "Glow": { "type": "checkbox", "default": "No", "values": { "Yes": { "g.css": ["QuickAccess"] }, "No": {} } },
                               "Blur": { "type": "slider", "default": "Off", "values": { "Off": {}, "Low": { "l.css": ["QuickAccess"] }, "High": {} } } } }
                           """, ("d.css", ".d{}"), ("b.css", ".b{}"), ("g.css", ".g{}"), ("l.css", ".l{}"));
        using var service = Service();

        Assert.True((await service.ConfigureExtensionAsync("theme:Dark", Json("true"), CancellationToken.None))
            .Succeeded);
        Assert.True(
            (await service.ConfigureExtensionAsync("patch:Dark:Accent", Json("\"Blue\""), CancellationToken.None))
            .Succeeded);
        Assert.True((await service.ConfigureExtensionAsync("patch:Dark:Glow", Json("true"), CancellationToken.None))
            .Succeeded);
        Assert.True((await service.ConfigureExtensionAsync("patch:Dark:Blur", Json("1"), CancellationToken.None))
            .Succeeded);
        Assert.True((await service.ConfigureExtensionAsync("component:Dark:Accent:Tint", Json("\"#abcdef\""),
            CancellationToken.None)).Succeeded);

        var css = service.ReadStyles().Styles.Select(style => style.Css).ToList();
        Assert.Equal([".d{}", ".b{}", ".g{}", ".l{}"],
            css.Where(text => !text.StartsWith(":root", StringComparison.Ordinal)));
        Assert.Contains(css, text => text.Contains("--tint: #abcdef;"));

        Assert.False((await service.ConfigureExtensionAsync("patch:Dark:Blur", Json("9"), CancellationToken.None))
            .Succeeded);
        Assert.False((await service.ConfigureExtensionAsync("nonsense", Json("1"), CancellationToken.None)).Succeeded);
        Assert.False((await service.ConfigureExtensionAsync("patch:Dark:Gone", Json("\"x\""), CancellationToken.None))
            .Succeeded);
    }

    [Fact]
    public async Task AProfileChoiceTurnsTheOtherProfileOffAndTheCascadeFollowsTheSwitch()
    {
        WriteTheme("dark", """{ "name": "Dark", "inject": { "d.css": ["QuickAccess"] } }""", ("d.css", ".d{}"));
        WriteTheme("light", """{ "name": "Light", "inject": { "l.css": ["QuickAccess"] } }""", ("l.css", ".l{}"));
        WriteTheme("night",
            """{ "name": "Night.profile", "display_name": "Night", "flags": ["PRESET"], "dependencies": { "Dark": {} } }""");
        WriteTheme("day",
            """{ "name": "Day.profile", "display_name": "Day", "flags": ["PRESET"], "dependencies": { "Light": {} } }""");
        using var service = Service();
        var before = service.StylesRevision;

        await service.ConfigureExtensionAsync("profile", Json("\"Night\""), CancellationToken.None);
        Assert.Equal("Night.profile", service.ReadState().SelectedPreset);
        Assert.Equal([".d{}"], service.ReadStyles().Styles.Select(style => style.Css));

        await service.ConfigureExtensionAsync("profile", Json("\"Day\""), CancellationToken.None);
        Assert.Equal("Day.profile", service.ReadState().SelectedPreset);
        Assert.Equal([".l{}"], service.ReadStyles().Styles.Select(style => style.Css));
        Assert.True(service.StylesRevision > before);

        await service.SetProfileAsync("", CancellationToken.None);
        Assert.Equal("", service.ReadState().SelectedPreset);
        Assert.Empty(service.ReadStyles().Styles);

        // Off in Settings publishes nothing, and says so in the section.
        _config.Enabled = false;
        service.ConfigurationChanged();
        await service.SetEnabledAsync("Dark", true, CancellationToken.None);
        Assert.Empty(service.ReadStyles().Styles);
        Assert.Equal("Off in Settings", service.ReadExtensionsItem().Detail);
    }

    [Fact]
    public async Task HidingATthemeKeepsItOffTheSectionAndIsSaved()
    {
        WriteTheme("dark", """{ "name": "Dark", "inject": { "d.css": ["QuickAccess"] } }""", ("d.css", ".d{}"));
        using var service = Service();

        Assert.True((await service.SetHiddenAsync("Dark", true, CancellationToken.None)).Succeeded);

        Assert.Single(_writes);
        Assert.Equal(["Dark"], _config.HiddenThemes);
        Assert.DoesNotContain(service.ReadExtensionsItem().Settings!, setting => setting.Key == "theme:Dark");
        Assert.Equal("0 of 1 enabled · 1 hidden", service.ReadExtensionsItem().Detail);
        Assert.True(service.ReadState().Themes.Single().Hidden);
    }

    [Fact]
    public async Task TheSectionsActionsOpenThePageOnTheRightTab()
    {
        using var service = Service();

        var browse = await service.ActivateExtensionAsync(ThemeService.ExtensionsBrowseId, CancellationToken.None);
        Assert.Equal("/wsgm/themes", browse.Payload?.GetProperty("route").GetString());
        Assert.Equal("browse", service.ReadState().ActiveTab);

        var manage = await service.ActivateExtensionAsync(ThemeService.ExtensionsManageId, CancellationToken.None);
        Assert.Equal("/wsgm/themes", manage.Payload?.GetProperty("route").GetString());
        Assert.Equal("installed", service.ReadState().ActiveTab);

        Assert.False((await service.ActivateExtensionAsync("wsgm.themes.nope", CancellationToken.None)).Succeeded);
        Assert.False((await service.UpdateAllAsync(CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ARefusedChangeIsPublishedAsTheError()
    {
        using var service = Service();
        var revision = service.Revision;

        var refused = await service.SetEnabledAsync("Gone", true, CancellationToken.None);

        Assert.Equal("Did not find theme Gone", refused.Error);
        Assert.Equal("Did not find theme Gone", service.ReadState().Error);
        Assert.True(service.Revision > revision);
        await service.DismissAsync(CancellationToken.None);
        Assert.Null(service.ReadState().Error);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
