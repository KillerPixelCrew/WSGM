namespace WSGM.Core;

/// <summary>Every setting a profile layer can hold.</summary>
public enum ProfileField
{
    /// <summary>RTSS frame limit.</summary>
    FrameLimit,

    /// <summary>Performance-overlay level.</summary>
    OverlayLevel,

    /// <summary>Manual power mode.</summary>
    TdpUnified,

    /// <summary>Unified power target.</summary>
    UnifiedWatts,

    /// <summary>Sustained power limit.</summary>
    SustainedWatts,

    /// <summary>Variable refresh.</summary>
    VariableRefreshRate = 6,

    /// <summary>Windows' processor boost mode.</summary>
    CpuBoost,

    /// <summary>AC power preset.</summary>
    AcPowerPreset,

    /// <summary>Battery power preset.</summary>
    BatteryPowerPreset,

    /// <summary>Authored fan-curve profile.</summary>
    FanCurveProfile,

    /// <summary>Managed-controller target.</summary>
    ControllerTarget,

    /// <summary>One device capability value, named by capability and instance.</summary>
    Device,

    /// <summary>Authored lighting color profile.</summary>
    LightingProfile,

    /// <summary>AutoTDP's frame-rate target.</summary>
    AutoTdpTarget
}
