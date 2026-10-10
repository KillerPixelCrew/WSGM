using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>A driver display preference retained independently of the monitor's current route.</summary>
public sealed class DisplayGpuPreference
{
    /// <summary>Physical display identity, or null for a driver-wide setting.</summary>
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

/// <summary>A remembered driver capability used to edit connected or disconnected displays.</summary>
public sealed class DisplayGpuCapability
{
    /// <summary>Physical display identity, or null for a driver-wide setting.</summary>
    public DisplayTargetIdentity? Target { get; set; }

    /// <summary>Stable graphics provider identity.</summary>
    public string? PluginId { get; set; }

    /// <summary>Last descriptor advertised by the driver. Runtime writes validate a fresh descriptor.</summary>
    public CapabilityDescriptor? Descriptor { get; set; }

    /// <summary>Last readable or accepted value, when the driver supplied one.</summary>
    public PluginValue? ObservedValue { get; set; }
}
