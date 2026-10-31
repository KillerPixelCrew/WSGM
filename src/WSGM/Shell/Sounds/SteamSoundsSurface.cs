using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The UI sounds page in Steam: installed Audio Loader packs, the DeckThemes store and the choice.</summary>
/// <remarks>
///     <see cref="SoundPackService" /> owns every operation; this surface projects its state and maps the page's
///     commands onto its methods, as the overlay's sounds view does.
/// </remarks>
internal static class SteamSoundsSurface
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "wsgm.sounds";

    /// <summary>The route this page is served at.</summary>
    public const string Route = "/wsgm/sounds";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "sounds";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "sounds";

    /// <summary>Installs the page renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-sounds-v1:steam-page",
        "UI sounds page");

    /// <summary>Declares the page's state and its exact command vocabulary over the session's sound service.</summary>
    /// <param name="enabled">Whether state may be published; patch installation is coordinated separately.</param>
    /// <param name="sounds">The session's sound-pack owner.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>A module borrowing the service; construction does not install its patch.</returns>
    public static ISteamUiModule Module(Func<bool> enabled, SoundPackService sounds, string id = "sounds")
    {
        ArgumentNullException.ThrowIfNull(sounds);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId,
                    enabled,
                    () => new ValueTask<SteamSoundsState?>(Project(sounds.ReadState(), sounds.StateRevision,
                        sounds.BlobUrl)),
                    SoundsJsonContext.Default.SteamSoundsState,
                    () => sounds.StateRevision)
            ],
            [
                SteamUiModuleBuilder.Command<string>(PatchId, "select", TryReadOptionalId,
                    sounds.SelectAsync, "The sound pack id payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "delete", TryReadId,
                    sounds.DeleteAsync, "The sound pack id payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "import", TryReadPath,
                    sounds.ImportAsync, "The sound pack file payload is invalid."),
                SteamUiModuleBuilder.Command<(int Page, string Search)>(PatchId, "browse", TryReadBrowse,
                    (request, token) => sounds.BrowseAsync(request.Page, request.Search, token),
                    "The sound pack browse payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "install", TryReadId,
                    sounds.InstallAsync, "The sound pack listing payload is invalid."),
                SteamUiModuleBuilder.Command<(string Id, string Asset)>(PatchId, "preview", TryReadPreview,
                    (request, token) => sounds.PreviewAsync(request.Id, request.Asset, token),
                    "The sound preview payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "stopPreview", sounds.StopPreviewAsync),
                SteamUiModuleBuilder.Command(PatchId, "refresh", sounds.RefreshAsync)
            ]);
    }

    /// <summary>Projects the service's state into the page's wire shape.</summary>
    /// <param name="state">The service's current state.</param>
    /// <param name="revision">The service's state revision.</param>
    /// <param name="blobUrl">Turns a store blob id into its address.</param>
    /// <returns>The detached page state.</returns>
    internal static SteamSoundsState Project(SoundPackState state, long revision, Func<string, string> blobUrl)
    {
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in state.Packs)
        {
            if (pack.StoreId is { Length: > 0 } storeId)
            {
                installed.TryAdd(storeId, pack.Version);
            }
        }

        var packs = state.Packs.Select(pack =>
        {
            var compatibility = pack.Compatibility;
            return new SteamSoundsPack(
                pack.Id,
                pack.Name,
                pack.Author,
                pack.Version,
                pack.Description,
                pack.StoreId,
                pack.Source,
                pack.Error,
                compatibility?.Summary,
                compatibility?.SupportedResources ?? [],
                [.. compatibility?.MissingResources ?? [], .. compatibility?.IgnoredResources ?? []],
                [
                    .. compatibility?.AssetProblems ?? [],
                    .. (compatibility?.UnknownMappings ?? []).Select(resource => "Unknown client resource: " + resource)
                ],
                pack.Error is null ? pack.Assets : []);
        }).ToArray();
        var listings = state.Listings.Select(item => new SteamSoundsListing(
            item.Id,
            item.DisplayName.Length > 0 ? item.DisplayName : item.Name,
            item.SpecifiedAuthor.Length > 0 ? item.SpecifiedAuthor : item.AuthorName,
            item.Version,
            item.ImageIds.Count > 0 ? blobUrl(item.ImageIds[0]) : null,
            item.DownloadCount,
            item.StarCount,
            item.Updated?.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            installed.GetValueOrDefault(item.Id))).ToArray();
        return new SteamSoundsState(
            state.Selected,
            packs,
            new SteamSoundsBrowse(listings, state.Total, state.Page, ThemeStoreClient.SoundsPageSize),
            state.Busy,
            state.Error,
            state.Compatibility,
            state.Integration,
            revision);
    }

    private static bool TryReadId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "id", out id);
    }

    private static bool TryReadOptionalId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadOnlyOptionalString(payload, "id", out id);
    }

    private static bool TryReadPath(JsonElement payload, out string path)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "path", out path);
    }

    private static bool TryReadBrowse(JsonElement payload, out (int Page, string Search) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadInt(payload, "page", 1, int.MaxValue, out var page)
            || !SteamUiPayload.TryReadString(payload, "search", out var search))
        {
            return false;
        }

        request = (page, search);
        return true;
    }

    private static bool TryReadPreview(JsonElement payload, out (string Id, string Asset) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadString(payload, "id", out var id)
            || !SteamUiPayload.TryReadString(payload, "asset", out var asset)
            || id.Length == 0
            || asset.Length == 0)
        {
            return false;
        }

        request = (id, asset);
        return true;
    }
}
