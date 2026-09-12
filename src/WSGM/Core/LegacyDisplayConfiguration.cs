using System.Collections.Generic;
using WindowsDeviceControl;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>The retired display shapes, kept only so <see cref="ConfigMigrations"/> can read a
/// stored document that still uses them.
///
/// They live apart from <see cref="AppConfig"/> deliberately: nothing current writes them, and a
/// reader that finds one is looking at a file an older build saved.</summary>
internal sealed class LegacyDisplayRoutes
{
    /// <summary>Whether the retired route automation was switched on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Route selected before entering Game Mode.</summary>
    public LegacyDisplayRouteBinding? EnterGameMode { get; set; }

    /// <summary>Route restored when leaving Game Mode.</summary>
    public LegacyDisplayRouteBinding? LeaveGameMode { get; set; }

    /// <summary>Route restored when the resident session started on the desktop.</summary>
    public LegacyDisplayRouteBinding? DesktopStartup { get; set; }

    /// <summary>Route restored after waking on the desktop.</summary>
    public LegacyDisplayRouteBinding? DesktopWake { get; set; }
}

/// <summary>One retired lifecycle binding: an action, a display to wait for, and a raw profile.</summary>
internal sealed class LegacyDisplayRouteBinding
{
    /// <summary>Configured provider identity.</summary>
    public PluginInstanceIdentity? Plugin { get; set; }

    /// <summary>Stable action ID within the provider.</summary>
    public string? ActionId { get; set; }

    /// <summary>Explicit action arguments.</summary>
    public Dictionary<string, PluginValue> Arguments { get; set; } = [];

    /// <summary>Display identity the binding waited for.</summary>
    public DisplayTargetIdentity? Target { get; set; }

    /// <summary>Captured native topology, replayable but not editable.</summary>
    public DisplayProfile? Profile { get; set; }

    /// <summary>Combined action, wait and profile deadline in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>One monitor's retired desktop and game values, keyed by GDI names rather than by a
/// display identity Windows can still resolve.</summary>
internal sealed class LegacyMonitorDisplayProfile
{
    /// <summary>Registry device key of the monitor, which is not a CCD device path.</summary>
    public string MonitorId { get; set; } = "";

    /// <summary>GDI source name, such as <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Friendly monitor label captured for the settings UI.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Whether Windows reported advanced-colour support for this monitor.</summary>
    public bool HdrAvailable { get; set; }

    /// <summary>Values applied or captured in desktop mode.</summary>
    public LegacyDisplayModeValues Desktop { get; set; } = new();

    /// <summary>Values applied or captured in game mode.</summary>
    public LegacyDisplayModeValues Game { get; set; } = new();
}

/// <summary>Resolution, refresh rate, scaling and HDR for one monitor in one session mode.</summary>
internal sealed class LegacyDisplayModeValues
{
    /// <summary>Horizontal resolution in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Vertical resolution in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Refresh rate in hertz.</summary>
    public int RefreshRate { get; set; }

    /// <summary>Windows display scaling percentage.</summary>
    public int DpiPercent { get; set; } = 100;

    /// <summary>Whether advanced colour was enabled.</summary>
    public bool HdrEnabled { get; set; }
}
