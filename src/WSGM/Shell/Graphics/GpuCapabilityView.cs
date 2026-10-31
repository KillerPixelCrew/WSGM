namespace WSGM.Shell;

/// <summary>One graphics capability, ready for a control.</summary>
/// <param name="View">
///     The router's view: the descriptor, the projection with its desired and Global values, profile scope
///     and apply timing, and the last command result.
/// </param>
/// <param name="OverrideId">
///     The id to hand <see cref="GpuCoordinator.UseGlobalAsync" /> while the running game's profile supplies
///     the value, or null when the value is Global's.
/// </param>
internal sealed record GpuCapabilityView(DeviceCapabilityView View, string? OverrideId);
