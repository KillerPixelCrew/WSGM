using System;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The Library Tab Manager page in Steam: the overlay's tab builder and tab-order editor projected
///     through <see cref="LibraryManagerService" />, opened from its Quick Access Extensions entry.
/// </summary>
/// <remarks>
///     The page edits a draft of one custom tab and sends the whole tab back to be validated and
///     stored; moves and native-tab hiding are applied one press at a time through the manager's
///     chained order writes, so Steam's strip follows while the user is still pressing. Its
///     publication is gated on the library tabs switch.
/// </remarks>
internal static class SteamLibraryTabsSurface
{
    /// <summary>The state and command namespace.</summary>
    internal const string PatchId = LibraryManagerService.TabsId;

    /// <summary>The route the page is served at.</summary>
    internal const string Route = "/wsgm/library-tabs";

    /// <summary>The renderer that draws it.</summary>
    internal const string Template = "library-tab-manager";

    /// <summary>The name the page's gate registers under.</summary>
    internal const string GateName = "libraryTabManager";

    /// <summary>Installs the page renderer and its state subscription.</summary>
    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "library-tab-manager-v1:steam-page", "Library Tab Manager");

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether state may be published: the host's pages on and the library tabs switch on.</param>
    /// <param name="service">The shared manager behind both surfaces.</param>
    /// <returns>A module borrowing the service; construction does not install its patch.</returns>
    internal static ISteamUiModule Module(Func<bool> enabled, LibraryManagerService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new SteamUiModule("library-tab-manager", [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled,
                    () => new ValueTask<SteamLibraryTabsState?>(service.ReadTabsState()),
                    LibraryManagerJsonContext.Default.SteamLibraryTabsState, () => service.Revision)
            ],
            [
                SteamUiModuleBuilder.Command(PatchId, "refresh", async token =>
                {
                    await service.RefreshAsync(token).ConfigureAwait(false);
                    return SteamUiCommandResult.Applied;
                }),
                SteamUiModuleBuilder.Command<CustomTabConfig>(PatchId, "saveTab", TryReadTab,
                    service.SaveTabAsync, "The tab payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "deleteTab", TryReadId,
                    service.DeleteTabAsync, "The tab id payload is invalid."),
                SteamUiModuleBuilder.Command<(string Key, int Delta)>(PatchId, "moveTab", TryReadMove,
                    (request, token) => service.MoveTabAsync(request.Key, request.Delta, token),
                    "The tab move payload is invalid."),
                SteamUiModuleBuilder.Command<(string Key, bool Hidden)>(PatchId, "setNativeHidden", TryReadHidden,
                    (request, token) => service.SetNativeTabHiddenAsync(request.Key, request.Hidden, token),
                    "The native tab payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "loadTags", async token =>
                {
                    var picker = await service.LoadTagsAsync(token).ConfigureAwait(false);
                    return picker.Error is null ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, picker.Error);
                }),
                SteamUiModuleBuilder.Command(PatchId, "loadCollections", async token =>
                {
                    var picker = await service.LoadCollectionsAsync(token).ConfigureAwait(false);
                    return picker.Error is null ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, picker.Error);
                }),
                SteamUiModuleBuilder.Command(PatchId, "loadGames", async token =>
                {
                    var picker = await service.LoadGamesAsync(token).ConfigureAwait(false);
                    return picker.Error is null ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, picker.Error);
                }),
                .. SteamUiModuleBuilder.BrowseCommands(PatchId,
                    new SteamUiBrowseBindings { Subject = "library tab", Dismiss = service.DismissAsync })
            ]);
    }

    private static bool TryReadId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "id", out id);
    }

    // The editor sends the tab in the shape the state publishes it; the service validates its
    // content, so only the shape is checked here.
    private static bool TryReadTab(JsonElement payload, out CustomTabConfig tab)
    {
        tab = null!;
        if (!SteamUiPayload.HasExactly(payload, 1)
            || !payload.TryGetProperty("tab", out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(element, LibraryManagerJsonContext.Default.CustomTabConfig);
            if (parsed is null)
            {
                return false;
            }

            tab = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadMove(JsonElement payload, out (string Key, int Delta) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || !payload.TryGetProperty("delta", out var delta)
            || !delta.TryGetInt32(out var value)
            || value is not (-1 or 1))
        {
            return false;
        }

        request = (key, value);
        return true;
    }

    private static bool TryReadHidden(JsonElement payload, out (string Key, bool Hidden) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "key", out var key)
            || !SteamUiPayload.TryReadBoolean(payload, "hidden", out var hidden))
        {
            return false;
        }

        request = (key, hidden);
        return true;
    }
}
