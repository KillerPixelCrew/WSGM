namespace WSGM.Core;

/// <summary>An install of the standalone Atlas8Tweaks VRR Fix, found by its runtime configuration.</summary>
/// <param name="FloorHz">The floor its configuration names, or null when unreadable.</param>
/// <param name="Original">The Intel state it saved before its install, or null when it saved none.</param>
internal sealed record ExtendedVrrStandalone(int? FloorHz, ExtendedVrrIntelState? Original);
