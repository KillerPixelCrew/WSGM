using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What the power-preset editor shows.</summary>
/// <param name="Scope">Which layer an edit lands in, for the caption.</param>
/// <param name="AcPreset">The edit layer's own AC assignment; null inherits in a game profile.</param>
/// <param name="BatteryPreset">The edit layer's own battery assignment.</param>
/// <param name="Status">Why the last apply failed, or empty.</param>
/// <param name="IsGlobal">Whether edits land in Global.</param>
/// <param name="AcSource">The layer supplying the AC assignment in force.</param>
/// <param name="BatterySource">The layer supplying the battery assignment in force.</param>
internal sealed record DevicePowerAssignmentState(
    string Scope,
    string? AcPreset,
    string? BatteryPreset,
    string Status,
    bool IsGlobal,
    ProfileSource AcSource = ProfileSource.None,
    ProfileSource BatterySource = ProfileSource.None);
