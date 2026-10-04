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

    /// <summary>How many images, from any source or provider, have been applied to its shortcut.</summary>
    public int ArtworkApplied { get; set; }

    /// <summary>Whether an import created the per-game profile holding its controller override.</summary>
    /// <remarks>
    ///     Only such a profile is removed when the override is cleared and nothing else is left in it.
    ///     A profile the user made, or had before an entry was adopted, keeps its name, executables
    ///     and switch.
    /// </remarks>
    public bool OwnsProfile { get; set; }

    internal ImportedEntry Copy() => (ImportedEntry)MemberwiseClone();
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
///         record for a title, everything in its choice that Steam now holds is dropped, so the record
///         is the one truth for anything already imported. Only a match the user fixed stays, because
///         Steam keeps no trace of which game the artwork came from.
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
/// <param name="Preselect">
///     Whether the review starts with it ticked. Only a plain add or an update WSGM's own change
///     requires is: never a removal, an add Steam may already have, or a title the user deleted.
/// </param>
/// <param name="ReplacedAppId">
///     For a title whose recorded shortcut the user deleted from Steam, that shortcut's id, so adding
///     it again can release what the old one left behind. Zero otherwise.
/// </param>
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
    string Route = "",
    bool Preselect = false,
    uint ReplacedAppId = 0);

/// <summary>What a scan knows about the source a record came from.</summary>
public enum ImportSourceState
{
    /// <summary>Read in full in this scan: a record whose title it did not report is gone.</summary>
    Read,

    /// <summary>
    ///     Not read, or not trusted to be complete: unticked, failed, or reporting nothing although
    ///     titles were imported from it. Its records are left out, because turning a launcher off or
    ///     a transient failure is not a request to delete its games.
    /// </summary>
    Unread,

    /// <summary>No longer on this machine: an uninstalled launcher or a removed folder.</summary>
    Gone
}

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
    private const string EditedByHand = "This entry has been changed by hand, so it is left alone.";

    private const string NeverConfirmed =
        "An earlier import of this title was never confirmed by Steam and may still appear. "
        + "Check the library before adding it again.";

    private const string AlreadyImported = "Already imported.";

    private const string AlreadyInSteam = "Steam already has an entry for this title.";
    private const string ClaimedByAnotherTitle =
        "Another imported title uses Steam's entry for this game. Add this title as a separate entry.";

    private const string DeletedFromSteam =
        "You deleted this title's shortcut from Steam. Tick it to add it again.";

    private const string CommandChanged =
        "What this title's shortcut should run has changed, so the shortcut is rewritten.";

    /// <summary>Compares titles by both halves of their identity, the way the plan does.</summary>
    public static IEqualityComparer<(string Source, string Key)> Identity => IdentityComparer.Instance;

    /// <summary>Builds the preview.</summary>
    /// <param name="discovered">What the sources found.</param>
    /// <param name="recorded">What WSGM remembers creating.</param>
    /// <param name="existing">The non-Steam shortcuts Steam currently has.</param>
    /// <param name="launcherTarget">The Target a generated entry would carry, or empty without a launcher.</param>
    /// <param name="defaultMode">The mode to use when nothing else decides.</param>
    /// <param name="includeUnroutable">Whether to offer titles with no validated launch route.</param>
    /// <param name="sources">
    ///     What this scan knows about a record's source (<see cref="ImportSourceState" />). Null means
    ///     every source was read.
    /// </param>
    /// <returns>One entry per title, in discovery order, then removals.</returns>
    public static IReadOnlyList<ImportPlanEntry> Build(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable,
        Func<string, ImportSourceState>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(launcherTarget);

        // Steam should never hold two entries with one id; if a read ever reports that, the first
        // stands rather than the whole scan failing on a duplicate key.
        Dictionary<uint, ExistingShortcut> byId = [];
        foreach (var shortcut in existing)
        {
            byId.TryAdd(shortcut.AppId, shortcut);
        }

        Dictionary<(string Source, string Key), ImportedEntry> records = new(IdentityComparer.Instance);
        foreach (var record in recorded)
        {
            records[(record.Source, record.Key)] = record;
        }

        // A shortcut a record names belongs to that record's title. Offering it to another title as
        // an adoption would let one run adopt it and then delete it as the other title's removal.
        HashSet<uint> claimed = [.. records.Values.Where(record => record.AppId > 0).Select(record => record.AppId)];
        Context context = new(byId, existing, claimed, launcherTarget, defaultMode, includeUnroutable);

        List<ImportPlanEntry> plan = [];
        HashSet<(string Source, string Key)> seen = new(IdentityComparer.Instance);
        foreach (var game in discovered)
        {
            if (!seen.Add((game.SourceId, game.Key)))
            {
                continue;
            }

            var record = records.GetValueOrDefault((game.SourceId, game.Key));

            // Something that is not a game is left out, unless WSGM already imported it: then it is
            // handled like any other title, because dropping it here would offer an installed
            // title's shortcut for removal whenever the lookup that classifies it failed.
            if (!game.IsGame && record is null)
            {
                continue;
            }

            if ((game.Packaged ? Describe(game, record, context) : DescribeCommand(game, record, context))
                is { } entry)
            {
                plan.Add(entry);
                if (entry.Action == ImportAction.Adopt)
                {
                    context.Claim(entry.AppId);
                }
            }
        }

        // Removals last, and only for entries WSGM itself created whose title is gone.
        foreach (var record in records.Values)
        {
            if (seen.Contains((record.Source, record.Key)) || record.AppId == 0)
            {
                continue;
            }

            var state = sources?.Invoke(record.Source) ?? ImportSourceState.Read;
            if (state is ImportSourceState.Unread)
            {
                continue;
            }

            if (!byId.TryGetValue(record.AppId, out var shortcut))
            {
                // Already gone from Steam: there is nothing to delete, but the record and the
                // controller override it left behind are still here. Kept as a removal, keeping the
                // app id so that override can be found, and selectable: an entry nobody can tick
                // is a record that announces on every scan that it is about to be dropped, forever.
                plan.Add(Removal(record, ImportAction.Remove,
                    "This entry is no longer in Steam, so only its record is left to drop.", true));
                continue;
            }

            // Agreement before anything is deleted: the live entry's Target and arguments must still
            // be exactly what the record says WSGM wrote.
            if (!OwnsRecorded(shortcut, record))
            {
                plan.Add(Removal(record, ImportAction.Conflict, EditedByHand, false));
                continue;
            }

            // Selectable, but never ticked for the user: deleting somebody's shortcut is a thing they
            // ask for, and an entry they cannot tick is one they can never ask for.
            plan.Add(Removal(record, ImportAction.Remove,
                state is ImportSourceState.Gone
                    ? "The launcher or folder this title came from is no longer on this machine."
                    : "This title is no longer installed.",
                true));
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

    /// <summary>Whether a live shortcut is still exactly what a record says WSGM wrote.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="record">What WSGM wrote.</param>
    /// <returns>True when its Target and arguments are still the recorded ones.</returns>
    /// <remarks>
    ///     The record, not the current launcher path, is the reference for both kinds of title. A
    ///     packaged shortcut the user edited keeps WSGM's Target and key and is still not WSGM's to
    ///     delete, and one written before WSGM moved folders is still WSGM's own.
    /// </remarks>
    public static bool OwnsRecorded(ExistingShortcut shortcut, ImportedEntry record)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(record);
        return CommandShortcut.Same(shortcut, record.Target, record.LaunchOptions);
    }



    /// <summary>Describes a title that launches through the packaged launcher.</summary>
    /// <returns>Its plan entry, or null for a title without a validated route that is not offered.</returns>
    private static ImportPlanEntry? Describe(DiscoveredGame game, ImportedEntry? record, Context context)
    {
        // The overlay route needs a validated launch route. Without one there is nothing for the
        // user to accept a risk about, so the choice is not offered rather than offered and refused.
        var canIntegrate = game.Launch.Validated;
        var requiresAcknowledgement = canIntegrate && game.Multiplayer is MultiplayerVerdict.Multiplayer;
        var mode = canIntegrate && game.Multiplayer is not MultiplayerVerdict.Multiplayer
            ? context.DefaultMode
            : ImportMode.ControllerOnly;

        // Not offered at all, rather than listed as something already done: a title nobody imported
        // is not "already imported" because it cannot be launched.
        if (!canIntegrate && !context.IncludeUnroutable && record is null)
        {
            return null;
        }

        ImportPlanEntry Entry(ImportAction action, string reason, ImportMode entryMode, uint appId, bool selectable,
            bool preselect = false)
        {
            return new ImportPlanEntry(game.SourceId, game.Key, game.Name, action, reason, entryMode,
                canIntegrate, requiresAcknowledgement, appId, selectable, Preselect: preselect);
        }

        if (record is { AppId: > 0 } && context.ById.TryGetValue(record.AppId, out var live))
        {
            var recordedMode = record.Mode.Length > 0 ? ParseMode(record.Mode) : mode;

            // Steam's fields differing from what was written can only mean somebody edited them,
            // even with our Target and key left in place: a mode switched by hand, a diagnostic
            // flag added. Restoring the recorded command would silently undo that, so it is left
            // alone. A route the user changes here is an update through the entry, not this.
            if (!OwnsRecorded(live, record))
            {
                return Entry(ImportAction.Conflict, EditedByHand, recordedMode, record.AppId, false);
            }

            // Untouched, but no longer what WSGM would write: the launcher moved with WSGM, or the
            // command format changed. Rewriting it is WSGM's own change to make.
            return Recompose(context.LauncherTarget, game.Key, recordedMode,
                       game.Multiplayer is MultiplayerVerdict.Multiplayer, record.Acknowledged) is { } fresh
                   && !CommandShortcut.Same(live, fresh.Target, fresh.LaunchOptions)
                ? Entry(ImportAction.Update, CommandChanged, recordedMode, record.AppId, true, true)
                : Entry(ImportAction.Skip, AlreadyImported, recordedMode, record.AppId, false);
        }

        // No live record, but Steam already has an entry launching this title. Adopting it is what
        // stops a second copy appearing every time the user re-runs a sync.
        var orphan = context.Unclaimed.FirstOrDefault(shortcut =>
            PackagedLauncherShortcut.Owns(shortcut, context.LauncherTarget, game.Key));
        if (orphan is not null)
        {
            // Adopted as what it currently launches, not as what the current default would write.
            // Taking over an entry must not quietly change how the game starts.
            var adopted = PackagedLauncherShortcut.TryReadMode(orphan.LaunchOptions, out var current)
                ? current
                : mode;
            return Entry(ImportAction.Adopt, AlreadyInSteam, adopted, orphan.AppId, true);
        }

        if (context.Claimed.Any(shortcut =>
                PackagedLauncherShortcut.Owns(shortcut, context.LauncherTarget, game.Key)))
        {
            return Entry(ImportAction.Add, ClaimedByAnotherTitle, mode, 0, true, false);
        }

        if (record is { ConfirmedUtc.Length: 0 })
        {
            // An earlier add that Steam did not confirm, and does not show now either. It may still
            // have succeeded, so adding again could make a second copy: offered, because only the
            // user can look at the library and say, but never ticked for them.
            return Entry(ImportAction.Add, NeverConfirmed, mode, 0, true) with { Unconfirmed = true };
        }

        return record is { AppId: > 0 }
            ? Entry(ImportAction.Add, DeletedFromSteam, mode, 0, true) with { ReplacedAppId = record.AppId }
            : Entry(ImportAction.Add, game.Launch.Evidence, mode, 0, true, true);
    }

    /// <summary>Describes a title that launches by a command rather than the packaged launcher.</summary>
    /// <remarks>
    ///     The same actions as a packaged title, with ownership by exact command. There is no input
    ///     mode and no acknowledgement: WSGM injects nothing into these, and Steam launches them as it
    ///     launches any non-Steam game. The route stands in for the mode.
    /// </remarks>
    private static ImportPlanEntry DescribeCommand(DiscoveredGame game, ImportedEntry? record, Context context)
    {
        var fallback = game.CommandRoutes[0].Id;

        ImportPlanEntry Command(ImportAction action, string reason, uint appId, bool selectable, string route,
            bool preselect = false)
        {
            return new ImportPlanEntry(game.SourceId, game.Key, game.Name, action, reason,
                ImportMode.SteamIntegration, false, false, appId, selectable, Route: route, Preselect: preselect);
        }

        if (record is { AppId: > 0 } && context.ById.TryGetValue(record.AppId, out var live))
        {
            var recordedRoute = record.Route.Length > 0 ? record.Route : fallback;
            if (!OwnsRecorded(live, record))
            {
                return Command(ImportAction.Conflict, EditedByHand, record.AppId, false, recordedRoute);
            }

            // The route it was imported with may be gone: the launcher it went through was
            // uninstalled. The title still runs by its first route, but moving it there is the
            // user's call, so it is offered and not ticked.
            if (game.CommandRoutes.FirstOrDefault(route => route.Id == recordedRoute) is not { } offered)
            {
                return Command(ImportAction.Update,
                    "The way this title was imported is no longer available, so it would move to "
                    + $"{game.CommandRoutes[0].Label}.",
                    record.AppId, true, fallback);
            }

            // Untouched, but the source now describes a different command for the same route: the
            // install moved, or the launcher did.
            return CommandShortcut.TryCompose(offered, context.LauncherTarget, out var fresh, out _)
                   && !CommandShortcut.Same(live, fresh.Target, fresh.LaunchOptions)
                ? Command(ImportAction.Update, CommandChanged, record.AppId, true, recordedRoute, true)
                : Command(ImportAction.Skip, AlreadyImported, record.AppId, false, recordedRoute);
        }

        foreach (var shortcut in context.Unclaimed)
        {
            if (CommandShortcut.RouteOf(shortcut, game.CommandRoutes, context.LauncherTarget) is { } adopted)
            {
                return Command(ImportAction.Adopt, AlreadyInSteam, shortcut.AppId, true, adopted.Id);
            }
        }

        if (context.Claimed.Any(shortcut =>
                CommandShortcut.RouteOf(shortcut, game.CommandRoutes, context.LauncherTarget) is not null))
        {
            return Command(ImportAction.Add, ClaimedByAnotherTitle, 0, true, fallback, false);
        }

        if (record is { ConfirmedUtc.Length: 0 })
        {
            return Command(ImportAction.Add, NeverConfirmed, 0, true, fallback) with { Unconfirmed = true };
        }

        return record is { AppId: > 0 }
            ? Command(ImportAction.Add, DeletedFromSteam, 0, true, fallback) with { ReplacedAppId = record.AppId }
            : Command(ImportAction.Add, game.Launch.Evidence, 0, true, fallback, true);
    }

    /// <summary>What WSGM would write for a packaged title now, or null when it cannot say.</summary>
    private static ShortcutFields? Recompose(
        string launcherTarget, string key, ImportMode mode, bool multiplayer, bool acknowledged)
    {
        if (launcherTarget.Length == 0)
        {
            return null;
        }

        try
        {
            return PackagedLauncherShortcut.Compose(launcherTarget, key, mode, multiplayer, acknowledged);
        }
        catch (ArgumentException)
        {
            // A record the launcher would now refuse to compose is left as it is, not rewritten.
            return null;
        }
    }

    private static ImportPlanEntry Removal(ImportedEntry record, ImportAction action, string reason, bool selectable)
    {
        return new ImportPlanEntry(record.Source, record.Key, record.Name, action, reason,
            ParseMode(record.Mode), false, false, record.AppId, selectable, Route: record.Route);
    }

    private static ImportMode ParseMode(string mode)
    {
        return mode.Equals(nameof(ImportMode.SteamIntegration), StringComparison.OrdinalIgnoreCase)
            ? ImportMode.SteamIntegration
            : ImportMode.ControllerOnly;
    }

    /// <summary>What every title in one build is described against.</summary>
    private sealed class Context(
        IReadOnlyDictionary<uint, ExistingShortcut> byId,
        IReadOnlyList<ExistingShortcut> existing,
        IReadOnlySet<uint> claimed,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable)
    {
        internal IReadOnlyDictionary<uint, ExistingShortcut> ById { get; } = byId;

        /// <summary>The shortcuts no record names, which are the only ones a title may adopt.</summary>
        internal IEnumerable<ExistingShortcut> Unclaimed =>
            existing.Where(shortcut => !claimed.Contains(shortcut.AppId));

        internal IEnumerable<ExistingShortcut> Claimed =>
            existing.Where(shortcut => claimed.Contains(shortcut.AppId));

        internal void Claim(uint appId) => claimed.Add(appId);

        internal string LauncherTarget { get; } = launcherTarget;
        internal ImportMode DefaultMode { get; } = defaultMode;
        internal bool IncludeUnroutable { get; } = includeUnroutable;
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
