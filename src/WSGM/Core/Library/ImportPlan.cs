using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Which input route a generated entry launches with.</summary>
public enum ImportMode
{
    /// <summary>Switch the controller to Xbox 360 and inject nothing.</summary>
    ControllerOnly,

    /// <summary>Give the real game Steam's overlay and Steam Input.</summary>
    SteamIntegration
}

/// <summary>What a sync would do to one title.</summary>
public enum ImportAction
{
    /// <summary>Create a new non-Steam shortcut.</summary>
    Add,

    /// <summary>Rewrite an existing entry WSGM created, because its command line changed.</summary>
    Update,

    /// <summary>Record an entry that already launches this title but WSGM did not record.</summary>
    Adopt,

    /// <summary>Nothing to do.</summary>
    Skip,

    /// <summary>Remove an entry WSGM created for a title that is no longer installed.</summary>
    Remove,

    /// <summary>The entry has been changed by hand. Never touched without confirmation.</summary>
    Conflict
}

/// <summary>What WSGM remembers about one entry it created.</summary>
public sealed class ImportedEntry
{
    /// <summary>Which source it came from.</summary>
    public string Source { get; set; } = "";

    /// <summary>Its stable identity in that source. The AUMID, for Xbox.</summary>
    public string Key { get; set; } = "";

    /// <summary>The shortcut id Steam generated, or zero while unconfirmed.</summary>
    public uint AppId { get; set; }

    /// <summary>What the entry was called when it was created.</summary>
    public string Name { get; set; } = "";

    /// <summary>Exactly the Target that was written.</summary>
    public string Target { get; set; } = "";

    /// <summary>Exactly the Launch Arguments that were written.</summary>
    public string LaunchOptions { get; set; } = "";

    /// <summary>Which input mode it was generated for: the packaged route's choice.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Which command route it was generated for, or empty for the packaged route.</summary>
    public string Route { get; set; } = "";

    /// <summary>Whether the user accepted the ban risk for this title.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>When it was created, round-trip UTC.</summary>
    public string ImportedUtc { get; set; } = "";

    /// <summary>When its id was confirmed by a library read, or empty while unconfirmed.</summary>
    public string ConfirmedUtc { get; set; } = "";

    /// <summary>How many of the Store's images were applied when it was imported.</summary>
    public int ArtworkApplied { get; set; }

    /// <summary>Whether an import created the per-game profile holding its controller override.</summary>
    /// <remarks>
    ///     Only such a profile is removed when the override is cleared and nothing else is left in it.
    ///     A profile the user made, or had before an entry was adopted, keeps its name, executables
    ///     and switch.
    /// </remarks>
    public bool OwnsProfile { get; set; }
}

/// <summary>What the user decided about one title, kept across scans.</summary>
/// <remarks>
///     <para>
///         The Game Library's equivalent of Steam ROM Manager's user exceptions. A scan rebuilds
///         every entry from what the sources and Steam say now; without these, a mode picked and not
///         yet applied, or a title the user said not to import, would come back undone on the next
///         scan.
///     </para>
///     <para>
///         A choice describes intent, and a record describes what Steam has. Once an apply writes a
///         record for a title, its choice is dropped, so the record is the one truth for anything
///         already imported.
///     </para>
/// </remarks>
public sealed class ImportChoice
{
    /// <summary>Which source the title belongs to.</summary>
    public string Source { get; set; } = "";

    /// <summary>Its identity in that source.</summary>
    public string Key { get; set; } = "";

    /// <summary>The launch mode the user picked, or empty when they have not picked one.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Whether the user accepted the ban risk along with that mode.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>Whether the user said not to import this title.</summary>
    public bool Excluded { get; set; }

    /// <summary>The command route the user picked, or empty when they have not picked one.</summary>
    public string Route { get; set; } = "";

    /// <summary>The artwork the user picked, one per artwork type at most.</summary>
    public List<ArtworkPick> Artwork { get; set; } = [];

    /// <summary>The provider of the game the user matched the title to, or empty for the automatic match.</summary>
    public string MatchProvider { get; set; } = "";

    /// <summary>That provider's id for the game.</summary>
    public string MatchId { get; set; } = "";

    /// <summary>That game's name, as the provider calls it.</summary>
    public string MatchName { get; set; } = "";

    /// <summary>Whether this choice says anything at all, so an empty one can be dropped.</summary>
    /// <returns>True when nothing is left in it.</returns>
    public bool IsEmpty()
    {
        return Mode.Length == 0 && !Acknowledged && !Excluded && Route.Length == 0 && Artwork.Count == 0
               && MatchId.Length == 0;
    }

    /// <summary>The picked mode, when there is one.</summary>
    /// <returns>The mode, or null when the user has not picked one.</returns>
    public ImportMode? PickedMode()
    {
        // Numeric strings parse too, whether or not they name a mode.
        return Enum.TryParse<ImportMode>(Mode, false, out var mode) && Enum.IsDefined(mode) ? mode : null;
    }
}

/// <summary>An image the user picked for one artwork type of one title.</summary>
/// <remarks>
///     Kept by URL rather than by position, because the candidates are fetched again after every scan
///     and their order is the provider's to change. An empty URL means the user cleared the slot: no
///     image is applied for that type.
/// </remarks>
public sealed class ArtworkPick
{
    /// <summary>Which artwork type.</summary>
    public ArtworkAsset Asset { get; set; }

    /// <summary>The full-size image, or empty for "apply nothing".</summary>
    public string Url { get; set; } = "";

    /// <summary>The thumbnail shown for it.</summary>
    public string Thumb { get; set; } = "";

    /// <summary>Who supplied it, as shown on screen.</summary>
    public string Provider { get; set; } = "";
}

/// <summary>One line of a sync preview.</summary>
/// <param name="Source">Which source the title belongs to.</param>
/// <param name="Key">The title's identity within that source.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Action">What a sync would do.</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Mode">Which route it would launch with.</param>
/// <param name="CanUseSteamIntegration">Whether the overlay route is available for it at all.</param>
/// <param name="RequiresAcknowledgement">Whether choosing that route needs the risk accepted.</param>
/// <param name="AppId">The existing shortcut id, when there is one.</param>
/// <param name="Selectable">Whether a sync may act on it without a per-entry decision.</param>
/// <param name="Unconfirmed">
///     Whether this adds a title again after an earlier add Steam never confirmed. That entry may
///     still appear, so this is offered for the user to decide and never started ticked.
/// </param>
/// <param name="Route">The command route it launches with, or empty for the packaged route.</param>
public sealed record ImportPlanEntry(
    string Source,
    string Key,
    string Name,
    ImportAction Action,
    string Reason,
    ImportMode Mode,
    bool CanUseSteamIntegration,
    bool RequiresAcknowledgement,
    uint AppId,
    bool Selectable,
    bool Unconfirmed = false,
    string Route = "");

/// <summary>An existing non-Steam shortcut, as Steam reports it.</summary>
/// <param name="AppId">Its generated id.</param>
/// <param name="Target">What it runs.</param>
/// <param name="LaunchOptions">Its arguments.</param>
public sealed record ExistingShortcut(uint AppId, string Target, string LaunchOptions);

/// <summary>Works out what a sync would do, without doing any of it.</summary>
/// <remarks>
///     <para>
///         Pure. A scan is a dry run by construction: this produces the preview and nothing else
///         writes until the user applies it.
///     </para>
///     <para>
///         Identity is the pair of source and key, never the display name. Two titles can share a
///         name, and two sources could in principle share a key; no two titles share both.
///     </para>
///     <para>
///         The user's own choices are not an input here. An imported title's mode comes from its
///         record, which is what Steam actually has; the caller lays the user's choices over the
///         result, so a choice that differs from the record shows up as the change it is.
///     </para>
/// </remarks>
public static class ImportPlan
{
    /// <summary>Builds the preview.</summary>
    /// <param name="discovered">What the sources found.</param>
    /// <param name="recorded">What WSGM remembers creating.</param>
    /// <param name="existing">The non-Steam shortcuts Steam currently has.</param>
    /// <param name="launcherTarget">The Target a generated entry would carry.</param>
    /// <param name="defaultMode">The mode to use when nothing else decides.</param>
    /// <param name="includeUnroutable">Whether to offer titles with no validated launch route.</param>
    /// <param name="scanned">
    ///     Whether a source was read in this scan. A record from a source that was not - one the user
    ///     unticked - is left out entirely rather than offered for removal: turning a launcher off is
    ///     not a request to delete its games. Null means every source was read.
    /// </param>
    /// <returns>One entry per title, in discovery order, then removals.</returns>
    public static IReadOnlyList<ImportPlanEntry> Build(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable,
        Func<string, bool>? scanned = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(existing);

        List<ImportPlanEntry> plan = [];
        var byId = existing.ToDictionary(shortcut => shortcut.AppId);
        HashSet<(string Source, string Key)> seen = new(IdentityComparer.Instance);

        foreach (var game in discovered)
        {
            seen.Add((game.SourceId, game.Key));
            plan.Add(Describe(game, recorded, byId, existing, launcherTarget, defaultMode,
                includeUnroutable));
        }

        // Removals last, and only for entries WSGM itself created whose title is gone.
        foreach (var record in recorded)
        {
            if (seen.Contains((record.Source, record.Key)) || record.AppId == 0
                                                           || (scanned is not null && !scanned(record.Source)))
            {
                continue;
            }

            if (!byId.TryGetValue(record.AppId, out var shortcut))
            {
                // Already gone from Steam: there is nothing to delete, but the record and the
                // controller override it left behind are still here. Kept as a removal, keeping the
                // app id so that override can be found, and selectable — an entry nobody can tick
                // is a record that announces on every scan that it is about to be dropped, forever.
                plan.Add(new ImportPlanEntry(record.Source, record.Key, record.Name, ImportAction.Remove,
                    "This entry is no longer in Steam, so only its record is left to drop.",
                    ParseMode(record.Mode), false, false, record.AppId, true, Route: record.Route));
                continue;
            }

            // Three-way agreement before anything is deleted: the record, the live entry's Target,
            // and its arguments must all still describe the entry WSGM created.
            if (!OwnsRecorded(shortcut, record, launcherTarget))
            {
                plan.Add(new ImportPlanEntry(record.Source, record.Key, record.Name, ImportAction.Conflict,
                    "This entry has been changed by hand, so it is left alone.",
                    ParseMode(record.Mode), false, false, record.AppId, false, Route: record.Route));
                continue;
            }

            // Selectable, but the caller never pre-selects a removal: deleting somebody's shortcut
            // is a thing they ask for, and an entry they cannot tick is one they can never ask for.
            plan.Add(new ImportPlanEntry(record.Source, record.Key, record.Name, ImportAction.Remove,
                "This title is no longer installed.",
                ParseMode(record.Mode), false, false, record.AppId, true, Route: record.Route));
        }

        return plan;
    }

    /// <summary>Whether a live shortcut is one WSGM created for a discovered title.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="game">The title.</param>
    /// <param name="launcherTarget">The Target a generated packaged entry carries.</param>
    /// <returns>
    ///     For a packaged title, the launcher Target with the title's key in the arguments; for a
    ///     command title, exactly one of its routes.
    /// </returns>
    public static bool Owns(ExistingShortcut shortcut, DiscoveredGame game, string launcherTarget)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Packaged
            ? PackagedLauncherShortcut.Owns(shortcut, launcherTarget, game.Key)
            : CommandShortcut.RouteOf(shortcut, game.CommandRoutes, launcherTarget) is not null;
    }

    /// <summary>Whether a live shortcut is still the one a record describes, by the record alone.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="record">What WSGM wrote.</param>
    /// <param name="launcherTarget">The Target a generated packaged entry carries.</param>
    /// <returns>True when it still runs what was written for that title.</returns>
    public static bool OwnsRecorded(ExistingShortcut shortcut, ImportedEntry record, string launcherTarget)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Route.Length == 0
            ? PackagedLauncherShortcut.Owns(shortcut, launcherTarget, record.Key)
            : CommandShortcut.Same(shortcut, record.Target, record.LaunchOptions);
    }

    /// <summary>Whether a record belongs to a title.</summary>
    /// <param name="record">The record.</param>
    /// <param name="source">The title's source.</param>
    /// <param name="key">The title's key.</param>
    /// <returns>True when both halves of the identity agree.</returns>
    public static bool Matches(ImportedEntry record, string source, string key)
    {
        ArgumentNullException.ThrowIfNull(record);
        return IdentityComparer.Instance.Equals((record.Source, record.Key), (source, key));
    }

    private static ImportPlanEntry Describe(
        DiscoveredGame game,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyDictionary<uint, ExistingShortcut> byId,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable)
    {
        if (!game.Packaged)
        {
            return DescribeCommand(game, recorded, byId, existing, launcherTarget);
        }

        // The overlay route needs a validated launch route. Without one there is nothing for the
        // user to accept a risk about, so the choice is not offered rather than offered and refused.
        var canIntegrate = game.Launch.Validated;
        var requiresAcknowledgement = canIntegrate && game.Multiplayer is MultiplayerVerdict.Multiplayer;
        var mode = !canIntegrate
            ? ImportMode.ControllerOnly
            : game.Multiplayer is MultiplayerVerdict.Multiplayer
                ? ImportMode.ControllerOnly
                : defaultMode;

        var record = recorded.FirstOrDefault(entry => Matches(entry, game.SourceId, game.Key));

        if (!canIntegrate && !includeUnroutable)
        {
            return Entry(game, ImportAction.Skip, game.Launch.Evidence, mode, false, false,
                record?.AppId ?? 0, false);
        }

        if (record is { AppId: > 0 } && byId.TryGetValue(record.AppId, out var live))
        {
            if (!PackagedLauncherShortcut.Owns(live, launcherTarget, game.Key))
            {
                return Entry(game, ImportAction.Conflict,
                    "This entry has been changed by hand, so it is left alone.",
                    ParseMode(record.Mode), canIntegrate, requiresAcknowledgement, record.AppId, false);
            }

            // Steam's fields differing from what was written can only mean somebody edited them,
            // even with our Target and key left in place - a mode switched by hand, a diagnostic
            // flag added. Restoring the recorded command would silently undo that, so it is left
            // alone. A route the user changes here is an update through the entry, not this.
            var wanted = record.Mode.Length > 0 ? ParseMode(record.Mode) : mode;
            var edited = !string.Equals(live.Target, record.Target, StringComparison.Ordinal)
                         || !string.Equals(live.LaunchOptions, record.LaunchOptions, StringComparison.Ordinal);
            return edited
                ? Entry(game, ImportAction.Conflict, "This entry has been changed by hand, so it is left alone.",
                    wanted, canIntegrate, requiresAcknowledgement, record.AppId, false)
                : Entry(game, ImportAction.Skip, "Already imported.",
                    wanted, canIntegrate, requiresAcknowledgement, record.AppId, false);
        }

        // No record, but Steam already has an entry launching this title. Adopting it is what stops
        // a second copy appearing every time the user re-runs a sync.
        var orphan = existing.FirstOrDefault(shortcut =>
            PackagedLauncherShortcut.Owns(shortcut, launcherTarget, game.Key));
        if (orphan is not null)
        {
            // Adopted as what it currently launches, not as what the current default would write.
            // Taking over an entry must not quietly change how the game starts.
            var adopted = PackagedLauncherShortcut.TryReadMode(orphan.LaunchOptions, out var current)
                ? current
                : mode;
            return Entry(game, ImportAction.Adopt, "Steam already has an entry for this title.",
                adopted, canIntegrate, requiresAcknowledgement, orphan.AppId, true);
        }

        if (record is { ConfirmedUtc.Length: 0 })
        {
            // An earlier add that Steam did not show within the settle, and does not show now either.
            // It may still have succeeded, so adding again could make a second copy: offered, because
            // only the user can look at the library and say, but never ticked for them.
            return Entry(game, ImportAction.Add,
                    "An earlier import of this title was never confirmed by Steam and may still appear. "
                    + "Check the library before adding it again.",
                    mode, canIntegrate, requiresAcknowledgement, 0, true)
                with
                {
                    Unconfirmed = true
                };
        }

        return Entry(game, ImportAction.Add, game.Launch.Evidence,
            mode, canIntegrate, requiresAcknowledgement, 0, true);
    }

    /// <summary>Describes a title that launches by a command rather than the packaged launcher.</summary>
    /// <remarks>
    ///     The same actions as a packaged title, with ownership by exact command. There is no input
    ///     mode and no acknowledgement: WSGM injects nothing into these, and Steam launches them as it
    ///     launches any non-Steam game. The route stands in for the mode.
    /// </remarks>
    private static ImportPlanEntry DescribeCommand(
        DiscoveredGame game,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyDictionary<uint, ExistingShortcut> byId,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget)
    {
        var fallback = game.CommandRoutes[0].Id;
        var record = recorded.FirstOrDefault(entry => Matches(entry, game.SourceId, game.Key));
        if (record is { AppId: > 0 } && byId.TryGetValue(record.AppId, out var live))
        {
            var route = record.Route.Length > 0 ? record.Route : fallback;
            return OwnsRecorded(live, record, launcherTarget)
                ? Command(game, ImportAction.Skip, "Already imported.", record.AppId, false, route)
                : Command(game, ImportAction.Conflict, "This entry has been changed by hand, so it is left alone.",
                    record.AppId, false, route);
        }

        foreach (var shortcut in existing)
        {
            if (CommandShortcut.RouteOf(shortcut, game.CommandRoutes, launcherTarget) is { } adopted)
            {
                return Command(game, ImportAction.Adopt, "Steam already has an entry for this title.",
                    shortcut.AppId, true, adopted.Id);
            }
        }

        if (record is { ConfirmedUtc.Length: 0 })
        {
            return Command(game, ImportAction.Add,
                    "An earlier import of this title was never confirmed by Steam and may still appear. "
                    + "Check the library before adding it again.", 0, true, fallback)
                with
                {
                    Unconfirmed = true
                };
        }

        return Command(game, ImportAction.Add, game.Launch.Evidence, 0, true, fallback);
    }

    private static ImportPlanEntry Command(
        DiscoveredGame game, ImportAction action, string reason, uint appId, bool selectable, string route)
    {
        return new ImportPlanEntry(game.SourceId, game.Key, game.Name, action, reason,
            ImportMode.SteamIntegration, false, false, appId, selectable, Route: route);
    }

    private static ImportPlanEntry Entry(
        DiscoveredGame game, ImportAction action, string reason, ImportMode mode,
        bool canIntegrate, bool requiresAcknowledgement, uint appId, bool selectable)
    {
        return new ImportPlanEntry(game.SourceId, game.Key, game.Name, action, reason, mode,
            canIntegrate, requiresAcknowledgement, appId, selectable);
    }

    private static ImportMode ParseMode(string mode)
    {
        return mode.Equals(nameof(ImportMode.SteamIntegration), StringComparison.OrdinalIgnoreCase)
            ? ImportMode.SteamIntegration
            : ImportMode.ControllerOnly;
    }

    /// <summary>Both halves of an identity, compared the way Windows compares package names.</summary>
    private sealed class IdentityComparer : IEqualityComparer<(string Source, string Key)>
    {
        internal static readonly IdentityComparer Instance = new();

        public bool Equals((string Source, string Key) x, (string Source, string Key) y)
        {
            return string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Source, string Key) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
        }
    }
}
