using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>The complete device power profile remembered for one custom AC or battery assignment.</summary>
public sealed record DevicePowerCustomValues
{
    /// <summary>Sustained power limit in watts.</summary>
    public int SustainedWatts { get; init; }

    /// <summary>Slow power limit in watts.</summary>
    public int SlowWatts { get; init; }

    /// <summary>Independent fast-PPT limit in watts, when the selected device declares one.</summary>
    public int? FastWatts { get; init; }

    /// <summary>Windows performance/efficiency mode.</summary>
    public DevicePowerMode WindowsMode { get; init; }

    /// <summary>Firmware scenario for this source, when included in the device presets.</summary>
    public string? Scenario { get; init; }

    // The SDK reserves "custom" for host presentation, so the validation target uses a separate ID.
    /// <summary>Builds an SDK validation value for this saved custom power assignment.</summary>
    /// <returns>A new preset with the saved wattage/mode and the same optional scenario for both power sources.</returns>
    internal DevicePowerPreset ToPreset()
    {
        return new DevicePowerPreset("custom-values", "Custom", SustainedWatts, SlowWatts, WindowsMode)
            { FastWatts = FastWatts, ScenarioOnAc = Scenario, ScenarioOnDc = Scenario };
    }
}
