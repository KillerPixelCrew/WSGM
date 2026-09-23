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

    /// <summary>Which route it was generated for.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Whether the user accepted the ban risk for this title.</summary>
    public bool Acknowledged { get; set; }

    /// <summary>When it was created, round-trip UTC.</summary>
    public string ImportedUtc { get; set; } = "";

    /// <summary>When its id was confirmed by a library read, or empty while unconfirmed.</summary>
    public string ConfirmedUtc { get; set; } = "";
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

    /// <summary>The picked mode, when there is one.</summary>
    /// <returns>The mode, or null when the user has not picked one.</returns>
    public ImportMode? PickedMode()
    {
        return Enum.TryParse<ImportMode>(Mode, false, out var mode) ? mode : null;
    }
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
    bool Selectable);

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
    /// <returns>One entry per title, in discovery order, then removals.</returns>
    public static IReadOnlyList<ImportPlanEntry> Build(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnroutable)
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
            if (seen.Contains((record.Source, record.Key)) || record.AppId == 0)
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
                    ParseMode(record.Mode), false, false, record.AppId, true));
                continue;
            }

            // Three-way agreement before anything is deleted: the record, the live entry's Target,
            // and its arguments must all still describe the entry WSGM created.
            if (!PackagedLauncherShortcut.Owns(shortcut, launcherTarget, record.Key))
            {
                plan.Add(new ImportPlanEntry(record.Source, record.Key, record.Name, ImportAction.Conflict,
                    "This entry has been changed by hand, so it is left alone.",
                    ParseMode(record.Mode), false, false, record.AppId, false));
                continue;
            }

            // Selectable, but the caller never pre-selects a removal: deleting somebody's shortcut
            // is a thing they ask for, and an entry they cannot tick is one they can never ask for.
            plan.Add(new ImportPlanEntry(record.Source, record.Key, record.Name, ImportAction.Remove,
                "This title is no longer installed.",
                ParseMode(record.Mode), false, false, record.AppId, true));
        }

        return plan;
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

        if (!game.IsGame && record is null)
        {
            // Selectable but not pre-selected. The Store is the only thing that can call a UWP
            // title a game, so an offline or incomplete lookup makes every one of them look like an
            // ordinary application; discovery keeps them listed precisely so the user can say
            // otherwise, and refusing the tick would take that back.
            return Entry(game, ImportAction.Add,
                "Neither the package nor the Store says this is a game. Import it anyway if it is.",
                mode, canIntegrate, requiresAcknowledgement, 0, true);
        }

        if (record is { AppId: > 0 } && byId.TryGetValue(record.AppId, out var live))
        {
            if (!PackagedLauncherShortcut.Owns(live, launcherTarget, game.Key))
            {
                return Entry(game, ImportAction.Conflict,
                    "This entry has been changed by hand, so it is left alone.",
                    ParseMode(record.Mode), canIntegrate, requiresAcknowledgement, record.AppId, false);
            }

            var wanted = record.Mode.Length > 0 ? ParseMode(record.Mode) : mode;
            var changed = !string.Equals(live.Target, record.Target, StringComparison.Ordinal)
                          || !string.Equals(live.LaunchOptions, record.LaunchOptions, StringComparison.Ordinal);
            return changed
                ? Entry(game, ImportAction.Update, "This entry's launch command has changed.",
                    wanted, canIntegrate, requiresAcknowledgement, record.AppId, true)
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

        return Entry(game, ImportAction.Add, game.Launch.Evidence,
            mode, canIntegrate, requiresAcknowledgement, 0, true);
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
