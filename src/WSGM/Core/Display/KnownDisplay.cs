using System;
using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     A display WSGM has seen, remembered so it can be configured while it is unplugged.
///     The reference setup has a TV behind an HDMI switch that exposes nothing until the switch selects
///     this PC, so the layout editor cannot rely on the display being enumerable while it is edited.
/// </summary>
public sealed class KnownDisplay
{
    /// <summary>Stable monitor identity.</summary>
    public DisplayTargetIdentity? Target { get; set; }

    /// <summary>Modes the driver advertised the last time the display was active.</summary>
    public List<DisplayMode> Modes { get; set; } = [];

    /// <summary>Whether the display reported advanced colour support when last seen.</summary>
    public bool HdrSupported { get; set; }

    /// <summary>Highest scaling percentage the display offered, or zero when it is unknown.</summary>
    public int MaximumDpiPercent { get; set; }

    /// <summary>Last driver display controls, retained for editing this monitor while disconnected.</summary>
    public List<DisplayGpuCapability> GpuCapabilities { get; set; } = [];

    /// <summary>When the display was last observed as connected.</summary>
    public DateTimeOffset LastSeen { get; set; }
}
