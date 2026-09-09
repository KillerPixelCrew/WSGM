using System.Collections.Generic;
using WindowsDeviceControl;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Explicitly enabled session route automation, independent of Device Integration.</summary>
public sealed class DisplayRouteConfiguration
{
    /// <summary>Whether configured lifecycle bindings may run automatically.</summary>
    public bool Enabled { get; set; }
    /// <summary>Route selected before entering Game Mode.</summary>
    public DisplayRouteBinding? EnterGameMode { get; set; }
    /// <summary>Desktop profile and external route restored when leaving Game Mode.</summary>
    public DisplayRouteBinding? LeaveGameMode { get; set; }
    /// <summary>Route restored when the resident session starts on Desktop.</summary>
    public DisplayRouteBinding? DesktopStartup { get; set; }
    /// <summary>Route restored after waking on Desktop, unless Game Mode is entering.</summary>
    public DisplayRouteBinding? DesktopWake { get; set; }
}

/// <summary>Saved plugin action and optional Windows display target/profile for one lifecycle event.</summary>
public sealed class DisplayRouteBinding
{
    /// <summary>Configured provider identity; absent when only a display profile is requested.</summary>
    public PluginInstanceIdentity? Plugin { get; set; }
    /// <summary>Stable action ID within the provider.</summary>
    public string? ActionId { get; set; }
    /// <summary>Explicit action arguments. Missing values use admitted provider defaults.</summary>
    public Dictionary<string, PluginValue> Arguments { get; set; } = [];
    /// <summary>Stable display identity to wait for when entering Game Mode.</summary>
    public DisplayTargetIdentity? Target { get; set; }
    /// <summary>Captured WDC topology to apply after route selection on entry, or before it on exit.</summary>
    public DisplayProfile? Profile { get; set; }
    /// <summary>Total action, display wait and profile deadline, from 1 to 120 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
