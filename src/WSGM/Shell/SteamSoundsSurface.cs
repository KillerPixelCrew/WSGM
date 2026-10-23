using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One installed sound pack as the UI sounds page and the Customization hub show it.</summary>
/// <param name="Id">The library id, sent back with every command.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">The manifest's author, or empty.</param>
/// <param name="Version">The manifest's version, or empty.</param>
/// <param name="Description">The manifest's description, or empty.</param>
/// <param name="StoreId">The DeckThemes listing it came from, or null for a local archive.</param>
/// <param name="Source">Repository or source text, or empty.</param>
/// <param name="Error">Why the pack cannot be used, or null.</param>
/// <param name="Compatibility">Its coverage of the current Steam resources, or null before a comparison.</param>
/// <param name="Mapped">The Steam resources it replaces.</param>
/// <param name="Defaults">The Steam resources that keep Steam's sound, missing or ignored.</param>
/// <param name="Problems">Missing, unsupported or unknown entries worth reading.</param>
/// <param name="Assets">The files it can preview, relative to the pack.</param>
internal sealed record SteamSoundsPack(
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string? StoreId,
    string Source,
    string? Error,
    string? Compatibility,
    IReadOnlyList<string> Mapped,
    IReadOnlyList<string> Defaults,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Assets);

/// <summary>One DeckThemes Audio Loader listing.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">What the store shows.</param>
/// <param name="Author">The author as the pack names them.</param>
/// <param name="Version">The store's version text.</param>
/// <param name="ImageUrl">Its first screenshot, or null.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Stars">How often it was starred.</param>
/// <param name="Updated">When it was last updated, as a date, or null.</param>
/// <param name="InstalledVersion">The installed copy's version, or null when it is not installed.</param>
internal sealed record SteamSoundsListing(
    string Id,
    string Name,
    string Author,
    string Version,
    string? ImageUrl,
    int Downloads,
    int Stars,
    string? Updated,
    string? InstalledVersion);

/// <summary>The store page last asked for.</summary>
/// <param name="Items">The listings on that page.</param>
/// <param name="Total">How many match in all.</param>
/// <param name="Page">The page, from one, or zero before the store was asked.</param>
/// <param name="PageSize">How many listings a page holds.</param>
internal sealed record SteamSoundsBrowse(IReadOnlyList<SteamSoundsListing> Items, int Total, int Page, int PageSize);

/// <summary>Everything the UI sounds page and the Customization hub draw.</summary>
/// <param name="Selected">The selected pack's id, empty for Steam's own sounds.</param>
/// <param name="Packs">The installed packs.</param>
/// <param name="Browse">The store page last asked for.</param>
/// <param name="Busy">Whether an operation is running.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Compatibility">What the selection does to Steam's sounds.</param>
/// <param name="Integration">What Steam's sound integration is doing.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamSoundsState(
    string Selected,
    IReadOnlyList<SteamSoundsPack> Packs,
    SteamSoundsBrowse Browse,
    bool Busy,
    string? Error,
    string Compatibility,
    string Integration,
    long Revision);

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

    /// <summary>The exact command vocabulary the page and the Customization hub emit.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["select", "delete", "import", "browse", "install", "preview", "stopPreview", "refresh"];

    /// <summary>Installs the page renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-sounds-v1:steam-page",
        "UI sounds page",
        [
            SteamPageProbe.React, SteamPageProbe.Focusable, SteamPageProbe.Fields, SteamPageProbe.Tabs,
            SteamPageProbe.Modal, SteamPageProbe.ShowModal
        ]);

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

/// <summary>Serializer metadata for the page's state.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamSoundsState))]
internal sealed partial class SoundsJsonContext : JsonSerializerContext;
