using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One animation as the Animations page and the overlay show it, listed or in the library.</summary>
/// <param name="Id">The repository's id, or the library's id for a brought file.</param>
/// <param name="Name">The name the user sees.</param>
/// <param name="Author">The uploader, or empty.</param>
/// <param name="Target"><c>boot</c>, <c>suspend</c> or <c>any</c>.</param>
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
    string Target,
    string? ThumbnailUrl,
    string? PreviewUrl,
    string Description,
    int Likes,
    int Downloads,
    string Updated,
    bool Downloaded,
    bool Custom);

/// <summary>The Browse tab: the repository's list, filtered, sorted and searched on the page's behalf.</summary>
/// <param name="Type"><c>all</c>, <c>boot</c> or <c>suspend</c>.</param>
/// <param name="Sort">One of <see cref="SteamAnimationsSurface.Sorts" />.</param>
/// <param name="Search">The search text.</param>
/// <param name="Items">What matches, in the sort's order.</param>
/// <param name="Total">How many the repository lists in all.</param>
/// <param name="Loading">Whether the repository is being asked.</param>
/// <param name="Error">Why the last request failed, or null.</param>
/// <param name="Fetched">When the list was last fetched, or null.</param>
public sealed record SteamAnimationsBrowse(
    string Type,
    string Sort,
    string Search,
    IReadOnlyList<SteamAnimationsItem> Items,
    int Total,
    bool Loading,
    string? Error,
    string? Fetched);

/// <summary>What the pages know beyond the slots and the lists.</summary>
/// <param name="ShuffleOnStart">Whether every slot is picked anew when WSGM starts.</param>
/// <param name="LibraryPath">Where the movies are kept.</param>
/// <param name="OverridesPath">Where the overrides are written, or null without Steam.</param>
/// <param name="RestartNeeded">Whether an override changed since Steam started, so a restart shows it.</param>
public sealed record SteamAnimationsSettings(
    bool ShuffleOnStart,
    string LibraryPath,
    string? OverridesPath,
    bool RestartNeeded);

/// <summary>Everything the Animations page, the Quick Access section and the overlay draw.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>library</c> or <c>settings</c>.</param>
/// <param name="Slots">The library id per slot, empty for Steam's own movie.</param>
/// <param name="Library">The library, downloads first.</param>
/// <param name="Browse">The repository's list.</param>
/// <param name="Detail">The animation opened for a closer look, or null.</param>
/// <param name="Settings">What else the pages know.</param>
/// <param name="Busy">Whether a download or a copy is running.</param>
/// <param name="Notice">A line worth reading, or null.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Revision">Monotonic observation revision.</param>
public sealed record SteamAnimationsState(
    string ActiveTab,
    IReadOnlyDictionary<string, string> Slots,
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

    /// <summary>Lists what matches a type, sort and search, fetching the repository once.</summary>
    Task<SteamUiCommandResult> BrowseAsync(string type, string sort, string search,
        CancellationToken cancellationToken);

    /// <summary>Fetches the repository's list again.</summary>
    Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Opens one animation for a closer look.</summary>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the closer look.</summary>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);

    /// <summary>Downloads a listed animation into the library.</summary>
    Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes an animation from the library, and from any slot playing it.</summary>
    Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>Plays an animation in a slot, or Steam's own with an empty id.</summary>
    Task<SteamUiCommandResult> SetSlotAsync(string slot, string id, CancellationToken cancellationToken);

    /// <summary>Picks every slot anew from the library.</summary>
    Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken);

    /// <summary>Changes one of WSGM's own settings.</summary>
    Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value, CancellationToken cancellationToken);

    /// <summary>Copies a movie file the user chose into the library.</summary>
    Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Clears the notice and the error.</summary>
    Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken);
}

/// <summary>The Animations page in Steam: the repository's list, the library and the slots.</summary>
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

    private const int MaximumId = 128;
    private const int MaximumPath = 1024;

    /// <summary>The sorts the Browse tab offers, as Animation Changer names them.</summary>
    public static IReadOnlyList<string> Sorts { get; } =
        ["Newest", "Oldest", "Alphabetical", "Most popular", "Most liked"];

    /// <summary>The types the Browse tab offers.</summary>
    public static IReadOnlyList<string> Types { get; } = ["all", AnimationTargets.Boot, AnimationTargets.Suspend];

    /// <summary>The exact command vocabulary the page emits.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        "setTab", "browse", "refresh", "open", "closeDetail", "download", "delete", "setSlot", "shuffle", "setSetting",
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
                SteamUiModuleBuilder.Command<BrowseRequest>(PatchId, "browse", TryReadBrowse,
                    (request, token) => backend.BrowseAsync(request.Type, request.Sort, request.Search, token),
                    "The animations browse payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "refresh", backend.RefreshAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "open", TryReadId,
                    backend.OpenAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "closeDetail", backend.CloseDetailAsync),
                SteamUiModuleBuilder.Command<string>(PatchId, "download", TryReadId,
                    backend.DownloadAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "delete", TryReadId,
                    backend.DeleteAsync, "The animation id payload is invalid."),
                SteamUiModuleBuilder.Command<(string Slot, string Id)>(PatchId, "setSlot", TryReadSlot,
                    (request, token) => backend.SetSlotAsync(request.Slot, request.Id, token),
                    "The animation slot payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "shuffle", backend.ShuffleAsync),
                SteamUiModuleBuilder.Command<(string Key, JsonElement Value)>(PatchId, "setSetting", TryReadSetting,
                    (request, token) => backend.SetSettingAsync(request.Key, request.Value, token),
                    "The animations setting payload is invalid."),
                SteamUiModuleBuilder.Command<string>(PatchId, "addFile", TryReadPath,
                    backend.AddFileAsync, "The animation file payload is invalid."),
                SteamUiModuleBuilder.Command(PatchId, "dismiss", backend.DismissAsync)
            ]);
    }

    private static bool TryReadTab(JsonElement payload, out string tab)
    {
        tab = string.Empty;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadBoundedString(payload, "tab", 16, out tab)
               && tab is "browse" or "library" or "settings";
    }

    private static bool TryReadId(JsonElement payload, out string id)
    {
        id = string.Empty;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadBoundedString(payload, "id", MaximumId, out id);
    }

    private static bool TryReadPath(JsonElement payload, out string path)
    {
        path = string.Empty;
        return SteamUiPayload.HasExactly(payload, 1)
               && SteamUiPayload.TryReadBoundedString(payload, "path", MaximumPath, out path);
    }

    private static bool TryReadBrowse(JsonElement payload, out BrowseRequest request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadString(payload, "type", 16, out var type)
            || !SteamUiPayload.TryReadString(payload, "sort", 32, out var sort)
            || !SteamUiPayload.TryReadString(payload, "search", 128, out var search))
        {
            return false;
        }

        request = new BrowseRequest(type, sort, search);
        return true;
    }

    private static bool TryReadSlot(JsonElement payload, out (string Slot, string Id) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "slot", 16, out var slot)
            || !AnimationSlots.IsSlot(slot)
            || !SteamUiPayload.TryReadString(payload, "id", MaximumId, out var id))
        {
            return false;
        }

        request = (slot, id);
        return true;
    }

    private static bool TryReadSetting(JsonElement payload, out (string Key, JsonElement Value) request)
    {
        request = default;
        if (!SteamUiPayload.HasExactly(payload, 2)
            || !SteamUiPayload.TryReadBoundedString(payload, "key", 64, out var key)
            || !payload.TryGetProperty("value", out var value))
        {
            return false;
        }

        request = (key, value.Clone());
        return true;
    }

    private readonly record struct BrowseRequest(string Type, string Sort, string Search);
}

/// <summary>Serializer metadata for the page's state.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamAnimationsState))]
internal sealed partial class AnimationsJsonContext : JsonSerializerContext;
