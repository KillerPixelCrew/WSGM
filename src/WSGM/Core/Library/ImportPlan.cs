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

/// <summary>One line of a sync preview.</summary>
/// <param name="Key">The title's identity.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Action">What a sync would do.</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Mode">Which route it would launch with.</param>
/// <param name="CanUseSteamIntegration">Whether the overlay route is available for it at all.</param>
/// <param name="RequiresAcknowledgement">Whether choosing that route needs the risk accepted.</param>
/// <param name="AppId">The existing shortcut id, when there is one.</param>
/// <param name="Selectable">Whether a sync may act on it without a per-entry decision.</param>
public sealed record ImportPlanEntry(
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
///         Identity is the source key — the AUMID for Xbox — never the display name. Two titles can
///         share a name; no two share an AUMID.
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
    /// <param name="includeUnknownRuntime">Whether to offer titles whose runtime is unestablished.</param>
    /// <returns>One entry per title, in discovery order, then removals.</returns>
    public static IReadOnlyList<ImportPlanEntry> Build(
        IReadOnlyList<DiscoveredGame> discovered,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnknownRuntime)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(existing);

        List<ImportPlanEntry> plan = [];
        var byId = existing.ToDictionary(shortcut => shortcut.AppId);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var game in discovered)
        {
            seen.Add(game.Key);
            plan.Add(Describe(game, recorded, byId, existing, launcherTarget, defaultMode,
                includeUnknownRuntime));
        }

        // Removals last, and only for entries WSGM itself created whose title is gone.
        foreach (var record in recorded)
        {
            if (seen.Contains(record.Key) || record.AppId == 0)
            {
                continue;
            }

            if (!byId.TryGetValue(record.AppId, out var shortcut))
            {
                // Already gone from Steam. Nothing to remove; the stale record is dropped on apply.
                plan.Add(new ImportPlanEntry(record.Key, record.Name, ImportAction.Skip,
                    "This entry is no longer in Steam, so its record is dropped.",
                    ParseMode(record.Mode), false, false, 0, false));
                continue;
            }

            // Three-way agreement before anything is deleted: the record, the live entry's Target,
            // and its arguments must all still describe the entry WSGM created.
            if (!Ours(shortcut, launcherTarget, record.Key))
            {
                plan.Add(new ImportPlanEntry(record.Key, record.Name, ImportAction.Conflict,
                    "This entry has been changed by hand, so it is left alone.",
                    ParseMode(record.Mode), false, false, record.AppId, false));
                continue;
            }

            plan.Add(new ImportPlanEntry(record.Key, record.Name, ImportAction.Remove,
                "This title is no longer installed.",
                ParseMode(record.Mode), false, false, record.AppId, false));
        }

        return plan;
    }

    private static ImportPlanEntry Describe(
        DiscoveredGame game,
        IReadOnlyList<ImportedEntry> recorded,
        IReadOnlyDictionary<uint, ExistingShortcut> byId,
        IReadOnlyList<ExistingShortcut> existing,
        string launcherTarget,
        ImportMode defaultMode,
        bool includeUnknownRuntime)
    {
        var classified = game.Runtime is not XboxRuntime.Unknown;

        // The overlay route needs a validated launch route. Without one there is nothing for the
        // user to accept a risk about, so the choice is not offered rather than offered and refused.
        var canIntegrate = classified;
        var requiresAcknowledgement = canIntegrate && game.Multiplayer is MultiplayerVerdict.Multiplayer;
        var mode = !canIntegrate
            ? ImportMode.ControllerOnly
            : game.Multiplayer is MultiplayerVerdict.Multiplayer
                ? ImportMode.ControllerOnly
                : defaultMode;

        var record = recorded.FirstOrDefault(
            entry => string.Equals(entry.Key, game.Key, StringComparison.OrdinalIgnoreCase));

        if (!classified && !includeUnknownRuntime)
        {
            return new ImportPlanEntry(game.Key, game.Name, ImportAction.Skip,
                game.RuntimeEvidence, mode, false, false, record?.AppId ?? 0, false);
        }

        if (!game.IsGame && record is null)
        {
            return new ImportPlanEntry(game.Key, game.Name, ImportAction.Skip,
                "Neither the package nor the Store says this is a game.",
                mode, canIntegrate, requiresAcknowledgement, 0, false);
        }

        if (record is { AppId: > 0 } && byId.TryGetValue(record.AppId, out var live))
        {
            if (!Ours(live, launcherTarget, game.Key))
            {
                return new ImportPlanEntry(game.Key, game.Name, ImportAction.Conflict,
                    "This entry has been changed by hand, so it is left alone.",
                    ParseMode(record.Mode), canIntegrate, requiresAcknowledgement, record.AppId, false);
            }

            var wanted = record.Mode.Length > 0 ? ParseMode(record.Mode) : mode;
            var changed = !string.Equals(live.Target, record.Target, StringComparison.Ordinal)
                          || !string.Equals(live.LaunchOptions, record.LaunchOptions, StringComparison.Ordinal);
            return changed
                ? new ImportPlanEntry(game.Key, game.Name, ImportAction.Update,
                    "This entry's launch command has changed.",
                    wanted, canIntegrate, requiresAcknowledgement, record.AppId, true)
                : new ImportPlanEntry(game.Key, game.Name, ImportAction.Skip,
                    "Already imported.", wanted, canIntegrate, requiresAcknowledgement, record.AppId, false);
        }

        // No record, but Steam already has an entry launching this title. Adopting it is what stops
        // a second copy appearing every time the user re-runs a sync.
        var orphan = existing.FirstOrDefault(shortcut => Ours(shortcut, launcherTarget, game.Key));
        if (orphan is not null)
        {
            return new ImportPlanEntry(game.Key, game.Name, ImportAction.Adopt,
                "Steam already has an entry for this title.",
                mode, canIntegrate, requiresAcknowledgement, orphan.AppId, true);
        }

        return new ImportPlanEntry(game.Key, game.Name, ImportAction.Add,
            classified ? game.RuntimeEvidence : game.RuntimeEvidence,
            mode, canIntegrate, requiresAcknowledgement, 0, true);
    }

    /// <summary>Whether a live shortcut is one WSGM created for this title.</summary>
    /// <param name="shortcut">The shortcut Steam reports.</param>
    /// <param name="launcherTarget">The Target a generated entry carries.</param>
    /// <param name="key">The title's identity.</param>
    /// <remarks>
    ///     Both halves must agree. A Target alone would match every title WSGM imported, and an
    ///     identity alone would match an entry somebody wrote by hand.
    /// </remarks>
    public static bool Ours(ExistingShortcut shortcut, string launcherTarget, string key)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        return Same(shortcut.Target, launcherTarget)
               && shortcut.LaunchOptions.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether two Target values name the same program, ignoring the quoting.</summary>
    private static bool Same(string left, string right)
    {
        return string.Equals(
            left.Trim().Trim('"'), right.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    private static ImportMode ParseMode(string mode)
    {
        return mode.Equals(nameof(ImportMode.SteamIntegration), StringComparison.OrdinalIgnoreCase)
            ? ImportMode.SteamIntegration
            : ImportMode.ControllerOnly;
    }
}
