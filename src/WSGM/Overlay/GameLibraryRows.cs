using System.Collections.Generic;
using System.Linq;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>What the overlay's Game Library view says about the library and each of its titles.</summary>
/// <remarks>
///     Pure, so the wording the view shows is tested without building a window. Every label a title
///     carries is the service's, the same words the Steam page shows; this only arranges them into the
///     overlay's rows.
/// </remarks>
internal static class GameLibraryRows
{
    /// <summary>One title's line in the review list.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>Whether it is selected, what would happen to it, and how it launches.</returns>
    internal static string Describe(GameLibraryEntry entry)
    {
        List<string> parts = [entry.ActionLabel, entry.LaunchLabel];
        if (entry.Selected)
        {
            parts.Insert(0, "Selected");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Whether a title is already in Steam as an entry the library manages.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>True for an imported title, whatever change is waiting for it; never an adoption or a removal.</returns>
    internal static bool InSteam(GameLibraryEntry entry)
    {
        return !entry.Excluded && entry.Action is "Skip" or "Update" or "Artwork" && entry.AppId > 0;
    }

    /// <summary>What a title's artwork would be.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>How many artwork types will have an image, why none were found, and how many were applied.</returns>
    internal static string Artwork(GameLibraryEntry entry)
    {
        if (entry.ArtworkStatus is "failed" or "unavailable" && entry.ArtworkDetail.Length > 0)
        {
            return entry.ArtworkDetail;
        }

        if (entry.ArtworkStatus is "pending" or "loading"
            && entry.Artwork.All(slot => slot.Kind is not ("pick" or "default" or "keep")))
        {
            return "Finding images…";
        }

        // An imported title keeps the images it has, which counts as having one.
        var shown = entry.Artwork.Count(slot => slot.Kind is "pick" or "default" or "keep");
        var line = entry.Artwork.Count == 0
            ? "No artwork"
            : $"{shown} of {entry.Artwork.Count} artwork types have an image";
        return entry.ArtworkApplied is { } applied ? $"{line}, {applied} applied by WSGM" : line;
    }

    /// <summary>One source's line in the overlay's source list.</summary>
    /// <param name="source">The source.</param>
    /// <returns>Whether it is on, and what it found or why it cannot be read.</returns>
    internal static string SourceLine(GameLibrarySource source)
    {
        if (!source.Installed)
        {
            return source.Detail.Length > 0 ? source.Detail : "Not found";
        }

        var found = source.Count >= 0 ? $" · {source.Count} found" : string.Empty;
        return (source.Enabled ? "On" : "Off") + found
                                               + (source.Kind == GameLibrarySourceKinds.Folder
                                                   ? $" · {source.Detail}"
                                                   : string.Empty);
    }

    /// <summary>The home level's summary of the last scan.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>What a sync would do, or a prompt to scan when nothing has been scanned.</returns>
    internal static string Summary(GameLibraryState state)
    {
        if (state.Entries.Count == 0)
        {
            return state.Phase == GameLibraryPhases.Idle
                ? "Scan to see which games can be brought into Steam."
                : "No games were found.";
        }

        var listed = state.Entries.Where(entry => !entry.Excluded).ToList();

        int Count(string action)
        {
            return listed.Count(entry => entry.Action == action);
        }

        List<string> parts =
        [
            $"{Count(nameof(ImportAction.Add))} to add",
            $"{Count(nameof(ImportAction.Update))} to update",
            $"{Count(nameof(ImportAction.Skip))} already imported"
        ];
        AddWhenAny(parts, Count("Artwork"), "with new artwork to save");
        AddWhenAny(parts, Count(nameof(ImportAction.Adopt)), "already in Steam to adopt");
        AddWhenAny(parts, Count(nameof(ImportAction.Remove)), "to remove");
        AddWhenAny(parts, Count(nameof(ImportAction.Conflict)), "edited by hand");
        return string.Join(", ", parts);
    }

    /// <summary>What the launch row offers for a title.</summary>
    /// <param name="entry">The title.</param>
    /// <returns>The row's description.</returns>
    internal static string LaunchChoice(GameLibraryEntry entry)
    {
        if (!entry.Packaged)
        {
            return entry.Routes.Count > 1
                ? "Press to switch to the next route."
                : "This title has one way to launch.";
        }

        if (!entry.CanUseSteamIntegration)
        {
            return "There is no validated way to give this title the Steam overlay.";
        }

        return entry.Mode == "SteamIntegration"
            ? "Press to switch to controller only."
            : entry.RequiresAcknowledgement && !entry.Acknowledged
                ? "This title is multiplayer; switching to the Steam overlay asks you to accept the risk."
                : "Press to switch to the Steam overlay.";
    }

    private static void AddWhenAny(List<string> parts, int count, string label)
    {
        if (count > 0)
        {
            parts.Add($"{count} {label}");
        }
    }
}
