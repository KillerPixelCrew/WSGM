using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One profile layer. Every member is optional: null means this layer does not set it.</summary>
public sealed class ProfileValues
{
    /// <summary>RTSS frame limit; zero disables limiting.</summary>
    public int? FrameLimit { get; set; }

    /// <summary>Performance-overlay level.</summary>
    public int? OverlayLevel { get; set; }

    /// <summary>Whether manual power uses the coordinated unified target rather than PL1/PL2.</summary>
    public bool? TdpUnified { get; set; }

    /// <summary>Unified power target in watts.</summary>
    public int? UnifiedWatts { get; set; }

    /// <summary>Sustained power limit in watts.</summary>
    public int? SustainedWatts { get; set; }

    /// <summary>Variable refresh.</summary>
    public bool? VariableRefreshRate { get; set; }

    /// <summary>Windows' processor boost mode.</summary>
    public CpuBoostMode? CpuBoost { get; set; }

    /// <summary>The frame rate AutoTDP holds the game to, independent of the frame limit.</summary>
    public int? AutoTdpTargetFps { get; set; }

    /// <summary>Device power preset on AC.</summary>
    public DevicePowerPresetReference? AcPowerPreset { get; set; }

    /// <summary>Device power preset on battery.</summary>
    public DevicePowerPresetReference? BatteryPowerPreset { get; set; }

    /// <summary>Authored fan-curve profile.</summary>
    public string? FanCurveProfileId { get; set; }

    /// <summary>Authored lighting color profile, inherited from Global when unset.</summary>
    public string? LightingProfileId { get; set; }

    /// <summary>Managed-controller target.</summary>
    public ManagedControllerTarget? ControllerTarget { get; set; }

    /// <summary>Device capability values, one per device, capability and instance.</summary>
    public List<ProfileDeviceValue> Device { get; set; } = [];
}
