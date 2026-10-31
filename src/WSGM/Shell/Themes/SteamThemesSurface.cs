using System;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The Themes page inside Steam: CSSLoader-compatible themes, browsed, installed and managed.</summary>
internal static class SteamThemesSurface
{
    /// <summary>The state and command namespace.</summary>
    public const string PatchId = "wsgm.themes";

    /// <summary>The route this page is served at.</summary>
    public const string Route = "/wsgm/themes";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "themes";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "themes";

    /// <summary>The page's tabs, in order.</summary>
    private static readonly string[] Tabs = ["browse", "installed", "profiles", "settings"];

    /// <summary>Installs the page renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-themes-v1:steam-page",
        "Themes page");

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether state may be published; patch installation is coordinated separately.</param>
    /// <param name="read">Reads the current model; null skips this publication without retracting the previous state.</param>
    /// <param name="revision">The model's revision, so an unchanged model is not serialized again.</param>
    /// <param name="browse">The page's own store browsing, tab and detail.</param>
    /// <param name="backend">Answers the shared theme owner's operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>A module borrowing its backends and readers; construction does not install its patch.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamThemesState?>> read,
        Func<long> revision,
        IThemeBrowseSession browse,
        ISteamThemesBackend backend,
        string id = "themes")
    {
        ArgumentNullException.ThrowIfNull(browse);
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
                    browse.SetTabAsync, "The themes tab payload is invalid."),
                SteamUiModuleBuilder.Command<BrowseRequest>(PatchId, "browse", TryReadBrowse,
                    (request, token) => browse.BrowseAsync(request.Filter, request.Order, request.Search, token),
                    "The themes browse payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "loadMore", browse.LoadMoreAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "open", TryReadId,
                    browse.OpenAsync, "The theme id payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "closeDetail", browse.CloseDetailAsync),
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
