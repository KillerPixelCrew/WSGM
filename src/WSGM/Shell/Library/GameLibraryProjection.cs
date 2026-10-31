using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Projects the Game Library's review into the state both surfaces render: the sources, every entry
///     with its labels, groups, routes and artwork slots, and the run's progress. Pure: every input is
///     handed over, and nothing here reads or changes the service.
/// </summary>
internal static class GameLibraryProjection
{
    /// <summary>Builds the state both surfaces render.</summary>
    /// <param name="settings">The library's settings in force.</param>
    /// <param name="sources">The sources a scan can read now.</param>
    /// <param name="availability">What each source's last detection found.</param>
    /// <param name="counts">How many titles each source's last scan found; a source not read in full is absent.</param>
    /// <param name="entries">Every entry in the review.</param>
    /// <param name="artwork">The artwork stage, or null to offer only the sources' own images.</param>
    /// <param name="tracked">The availability tracker's latest result.</param>
    /// <param name="phase">One of the <see cref="GameLibraryPhases" /> values.</param>
    /// <param name="progress">How many selected changes the run applied so far.</param>
    /// <param name="progressTotal">How many selected changes the run applies.</param>
    /// <param name="launcher">The packaged-game launcher, or null when it is missing.</param>
    /// <param name="busy">Whether a scan or an apply runs.</param>
    /// <param name="notes">What the last scan or apply had to say that is not an error.</param>
    /// <param name="error">Why the last run failed, or null.</param>
    /// <param name="revision">The revision the state carries.</param>
    /// <returns>A detached state.</returns>
    internal static GameLibraryState BuildState(
        GameLibraryConfig settings,
        IReadOnlyList<ILibrarySource> sources,
        IReadOnlyDictionary<string, SourceAvailability> availability,
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyCollection<GameLibraryEntryState> entries,
        GameLibraryArtwork? artwork,
        ShortcutAvailabilitySnapshot tracked,
        string phase,
        int progress,
        int progressTotal,
        string? launcher,
        bool busy,
        IReadOnlyList<string> notes,
        string? error,
        long revision)
    {
        var names = GameLibraryScanner.SourceNames(sources);
        var disabled = new HashSet<string>(settings.DisabledSources, StringComparer.OrdinalIgnoreCase);
        var projected = entries
            .OrderBy(entry => entry.Plan.Name, StringComparer.CurrentCulture)
            .Select(entry => Project(entry, names, settings.ArtworkPreference, artwork, tracked))
            .ToList();
        List<GameLibrarySource> published =
        [
            .. sources.Select(source =>
            {
                var found = availability.TryGetValue(source.Id, out var detected)
                    ? detected
                    : new SourceAvailability(false, "Checking…");
                return new GameLibrarySource(
                    source.Id,
                    source.DisplayName,
                    source.Kind,
                    found.Installed,
                    !disabled.Contains(source.Id),
                    found.Detail,
                    counts.TryGetValue(source.Id, out var count) ? count : -1,
                    !disabled.Contains(source.Id) && (source.Kind != LibrarySourceKind.Launcher || found.Installed),
                    source.Kind != LibrarySourceKind.Launcher || found.Installed);
            })
        ];

        return new GameLibraryState(
            published,
            [.. published.Where(source => source is { Installed: true, Enabled: true }).Select(source => source.Name)],
            phase,
            projected,
            projected.Count(entry => entry.Selected),
            progress,
            progressTotal,
            launcher is not null,
            launcher is null && entries.Any(entry => entry.Packaged && entry.Editable)
                ? "The packaged-game launcher is missing from this install, so Xbox titles cannot be imported."
                : null,
            busy,
            notes.Count == 0 ? null : string.Join(" ", notes),
            error,
            settings.ArtworkPreference.ToString(),
            settings.CreateCollections,
            revision,
            [.. settings.RomSources.Select(source => source.Copy())],
            [.. settings.ManualSources.Select(source => source.Copy())],
            StorageLibraries: tracked.Libraries,
            Shortcuts: tracked.Shortcuts,
            StorageChecked: tracked.Checked);
    }

    /// <summary>The stand-in discovery for a title whose source no longer lists it.</summary>
    /// <param name="entry">The title's plan entry.</param>
    /// <param name="record">What WSGM recorded for it, if anything.</param>
    /// <returns>A discovery carrying the plan entry's own identity.</returns>
    internal static DiscoveredGame Placeholder(ImportPlanEntry entry, ImportedEntry? record)
    {
        // A title that is no longer installed has no discovery record, so it stands in with its plan
        // entry's own identity: the source it came from, not an assumed one. Whether it launched
        // through the packaged launcher is the plan's (its record's) to say, not this stand-in's.
        if (record?.Content is { } content)
        {
            return DiscoveredGame.Command(entry.Source, entry.Key, entry.Name, content.BackingPath.AbsolutePath,
            [
                new ShortcutRoute(entry.Route.Length > 0 ? entry.Route : "managed", "Managed content", "", "", "",
                    entry.Reason, ManagedId: content.Id)
            ], content.Copy());
        }

        return new DiscoveredGame(entry.Source, entry.Key, entry.Name, string.Empty,
            new GameLaunch("Not installed", false, entry.Reason),
            MultiplayerVerdict.Unknown, string.Empty, false, [], []);
    }

    /// <summary>One entry as both surfaces show it.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="names">Source display names by id.</param>
    /// <param name="preference">Which kind of artwork leads.</param>
    /// <param name="artwork">The artwork stage, or null.</param>
    /// <param name="tracked">The availability tracker's latest result.</param>
    /// <returns>The published entry.</returns>
    internal static GameLibraryEntry Project(GameLibraryEntryState entry, Dictionary<string, string> names,
        ArtworkPreference preference, GameLibraryArtwork? artwork, ShortcutAvailabilitySnapshot tracked)
    {
        var progress = Progress(entry, artwork);
        var route = entry.Game.CommandRoutes.FirstOrDefault(candidate => candidate.Id == entry.Route);
        var status = StatusOf(entry, tracked);
        return new GameLibraryEntry(
            entry.Id,
            entry.Plan.Name,
            names.GetValueOrDefault(entry.Plan.Source, entry.Plan.Source),
            entry.Plan.Source,
            entry.ActionName,
            entry.Excluded ? "Left out" : ActionLabel(entry.ActionName),
            GroupOf(entry, progress.Status),
            entry.Reason,
            entry.Selected,
            entry.Selectable,
            entry.Excluded,
            entry.Editable,
            entry.Packaged,
            entry.Mode.ToString(),
            entry.Plan.CanUseSteamIntegration,
            entry.Plan.RequiresAcknowledgement,
            entry.Acknowledged,
            entry.Packaged ? ModeLabel(entry.Mode) : route?.Label ?? entry.Game.Launch.Label,
            route?.Follows ?? false,
            [
                .. entry.Game.CommandRoutes.Select(candidate => new GameLibraryRoute(candidate.Id, candidate.Label,
                    candidate.Follows))
            ],
            entry.Route,
            entry.AppId,
            entry.ArtworkApplied,
            entry.Editable
                ? [.. GameLibraryArtwork.Assets.Select(type =>
                    Slot(entry, type, Options(entry, type, artwork), preference, artwork))]
                : [],
            StatusName(progress.Status),
            progress.Detail,
            entry.Match?.Name ?? progress.MatchName,
            entry.Match is not null,
            entry.Game.Content?.SystemId ?? "", entry.Game.Content?.EmulatorInstallationId ?? "",
            entry.Game.Content?.CoreId ?? "", entry.Game.Content?.Id ?? "",
            status?.Location ?? entry.Game.Content?.Location ?? "",
            status?.State.ToString() ?? "", entry.Game.Content?.BackingPath.AbsolutePath ?? "",
            entry.Game.Content?.Arguments,
            status is { Available: false },
            status?.Label ?? "",
            entry.Game.Content?.LibraryId ?? status?.LibraryId ?? "",
            status?.Detail ?? "");
    }

    /// <summary>Every candidate for one entry's artwork type.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="type">The artwork type.</param>
    /// <param name="artwork">The artwork stage, or null to offer only the source's own images.</param>
    /// <returns>The candidates, in the order they are offered.</returns>
    internal static IReadOnlyList<GameLibraryArtworkOption> Options(GameLibraryEntryState entry, ArtworkAsset type,
        GameLibraryArtwork? artwork)
    {
        return artwork is null
            ?
            [
                .. entry.Game.Artwork.Where(image => image.Asset == type)
                    .Select(image => new GameLibraryArtworkOption(image.Url, image.Url, "Catalog", true, 0, 0))
            ]
            : artwork.Options(entry.Id, type);
    }

    /// <summary>How far the artwork stage got with one entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="artwork">The artwork stage, or null.</param>
    /// <returns>The stage's progress, or ready without a stage.</returns>
    internal static GameLibraryArtworkProgress Progress(GameLibraryEntryState entry, GameLibraryArtwork? artwork)
    {
        return artwork?.StatusOf(entry.Id)
               ?? new GameLibraryArtworkProgress(GameLibraryArtworkStatus.Ready, string.Empty, string.Empty);
    }

    /// <summary>The first candidate of the preferred kind, or of any kind when there is none of it.</summary>
    /// <param name="options">The candidates.</param>
    /// <param name="preference">Which kind leads.</param>
    /// <param name="fallBack">Whether a candidate of the other kind may stand in.</param>
    /// <returns>The candidate, or null.</returns>
    internal static GameLibraryArtworkOption? Preferred(
        IReadOnlyList<GameLibraryArtworkOption> options, ArtworkPreference preference, bool fallBack)
    {
        var catalog = preference is ArtworkPreference.Catalog;
        return options.FirstOrDefault(option => option.Catalog == catalog)
               ?? (fallBack ? options.FirstOrDefault() : null);
    }

    /// <summary>What one artwork type of an entry would get.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="type">The artwork type.</param>
    /// <param name="options">Its candidates.</param>
    /// <param name="preference">Which kind of artwork leads.</param>
    /// <param name="artwork">The artwork stage, or null.</param>
    /// <returns>The slot both surfaces show.</returns>
    internal static GameLibraryArtworkSlot Slot(GameLibraryEntryState entry, ArtworkAsset type,
        IReadOnlyList<GameLibraryArtworkOption> options, ArtworkPreference preference, GameLibraryArtwork? artwork)
    {
        var name = ArtworkAssetNames.ToId(type);
        if (entry.Picks.TryGetValue(type, out var pick))
        {
            return pick.Url.Length == 0
                ? new GameLibraryArtworkSlot(name, "none", string.Empty, string.Empty, 0, options.Count)
                : new GameLibraryArtworkSlot(name, "pick", pick.Thumb, pick.Provider, IndexOf(options, pick.Url) + 1,
                    options.Count);
        }

        if (entry.AppId > 0)
        {
            return new GameLibraryArtworkSlot(name, "keep", string.Empty, string.Empty, 0, options.Count);
        }

        if (Preferred(options, preference, true) is { } option)
        {
            return new GameLibraryArtworkSlot(name, "default", option.Thumb, option.Provider,
                IndexOf(options, option.Url) + 1, options.Count);
        }

        return new GameLibraryArtworkSlot(name,
            Progress(entry, artwork).Status is GameLibraryArtworkStatus.Pending or GameLibraryArtworkStatus.Loading
                ? "loading"
                : "none",
            string.Empty, string.Empty, 0, 0);
    }

    /// <summary>Where a URL sits among the candidates.</summary>
    /// <param name="options">The candidates.</param>
    /// <param name="url">The URL.</param>
    /// <returns>Its index, or -1.</returns>
    internal static int IndexOf(IReadOnlyList<GameLibraryArtworkOption> options, string url)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (string.Equals(options[index].Url, url, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>An artwork status's published name.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The name both surfaces read.</returns>
    internal static string StatusName(GameLibraryArtworkStatus status)
    {
        return status switch
        {
            GameLibraryArtworkStatus.Loading => "loading",
            GameLibraryArtworkStatus.Ready => "ready",
            GameLibraryArtworkStatus.NotFound => "notFound",
            GameLibraryArtworkStatus.Failed => "failed",
            GameLibraryArtworkStatus.Unavailable => "unavailable",
            _ => "pending"
        };
    }

    /// <summary>Which tab lists an entry besides All; one group each, attention first.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="artwork">The artwork stage, or null.</param>
    /// <returns>The group's name.</returns>
    internal static string GroupOf(GameLibraryEntryState entry, GameLibraryArtwork? artwork)
    {
        return GroupOf(entry, Progress(entry, artwork).Status);
    }

    /// <summary>What an input mode is called on either surface.</summary>
    /// <param name="mode">Packaged launch input mode.</param>
    /// <returns>The shared Steam and overlay display label.</returns>
    internal static string ModeLabel(ImportMode mode)
    {
        return mode is ImportMode.SteamIntegration ? "Steam overlay" : "Controller only";
    }

    /// <summary>The managed content's evidence lines for a details sheet.</summary>
    /// <param name="content">The title's managed content, or null.</param>
    /// <returns>Its location, content path and required companions.</returns>
    internal static IEnumerable<string> ContentNotes(ManagedContentRecord? content)
    {
        if (content is null)
        {
            yield break;
        }

        yield return "Location: " + content.Location;
        yield return "Content: " + content.BackingPath.AbsolutePath;
        foreach (var companion in content.RequiredPaths)
        {
            yield return "Required companion: " + companion.AbsolutePath;
        }
    }

    private static string GroupOf(GameLibraryEntryState entry, GameLibraryArtworkStatus artwork)
    {
        if (entry.Excluded)
        {
            return "excluded";
        }

        if (entry.Action is ImportAction.Conflict or ImportAction.Remove
            || (entry.Editable && artwork is GameLibraryArtworkStatus.Failed))
        {
            return "attention";
        }

        return entry.Action is ImportAction.Add or ImportAction.Adopt ? "new" : "imported";
    }

    /// <summary>What an action is called on either surface.</summary>
    private static string ActionLabel(string action)
    {
        return action switch
        {
            "Add" => "New",
            "Update" => "Update",
            "Artwork" => "Artwork",
            "Adopt" => "Adopt",
            "Remove" => "Remove",
            "Skip" => "Imported",
            "Conflict" => "Edited by hand",
            _ => action
        };
    }

    /// <summary>The tracker's status for an entry's shortcut, by app id, else by its managed content id.</summary>
    private static ShortcutStatus? StatusOf(GameLibraryEntryState entry, ShortcutAvailabilitySnapshot tracked)
    {
        return tracked.Find(entry.AppId)
               ?? (entry.Game.Content is { } content
                   ? tracked.Shortcuts.FirstOrDefault(status => status.ContentId == content.Id)
                   : null);
    }
}
