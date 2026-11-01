using WindowsDeviceControl;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>A driver display preference retained independently of the monitor's current route.</summary>
public sealed class DisplayGpuPreference
{
    /// <summary>Physical display identity. Legacy driver-wide entries with no target are retained but not applied.</summary>
    public DisplayTargetIdentity? Target { get; set; }

    /// <summary>Stable graphics provider identity.</summary>
    public string? PluginId { get; set; }

    /// <summary>Stable capability identity within the provider.</summary>
    public string? CapabilityId { get; set; }

    /// <summary>Last live instance identity, retained as metadata rather than a physical display identity.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Explicit value applied when this layout is selected.</summary>
    public PluginValue Value { get; set; }
}
