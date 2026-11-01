using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The Animations page in Steam: the repository's boot movies, the library and the choice.</summary>
internal static class SteamAnimationsSurface
{
    /// <summary>Identity for ownership, state and commands.</summary>
    public const string PatchId = "wsgm.animations";

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

    /// <summary>Installs the page renderer and its state subscription.</summary>
    public static ISteamUiPatch Patch { get; } = SteamPagePatch.Create(
        PatchId,
        GateName,
        "steam-animations-v1:steam-page",
        "Animations page");

    /// <summary>Declares the page's state and its exact command vocabulary.</summary>
    /// <param name="enabled">Whether state may be published; patch installation is coordinated separately.</param>
    /// <param name="read">Reads the current model; null skips this publication without retracting the previous state.</param>
    /// <param name="revision">The model's revision, so an unchanged model is not serialized again.</param>
    /// <param name="browse">The page's own tab, listing and detail.</param>
    /// <param name="backend">Answers the shared animation owner's operations.</param>
    /// <param name="id">Module identity for diagnostics.</param>
    /// <returns>A module borrowing its backends and readers; construction does not install its patch.</returns>
    public static ISteamUiModule Module(
        Func<bool> enabled,
        Func<ValueTask<SteamAnimationsState?>> read,
        Func<long> revision,
        IAnimationBrowseSession browse,
        ISteamAnimationsBackend backend,
        string id = "animations")
    {
        ArgumentNullException.ThrowIfNull(browse);
        ArgumentNullException.ThrowIfNull(backend);
        return new SteamUiModule(
            id,
            [Patch],
            [
                SteamUiModuleBuilder.Publication(
                    PatchId, enabled, read, AnimationsJsonContext.Default.SteamAnimationsState, revision)
            ],
            [
                .. SteamUiModuleBuilder.BrowseCommands<(string Sort, string Search)>(PatchId,
                    new SteamUiBrowseBindings
                    {
                        Subject = "animations",
                        Tabs = Tabs,
                        SetTab = browse.SetTabAsync,
                        More = browse.BrowseMoreAsync,
                        Open = browse.OpenAsync,
                        CloseDetail = browse.CloseDetailAsync,
                        Dismiss = backend.DismissAsync
                    },
                    TryReadBrowse,
                    (request, token) => browse.BrowseAsync(request.Sort, request.Search, token)),
                SteamUiModuleBuilder.Command(PatchId, "refresh", backend.RefreshAsync),
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
                    backend.AddFileAsync, "The animation file payload is invalid.")
            ]);
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
