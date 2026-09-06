using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>A saved preset assignment bound to the device plugin that declared it.</summary>
public sealed record DevicePowerPresetReference
{
    /// <summary>Package ID, preventing a replacement plugin from inheriting another device's policy.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Stable preset ID declared by that plugin.</summary>
    public string PresetId { get; set; } = string.Empty;

    /// <summary>Observed values retained for a Custom assignment on this power source.</summary>
    public DevicePowerCustomValues? CustomValues { get; set; }
}

/// <summary>The complete device power profile remembered for one custom AC or battery assignment.</summary>
public sealed record DevicePowerCustomValues
{
    /// <summary>Sustained power limit in watts.</summary>
    public int SustainedWatts { get; init; }
    /// <summary>Slow power limit in watts.</summary>
    public int SlowWatts { get; init; }
    /// <summary>Windows performance/efficiency mode.</summary>
    public DevicePowerMode WindowsMode { get; init; }
    /// <summary>Firmware scenario for this source, when included in the device presets.</summary>
    public string? Scenario { get; init; }

    // The SDK reserves "custom" for host presentation, so the validation target uses a separate ID.
    internal DevicePowerPreset ToPreset() => new("custom-values", "Custom", SustainedWatts, SlowWatts, WindowsMode)
    { ScenarioOnAc = Scenario, ScenarioOnDc = Scenario };
}
