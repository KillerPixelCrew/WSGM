using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>A remembered driver capability used to edit connected or disconnected displays.</summary>
public sealed class DisplayGpuCapability
{
    /// <summary>Physical display identity. Untargeted driver controls are excluded from the display editor.</summary>
    public DisplayTargetIdentity? Target { get; set; }

    /// <summary>Stable graphics provider identity.</summary>
    public string? PluginId { get; set; }

    /// <summary>Last descriptor advertised by the driver. Runtime writes validate a fresh descriptor.</summary>
    public CapabilityDescriptor? Descriptor { get; set; }

    /// <summary>Last readable or accepted value, when the driver supplied one.</summary>
    public PluginValue? ObservedValue { get; set; }
}
