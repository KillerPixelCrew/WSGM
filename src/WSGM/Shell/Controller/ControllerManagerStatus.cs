using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The complete controller-management projection consumed by the overlay and diagnostics.</summary>
/// <param name="State">Current management lifecycle state.</param>
/// <param name="Target">
///     Resolved target type, possibly retained while idle, and the physical pad's own kind while native; null when
///     off, unavailable or faulted.
/// </param>
/// <param name="TargetSource">Profile layer supplying the target; None also covers the built-in default.</param>
/// <param name="ApplicationId">Canonical identity when the target comes from a game profile; otherwise null.</param>
/// <param name="Detail">Displayable lifecycle, availability or failure detail.</param>
internal sealed record ControllerManagerStatus(
    ControllerManagementState State,
    ManagedControllerTarget? Target,
    ProfileSource TargetSource,
    string? ApplicationId,
    string Detail);
