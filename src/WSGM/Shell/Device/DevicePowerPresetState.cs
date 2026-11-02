using System.Collections.Generic;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Current device preset choices and their projection from device/Windows power state.</summary>
/// <param name="Presets">
///     Presets declared by the current capability set, followed by the user's saved presets for this device
///     family, all selectable and assignable alike.
/// </param>
/// <param name="Available">Whether the current power pair and any required source/scenario admit preset commands.</param>
/// <param name="Current">Matching preset identifier, <c>custom</c>, or empty when no current choice can be projected.</param>
/// <param name="Status">Last operation or availability detail; empty means no detail.</param>
/// <param name="Values">Current watt limits and Windows mode when projectable, otherwise null.</param>
/// <param name="Saved">The user's saved presets for this device family, and whether each fits its current limits.</param>
/// <param name="Savable">Whether <paramref name="Values" /> is a Custom reading that can be saved as a preset.</param>
internal sealed record DevicePowerPresetState(
    IReadOnlyList<DevicePowerPreset> Presets,
    bool Available,
    string Current,
    string Status,
    DevicePowerCustomValues? Values = null,
    IReadOnlyList<DevicePowerSavedPreset>? Saved = null,
    bool Savable = false);
