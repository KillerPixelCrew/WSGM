using System;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     The SD Card Manager page in Steam: the overlay's card manager projected through
///     <see cref="LibraryManagerService" />, opened from its Quick Access Extensions entry.
/// </summary>
/// <remarks>
///     The page carries no card logic of its own: every command names a card by its content id and
///     the service answers it through the same rename, tab, hide and forget paths the overlay uses.
///     Its publication is gated on the card manager switch, so a disabled feature publishes nothing
///     and its entry is absent from the Extensions tab.
/// </remarks>
internal static class SteamCardManagerSurface
{
    /// <summary>The state and command namespace.</summary>
    internal const string PatchId = LibraryManagerService.CardsId;

    /// <summary>The route the page is served at.</summary>
    internal const string Route = "/wsgm/cards";

    /// <summary>The renderer that draws it.</summary>
    internal const string Template = "card-manager";

    /// <summary>The name the page's gate registers under.</summary>
    internal const string GateName = "cardManager";

    /// <summary>Installs the page renderer and its state subscription.</summary>
    internal static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId, GateName, "card-manager-v1:steam-page", "SD Card Manager",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.Modal,
            SteamPageProbe.ShowModal
        ]);

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether state may be published: the host's pages on and the card manager switch on.</param>
    /// <param name="service">The shared manager behind both surfaces.</param>
    /// <returns>A module borrowing the service; construction does not install its patch.</returns>
    internal static ISteamUiModule Module(Func<bool> enabled, LibraryManagerService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new SteamUiModule("card-manager", [Patch],
            [
                SteamUiModuleBuilder.Publication(PatchId, enabled,
                    () => new ValueTask<SteamCardManagerState?>(service.ReadCardState()),
                    LibraryManagerJsonContext.Default.SteamCardManagerState, () => service.Revision)
            ],
            [
                SteamUiModuleBuilder.Command(PatchId, "refresh", async token =>
                {
                    await service.RefreshAsync(token).ConfigureAwait(false);
                    return SteamUiCommandResult.Applied;
                }),
                SteamUiModuleBuilder.Command<(string ContentId, string Name)>(PatchId, "rename", TryReadRename,
                    (request, token) => service.RenameCardAsync(request.ContentId, request.Name, token),
                    "The card rename payload is invalid."),
                SteamUiModuleBuilder.Command<(string ContentId, bool Value)>(PatchId, "setEnabled", TryReadEnabled,
                    (request, token) => service.SetCardEnabledAsync(request.ContentId, request.Value, token),
                    "The card tab payload is invalid."),
                SteamUiModuleBuilder.Command<(string ContentId, bool Value)>(PatchId, "setHidden", TryReadHidden,
                    (request, token) => service.SetCardHiddenAsync(request.ContentId, request.Value, token),
                    "The card visibility payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "forget", TryReadContentId,
                    service.ForgetCardAsync, "The card payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "loadGames", TryReadContentId,
                    async (contentId, token) =>
                    {
                        var games = await service.LoadCardGamesAsync(contentId, token).ConfigureAwait(false);
                        // The ids are listed either way; a library that did not answer is named on the page.
                        return games.Error is null || games.Games.Count > 0
                            ? SteamUiCommandResult.Applied
                            : new SteamUiCommandResult(false, games.Error);
                    }, "The card payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "dismiss", service.DismissAsync)
            ]);
    }

    private static bool TryReadContentId(JsonElement payload, out string contentId)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "contentId", out contentId);
    }

    private static bool TryReadRename(JsonElement payload, out (string ContentId, string Name) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "contentId", out var contentId)
            || !SteamUiPayload.TryReadNonBlankString(payload, "name", out var name))
        {
            return false;
        }

        request = (contentId, name);
        return true;
    }

    private static bool TryReadEnabled(JsonElement payload, out (string ContentId, bool Value) request)
    {
        return TryReadFlag(payload, "enabled", out request);
    }

    private static bool TryReadHidden(JsonElement payload, out (string ContentId, bool Value) request)
    {
        return TryReadFlag(payload, "hidden", out request);
    }

    private static bool TryReadFlag(JsonElement payload, string flag, out (string ContentId, bool Value) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadNonBlankString(payload, "contentId", out var contentId)
            || !SteamUiPayload.TryReadBoolean(payload, flag, out var value))
        {
            return false;
        }

        request = (contentId, value);
        return true;
    }
}
