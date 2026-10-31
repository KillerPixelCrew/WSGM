using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The complete AutoTDP projection.</summary>
/// <param name="State">Whether automatic control is off, unavailable, idle, controlling or manually paused.</param>
/// <param name="Watts">
///     Reported power limit in watts, or null; may be a controller estimate or fallback rather than
///     hardware readback.
/// </param>
/// <param name="FrametimeMs">Mean frame time of the selected RTSS sample in milliseconds, or null without a sample.</param>
/// <param name="TargetFrametimeMs">Frame delivery deadline in milliseconds, or null when this state has no active target.</param>
/// <param name="ApplicationId">Canonical application identity associated with the projection, or null when unresolved.</param>
/// <param name="Detail">Displayable reason for the state or controller decision.</param>
/// <param name="Action">Controller decision represented by this update, or null for a status-only update.</param>
internal sealed record AutoTdpStatus(
    AutoTdpState State,
    int? Watts,
    double? FrametimeMs,
    double? TargetFrametimeMs,
    string? ApplicationId,
    string Detail,
    AutoTdpAction? Action = null);
