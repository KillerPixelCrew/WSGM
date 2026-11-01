using System.Collections.Generic;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Current device preset choices and their projection from device/Windows power state.</summary>
/// <param name="Presets">Presets declared by the current capability set.</param>
/// <param name="Available">Whether the current power pair and any required source/scenario admit preset commands.</param>
/// <param name="Current">Matching preset identifier, <c>custom</c>, or empty when no current choice can be projected.</param>
/// <param name="Status">Last operation or availability detail; empty means no detail.</param>
/// <param name="Values">Current watt limits and Windows mode when projectable, otherwise null.</param>
internal sealed record DevicePowerPresetState(
    IReadOnlyList<DevicePowerPreset> Presets,
    bool Available,
    string Current,
    string Status,
    DevicePowerCustomValues? Values = null);
