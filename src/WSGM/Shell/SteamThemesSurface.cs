using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One installed theme as the Themes page and the overlay show it.</summary>
/// <param name="Id">The store id, or the name.</param>
/// <param name="Name">The identity among installed themes, sent back with every command.</param>
/// <param name="DisplayName">The name the user sees.</param>
/// <param name="Version">The installed version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="Hidden">Whether it is kept off the Quick Access section.</param>
/// <param name="Status">
///     <c>installed</c>, <c>outdated</c>, <c>local</c> (the store does not list it) or <c>unknown</c>
///     (not checked yet).
/// </param>
/// <param name="LatestVersion">The store's version when it differs, or null.</param>
/// <param name="Patches">Its patches and their values.</param>
/// <param name="Dependencies">The names of the themes it needs.</param>
public sealed record SteamThemesInstalled(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Author,
    bool Enabled,
    bool Hidden,
    string Status,
    string? LatestVersion,
    IReadOnlyList<ThemePatchSnapshot> Patches,
    IReadOnlyList<string> Dependencies);

/// <summary>One store listing as the page draws it.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Version">The store's version text.</param>
/// <param name="Target">The part of Steam it restyles.</param>
/// <param name="Targets">Every target the store tags it with.</param>
/// <param name="Author">The author as the theme names them.</param>
/// <param name="ImageUrl">Its first screenshot, or null.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Stars">How often it was starred.</param>
/// <param name="Updated">When it was last updated, as a date, or null.</param>
/// <param name="LocalStatus"><c>none</c>, <c>installed</c> or <c>outdated</c>.</param>
public sealed record SteamThemesStoreItem(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Target,
    IReadOnlyList<string> Targets,
    string Author,
    string? ImageUrl,
    int Downloads,
    int Stars,
    string? Updated,
    string LocalStatus);

/// <summary>The Browse tab: what was asked for and what the store answered.</summary>
/// <param name="Filter">The target shown, or <c>All</c>.</param>
/// <param name="Order">The order, one of the store's names.</param>
/// <param name="Search">The search text.</param>
/// <param name="Filters">Every target and its count, once the store has been asked.</param>
/// <param name="Orders">The store's order names.</param>
/// <param name="Items">The listings loaded so far.</param>
/// <param name="Total">How many match in all.</param>
/// <param name="Page">The last page loaded.</param>
/// <param name="Loading">Whether a request is in flight.</param>
/// <param name="Error">Why the last request failed, or null.</param>
public sealed record SteamThemesBrowse(
    string Filter,
    string Order,
    string Search,
    IReadOnlyDictionary<string, int> Filters,
    IReadOnlyList<string> Orders,
    IReadOnlyList<SteamThemesStoreItem> Items,
    int Total,
    int Page,
    bool Loading,
    string? Error);

/// <summary>A theme another needs, as the detail view lists it.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Installed">Whether it is already installed.</param>
public sealed record SteamThemesDependency(string Id, string Name, string DisplayName, bool Installed);

/// <summary>One theme opened from the store.</summary>
/// <param name="Item">Its listing.</param>
/// <param name="Description">The author's description.</param>
/// <param name="ImageUrls">Its screenshots.</param>
/// <param name="Dependencies">The themes it needs.</param>
/// <param name="Loading">Whether the details are still being read.</param>
/// <param name="Error">Why they could not be read, or null.</param>
public sealed record SteamThemesDetail(
    SteamThemesStoreItem Item,
    string Description,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<SteamThemesDependency> Dependencies,
    bool Loading,
    string? Error);

/// <summary>The Settings tab.</summary>
/// <param name="Enabled">Whether enabled themes are installed into Steam.</param>
/// <param name="TranslationsBranch">Which translation table is fetched.</param>
/// <param name="SteamBeta">Whether Steam is on a beta branch.</param>
/// <param name="Translations">How many class names the table maps.</param>
/// <param name="TranslationsFetched">When the table was last fetched, or null.</param>
/// <param name="ThemesPath">The themes folder.</param>
/// <param name="SteamLink">What became of Steam's <c>themes_custom</c> link.</param>
public sealed record SteamThemesSettings(
    bool Enabled,
    string TranslationsBranch,
    bool SteamBeta,
    int Translations,
    string? TranslationsFetched,
    string ThemesPath,
    string SteamLink);

/// <summary>The whole model behind the Themes page, the Quick Access section and the overlay.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>installed</c>, <c>profiles</c> or <c>settings</c>.</param>
/// <param name="Themes">The installed themes that are not profiles, by display name.</param>
/// <param name="Presets">The profiles.</param>
/// <param name="SelectedPreset">The enabled profile's name, or empty.</param>
/// <param name="Browse">The Browse tab.</param>
/// <param name="Detail">The theme opened from the store, or null.</param>
/// <param name="Settings">The Settings tab.</param>
/// <param name="Errors">The folders the loader refused.</param>
/// <param name="Busy">Whether an install, update or reload is running.</param>
/// <param name="Notice">Something the user should read, or null.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Updates">How many installed themes the store has a newer version of.</param>
/// <param name="Revision">Monotonic observation revision.</param>
public sealed record SteamThemesState(
    string ActiveTab,
    IReadOnlyList<SteamThemesInstalled> Themes,
    IReadOnlyList<SteamThemesInstalled> Presets,
    string SelectedPreset,
    SteamThemesBrowse Browse,
    SteamThemesDetail? Detail,
    SteamThemesSettings Settings,
    IReadOnlyList<ThemeLoadError> Errors,
    bool Busy,
    string? Notice,
    string? Error,
    int Updates,
    long Revision);

/// <summary>Answers the Themes page's commands; the overlay calls the same methods.</summary>
public interface ISteamThemesBackend
{
    /// <summary>Shows one of the page's tabs.</summary>
    Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Asks the store for the first page matching a filter, order and search.</summary>
    Task<SteamUiCommandResult> BrowseAsync(string filter, string order, string search,
        CancellationToken cancellationToken);

    /// <summary>Asks the store for the next page of the current listing.</summary>
    Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken);

    /// <summary>Opens one store listing's details.</summary>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the details.</summary>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);

    /// <summary>Installs a theme from the store, with the dependencies it lacks.</summary>
    Task<SteamUiCommandResult> InstallAsync(string id, CancellationToken cancellationToken);

    /// <summary>Installs the store's newer version of an installed theme.</summary>
    Task<SteamUiCommandResult> UpdateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Installs every newer version the store has.</summary>
    Task<SteamUiCommandResult> UpdateAllAsync(CancellationToken cancellationToken);

    /// <summary>Turns a theme off and removes its folder.</summary>
    Task<SteamUiCommandResult> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Turns a theme on or off.</summary>
    Task<SteamUiCommandResult> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken);

    /// <summary>Chooses a patch's option.</summary>
    Task<SteamUiCommandResult> SetPatchAsync(string theme, string patch, string value,
        CancellationToken cancellationToken);

    /// <summary>Sets a component's colour or image.</summary>
    Task<SteamUiCommandResult> SetComponentAsync(
        string theme, string patch, string component, string value, CancellationToken cancellationToken);

    /// <summary>Turns a profile on, or every profile off with an empty name.</summary>
    Task<SteamUiCommandResult> SetProfileAsync(string name, CancellationToken cancellationToken);

    /// <summary>Saves the enabled themes and their patch values as a profile.</summary>
    Task<SteamUiCommandResult> CreateProfileAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the themes folder again and checks for updates.</summary>
    Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Keeps a theme off the Quick Access section, or shows it there again.</summary>
    Task<SteamUiCommandResult> SetHiddenAsync(string name, bool hidden, CancellationToken cancellationToken);

    /// <summary>Changes one of the Settings tab's values.</summary>
    Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value, CancellationToken cancellationToken);

    /// <summary>Clears the notice and the error.</summary>
    Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken);
}

/// <summary>The Themes page inside Steam: CSSLoader-compatible themes, browsed, installed and managed.</summary>
public static class SteamThemesSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "steam-ui.themes";

    /// <summary>The route this page is served at.</summary>
    public const string Route = "/wsgm/themes";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "themes";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "themes";

    /// <summary>The page's tabs, in order.</summary>
    private static readonly string[] Tabs = ["browse", "installed", "profiles", "settings"];

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "setTab", "browse", "loadMore", "open", "closeDetail", "install", "update", "updateAll", "delete",
        "setEnabled", "setPatch", "setComponent", "setProfile", "createProfile", "refresh", "setHidden", "setSetting",
        "dismiss"
    ];

    /// <summary>Installs the page renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-themes-v1:steam-page",
        "Themes page",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.Tabs,
            SteamPageProbe.Modal, SteamPageProbe.ShowModal
        ]);

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether the page may be installed and published.</param>
    /// <param name="read">Reads the current page model.</param>
    /// <param name="revision">The model's revision, so an unchanged model is not serialized again.</param>
    /// <param name="backend">Answers user operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>The module.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamThemesState?>> read,
        Func<long> revision,
        ISteamThemesBackend backend,
        string id = "themes")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, ThemesJsonContext.Default.SteamThemesState, revision)
            ],
            [
                SteamUiModuleBuilder.Command<string>(PatchId, "setTab", TryReadTab,
                    backend.SetTabAsync, "The themes tab payload is invalid."),
                SteamUiModuleBuilder.Command<BrowseRequest>(PatchId, "browse", TryReadBrowse,
                    (request, token) => backend.BrowseAsync(request.Filter, request.Order, request.Search, token),
                    "The themes browse payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "loadMore", backend.LoadMoreAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "open", TryReadId,
                    backend.OpenAsync, "The theme id payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "closeDetail", backend.CloseDetailAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "install", TryReadId,
                    backend.InstallAsync, "The theme id payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "update", TryReadName,
                    backend.UpdateAsync, "The theme name payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "updateAll", backend.UpdateAllAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "delete", TryReadName,
                    backend.DeleteAsync, "The theme name payload is invalid."),
                SteamUiModuleBuilder.Command<(string Name, bool Enabled)>(PatchId, "setEnabled", TryReadEnabled,
                    (request, token) => backend.SetEnabledAsync(request.Name, request.Enabled, token),
                    "The theme switch payload is invalid."),
                SteamUiModuleBuilder.Command<PatchRequest>(PatchId, "setPatch", TryReadPatch,
                    (request, token) => backend.SetPatchAsync(request.Theme, request.Patch, request.Value, token),
                    "The theme patch payload is invalid."),
                SteamUiModuleBuilder.Command<ComponentRequest>(PatchId, "setComponent", TryReadComponent,
                    (request, token) => backend.SetComponentAsync(
                        request.Theme, request.Patch, request.Component, request.Value, token),
                    "The theme component payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "setProfile", TryReadOptionalName,
                    backend.SetProfileAsync, "The profile payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "createProfile", TryReadName,
                    backend.CreateProfileAsync, "The profile name payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "refresh", backend.RefreshAsync),
                SteamUiModuleBuilder.Command<(string Name, bool Hidden)>(PatchId, "setHidden", TryReadHidden,
                    (request, token) => backend.SetHiddenAsync(request.Name, request.Hidden, token),
                    "The theme visibility payload is invalid."),
                SteamUiModuleBuilder.Command<(string Key, JsonElement Value)>(PatchId, "setSetting", TryReadSetting,
                    (request, token) => backend.SetSettingAsync(request.Key, request.Value, token),
                    "The themes setting payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "dismiss", backend.DismissAsync)
            ]);
    }

    private static bool TryReadTab(JsonElement payload, out string tab)
    {
        return SteamUiPayload.TryReadOnlyChoice(payload, "tab", Tabs, out tab);
    }

    private static bool TryReadId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "id", out id);
    }

    private static bool TryReadName(JsonElement payload, out string name)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "name", out name);
    }

    private static bool TryReadOptionalName(JsonElement payload, out string name)
    {
        return SteamUiPayload.TryReadOnlyOptionalString(payload, "name", out name);
    }

    private static bool TryReadBrowse(JsonElement payload, out BrowseRequest request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadString(payload, "filter", out var filter)
            || !SteamUiPayload.TryReadString(payload, "order", out var order)
            || !SteamUiPayload.TryReadString(payload, "search", out var search))
        {
            return false;
        }

        request = new BrowseRequest(filter, order, search);
        return true;
    }

    private static bool TryReadEnabled(JsonElement payload, out (string Name, bool Enabled) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "name", out var name)
            || !SteamUiPayload.TryReadBoolean(payload, "enabled", out var enabled))
        {
            return false;
        }

        request = (name, enabled);
        return true;
    }

    private static bool TryReadHidden(JsonElement payload, out (string Name, bool Hidden) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "name", out var name)
            || !SteamUiPayload.TryReadBoolean(payload, "hidden", out var hidden))
        {
            return false;
        }

        request = (name, hidden);
        return true;
    }

    private static bool TryReadPatch(JsonElement payload, out PatchRequest request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadNonBlankString(payload, "theme", out var theme)
            || !SteamUiPayload.TryReadNonBlankString(payload, "patch", out var patch)
            || !SteamUiPayload.TryReadString(payload, "value", out var value))
        {
            return false;
        }

        request = new PatchRequest(theme, patch, value);
        return true;
    }

    private static bool TryReadComponent(JsonElement payload, out ComponentRequest request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 4)
            || !SteamUiPayload.TryReadNonBlankString(payload, "theme", out var theme)
            || !SteamUiPayload.TryReadNonBlankString(payload, "patch", out var patch)
            || !SteamUiPayload.TryReadNonBlankString(payload, "component", out var component)
            || !SteamUiPayload.TryReadString(payload, "value", out var value))
        {
            return false;
        }

        request = new ComponentRequest(theme, patch, component, value);
        return true;
    }

    private static bool TryReadSetting(JsonElement payload, out (string Key, JsonElement Value) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || !payload.TryGetProperty("value", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.String
                or JsonValueKind.Number))
        {
            return false;
        }

        request = (key, value.Clone());
        return true;
    }

    /// <summary>One request for a store page.</summary>
    private readonly record struct BrowseRequest(string Filter, string Order, string Search);

    /// <summary>One request to choose a patch's option.</summary>
    private readonly record struct PatchRequest(string Theme, string Patch, string Value);

    /// <summary>One request to set a component's value.</summary>
    private readonly record struct ComponentRequest(string Theme, string Patch, string Component, string Value);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamThemesState))]
internal sealed partial class ThemesJsonContext : JsonSerializerContext;
