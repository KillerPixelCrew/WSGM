namespace WSGM.Core;

/// <summary>The whole refresh rates a graphics driver can hold one display at.</summary>
/// <param name="MinimumHz">The lowest whole rate inside the monitor's range.</param>
/// <param name="MaximumHz">The highest whole rate inside the monitor's range.</param>
/// <param name="FixedHz">The rate the driver holds for WSGM now, rounded, or null when none is fixed.</param>
internal sealed record DriverRefreshRange(int MinimumHz, int MaximumHz, int? FixedHz);
