using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One boot movie as the Animations page and the overlay show it, listed or in the library.</summary>
/// <param name="Id">The repository's id, or the library's id for a brought file.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">The uploader, or empty.</param>
/// <param name="ThumbnailUrl">A still, or null.</param>
/// <param name="PreviewUrl">The movie, playable in the page, or null.</param>
/// <param name="Description">The post's text.</param>
/// <param name="Likes">How often it was liked.</param>
/// <param name="Downloads">How often it was downloaded.</param>
/// <param name="Updated">When it was last changed, as a date, or empty.</param>
/// <param name="Downloaded">Whether the library holds it.</param>
/// <param name="Custom">Whether it is a file the user brought.</param>
public sealed record SteamAnimationsItem(
    string Id,
    string Name,
    string Author,
    string? ThumbnailUrl,
    string? PreviewUrl,
    string Description,
    int Likes,
    int Downloads,
    string Updated,
    bool Downloaded,
    bool Custom);

/// <summary>One way the Browse tab orders the repository's list.</summary>
/// <param name="Id">What the page sends back.</param>
/// <param name="Label">What it shows, as Animation Changer names it.</param>
public sealed record SteamAnimationsSort(string Id, string Label);

/// <summary>The Browse tab: the repository's list, sorted and searched on the page's behalf.</summary>
/// <param name="Sort">The id of one of <paramref name="Sorts" />.</param>
/// <param name="Sorts">The orders offered, which the page and the overlay draw from.</param>
/// <param name="Search">The search text.</param>
/// <param name="Items">
///     The first of what matches, in the sort's order: a page more with each <c>more</c>. The
///     repository lists thousands, and a card for each stalls Steam's renderer.
/// </param>
/// <param name="Matched">How many match the search in all.</param>
/// <param name="Total">How many the repository lists in all.</param>
/// <param name="Loading">Whether the repository is being asked.</param>
/// <param name="Error">Why the last request failed, or null.</param>
public sealed record SteamAnimationsBrowse(
    string Sort,
    IReadOnlyList<SteamAnimationsSort> Sorts,
    string Search,
    IReadOnlyList<SteamAnimationsItem> Items,
    int Matched,
    int Total,
    bool Loading,
    string? Error);

/// <summary>What the pages know beyond the choice and the lists.</summary>
/// <param name="ShuffleOnStart">Whether the boot movie is picked anew when WSGM starts.</param>
/// <param name="BootVolume">The boot movie's volume in percent of its file's.</param>
/// <param name="LibraryPath">Where the movies are kept.</param>
/// <param name="OverridesPath">Where the override is written, or null without Steam.</param>
/// <param name="RestartNeeded">Whether the override changed since Steam started, so a restart shows it.</param>
public sealed record SteamAnimationsSettings(
    bool ShuffleOnStart,
    int BootVolume,
    string LibraryPath,
    string? OverridesPath,
    bool RestartNeeded);

/// <summary>Everything the Animations page, the Quick Access section and the overlay draw.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>library</c> or <c>settings</c>.</param>
/// <param name="Selected">The library id Big Picture starts with, empty for Steam's own movie.</param>
/// <param name="StockName">What the empty choice is called.</param>
/// <param name="Library">The library, downloads first.</param>
/// <param name="Browse">The repository's list.</param>
/// <param name="Detail">The movie opened for a closer look, or null.</param>
/// <param name="Settings">What else the pages know.</param>
/// <param name="Busy">Whether a download or a copy is running.</param>
/// <param name="Notice">A line worth reading, or null.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Revision">Monotonic observation revision.</param>
public sealed record SteamAnimationsState(
    string ActiveTab,
    string Selected,
    string StockName,
    IReadOnlyList<SteamAnimationsItem> Library,
    SteamAnimationsBrowse Browse,
    SteamAnimationsItem? Detail,
    SteamAnimationsSettings Settings,
    bool Busy,
    string? Notice,
    string? Error,
    long Revision);

/// <summary>Answers the Animations page's commands; the overlay calls the same methods.</summary>
public interface ISteamAnimationsBackend
{
    /// <summary>Shows one of the page's tabs.</summary>
    Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Lists what matches a sort and search, fetching the repository once.</summary>
    Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken cancellationToken);

    /// <summary>Shows the next page of what matches.</summary>
    Task<SteamUiCommandResult> BrowseMoreAsync(CancellationToken cancellationToken);

    /// <summary>Fetches the repository's list again.</summary>
    Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Opens one movie for a closer look.</summary>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the closer look.</summary>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);

    /// <summary>Downloads a listed movie into the library.</summary>
    Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes a movie from the library, and from the boot if it plays there.</summary>
    Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>Starts Big Picture with a movie from the library, or Steam's own with an empty id.</summary>
    Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken);

    /// <summary>Picks the boot movie anew from the library.</summary>
    Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken);

    /// <summary>Turns picking the boot movie anew at WSGM's start on or off.</summary>
    Task<SteamUiCommandResult> SetShuffleOnStartAsync(bool shuffle, CancellationToken cancellationToken);

    /// <summary>Sets the boot movie's volume in percent of its file's.</summary>
    Task<SteamUiCommandResult> SetBootVolumeAsync(int volume, CancellationToken cancellationToken);

    /// <summary>Copies a movie file the user chose into the library.</summary>
    Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Clears the notice and the error.</summary>
    Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken);
}

/// <summary>The Animations page in Steam: the repository's boot movies, the library and the choice.</summary>
public static class SteamAnimationsSurface
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "steam-ui.animations";

    /// <summary>The route this page is served at.</summary>
    public const string Route = "/wsgm/animations";

    /// <summary>The renderer that draws it.</summary>
    public const string Template = "animations";

    /// <summary>The name the page's gate registers under.</summary>
    public const string GateName = "animations";

    /// <summary>The page's tabs, in order.</summary>
    private static readonly string[] Tabs = ["browse", "library", "settings"];

    /// <summary>The sorts the Browse tab offers, the first the default, labelled as Animation Changer names them.</summary>
    public static IReadOnlyList<SteamAnimationsSort> Sorts { get; } =
    [
        new("newest", "Newest"), new("oldest", "Oldest"), new("name", "Alphabetical"),
        new("popular", "Most popular"), new("liked", "Most liked")
    ];

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "setTab", "browse", "more", "refresh", "open", "closeDetail", "download", "delete", "select", "shuffle",
        "setShuffleOnStart", "setBootVolume",
        "addFile", "dismiss"
    ];

    /// <summary>Installs the page renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-animations-v1:steam-page",
        "Animations page",
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
        Func<ValueTask<SteamAnimationsState?>> read,
        Func<long> revision,
        ISteamAnimationsBackend backend,
        string id = "animations")
    {
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, AnimationsJsonContext.Default.SteamAnimationsState, revision)
            ],
            [
                SteamUiModuleBuilder.Command<string>(PatchId, "setTab", TryReadTab,
                    backend.SetTabAsync, "The animations tab payload is invalid."),
                SteamUiModuleBuilder.Command<(string Sort, string Search)>(PatchId, "browse", TryReadBrowse,
                    (request, token) => backend.BrowseAsync(request.Sort, request.Search, token),
                    "The animations browse payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "more", backend.BrowseMoreAsync),
                SteamUiModuleBuilder.Command(PatchId, "refresh", backend.RefreshAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "open", TryReadId,
                    backend.OpenAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "closeDetail", backend.CloseDetailAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "download", TryReadId,
                    backend.DownloadAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "delete", TryReadId,
                    backend.DeleteAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "select", TryReadOptionalId,
                    backend.SelectAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "shuffle", backend.ShuffleAsync),
                SteamUiModuleBuilder.Command<bool>(PatchId, "setShuffleOnStart", TryReadShuffle,
                    backend.SetShuffleOnStartAsync, "The shuffle payload is invalid."),
                SteamUiModuleBuilder.Command<int>(PatchId, "setBootVolume", TryReadVolume,
                    backend.SetBootVolumeAsync, "The volume payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "addFile", TryReadPath,
                    backend.AddFileAsync, "The animation file payload is invalid."),
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

    private static bool TryReadOptionalId(JsonElement payload, out string id)
    {
        return SteamUiPayload.TryReadOnlyOptionalString(payload, "id", out id);
    }

    private static bool TryReadPath(JsonElement payload, out string path)
    {
        return SteamUiPayload.TryReadOnlyString(payload, "path", out path);
    }

    private static bool TryReadShuffle(JsonElement payload, out bool shuffle)
    {
        return SteamUiPayload.TryReadOnlyBoolean(payload, "value", out shuffle);
    }

    private static bool TryReadVolume(JsonElement payload, out int volume)
    {
        volume = 0;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadInt(payload, "value", 0, AnimationOverrides.FullVolume, out volume);
    }

    private static bool TryReadBrowse(JsonElement payload, out (string Sort, string Search) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadString(payload, "sort", out var sort)
            || !SteamUiPayload.TryReadString(payload, "search", out var search))
        {
            return false;
        }

        request = (sort, search);
        return true;
    }
}

/// <summary>Serializer metadata for the page's state.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamAnimationsState))]
internal sealed partial class AnimationsJsonContext : JsonSerializerContext;
