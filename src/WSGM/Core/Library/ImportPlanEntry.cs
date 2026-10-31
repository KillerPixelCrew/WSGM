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
