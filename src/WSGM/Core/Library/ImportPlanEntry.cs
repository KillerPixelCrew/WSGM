namespace WSGM.Core;

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

/// <summary>Which input route a generated entry launches with.</summary>
public enum ImportMode
{
    /// <summary>Switch the controller to Xbox 360 and inject nothing.</summary>
    ControllerOnly,

    /// <summary>Give the real game Steam's overlay and Steam Input.</summary>
    SteamIntegration
}

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
