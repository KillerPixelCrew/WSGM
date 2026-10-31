using System;
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
        if (entry.Unavailable)
        {
            parts.Add(entry.AvailabilityLabel);
        }

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
        return (source.Checked ? "On" : "Off") + found
                                               + (source.Kind == LibrarySourceKind.Folder
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

    /// <summary>Whether a card library has something for the user to look at or do.</summary>
    /// <param name="storage">The library.</param>
    /// <returns>True unless it is connected with nothing to repair or accept.</returns>
    internal static bool StorageNeedsAttention(GameLibraryStorage storage)
    {
        return storage.CanRepair || storage.CanAcceptBinding
               || !string.Equals(storage.Status, "Connected", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The home level's line for the Storage row.</summary>
    /// <param name="shortcuts">Every tracked shortcut.</param>
    /// <param name="storages">Every card library.</param>
    /// <param name="checkedOnce">Whether the shortcuts have been checked yet.</param>
    /// <returns>How many shortcuts are tracked and how many cannot start, or that the check is running.</returns>
    internal static string StorageSummary(IReadOnlyList<ShortcutStatus> shortcuts,
        IReadOnlyList<GameLibraryStorage> storages, bool checkedOnce)
    {
        List<string> parts = [];
        if (!checkedOnce)
        {
            parts.Add("Checking shortcuts");
        }
        else
        {
            parts.Add(shortcuts.Count == 1 ? "1 shortcut" : $"{shortcuts.Count} shortcuts");
            AddWhenAny(parts, shortcuts.Count(shortcut => !shortcut.Available), "unavailable");
        }

        var attention = storages.Count(StorageNeedsAttention);
        if (attention > 0)
        {
            parts.Add(attention == 1 ? "1 card needs attention" : $"{attention} cards need attention");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One shortcut's line in its location group: its state, whose it is, and what to do.</summary>
    /// <param name="shortcut">The tracked shortcut.</param>
    /// <returns>The host's label, then the detail when it cannot start.</returns>
    internal static string ShortcutLine(ShortcutStatus shortcut)
    {
        List<string> parts = [shortcut.Label];
        if (!shortcut.Owned)
        {
            parts.Add("Your shortcut");
        }

        if (!shortcut.Available && shortcut.Detail.Length > 0)
        {
            parts.Add(shortcut.Detail);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A card library's line: its status and how many titles it lists.</summary>
    /// <param name="storage">The library.</param>
    /// <returns>The host's status followed by the title count.</returns>
    internal static string StorageLine(GameLibraryStorage storage)
    {
        return storage.Status + " · " + (storage.TitleCount == 1 ? "1 title" : $"{storage.TitleCount} titles");
    }

    /// <summary>Groups the tracked shortcuts by where their files live, with each card library as its own group.</summary>
    /// <param name="shortcuts">Every tracked shortcut, already ordered by location and name.</param>
    /// <param name="storages">Every card library, including disconnected ones with no shortcut in Steam.</param>
    /// <returns>One group per card library first, then one per other location, each by name.</returns>
    internal static IReadOnlyList<StorageGroup> StorageGroups(IReadOnlyList<ShortcutStatus> shortcuts,
        IReadOnlyList<GameLibraryStorage> storages)
    {
        List<StorageGroup> groups = [];
        HashSet<ShortcutStatus> placed = [];
        foreach (var storage in storages)
        {
            var members = shortcuts.Where(shortcut => string.Equals(shortcut.LibraryId, storage.Id,
                    StringComparison.OrdinalIgnoreCase)
                || shortcut.LibraryId.Length == 0 && shortcut.Location == storage.Name).ToArray();
            placed.UnionWith(members);
            groups.Add(new StorageGroup(storage.Name, storage, members));
        }

        foreach (var location in shortcuts.Where(shortcut => !placed.Contains(shortcut))
                     .GroupBy(shortcut => shortcut.Location, StringComparer.CurrentCultureIgnoreCase))
        {
            groups.Add(new StorageGroup(location.Key.Length > 0 ? location.Key : "Unknown location", null,
                [.. location]));
        }

        return [.. groups.OrderBy(group => group.Name, StringComparer.CurrentCulture)];
    }

    private static void AddWhenAny(List<string> parts, int count, string label)
    {
        if (count > 0)
        {
            parts.Add($"{count} {label}");
        }
    }
}
