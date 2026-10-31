using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Everything Game Mode entry and leave do beyond starting Steam.
///     One record covers both directions because entry and leave are two halves of one transaction: the
///     layout Game Mode applies is only safe to write if this configuration also says how to get back.
/// </summary>
public sealed class GameModeLaunchConfiguration
{
    /// <summary>Whether entry applies a saved layout or only today's scaling handling.</summary>
    public GameModeLaunchKind Kind { get; set; }

    /// <summary>Layout applied on entry. Only used when <see cref="Kind" /> is Custom.</summary>
    public DisplayLayout? GameLayout { get; set; }

    /// <summary>
    ///     Display to wait for before applying the layout. The wait has no timeout; the splash
    ///     carries a Cancel button instead.
    /// </summary>
    public DisplayTargetIdentity? WaitForDisplay { get; set; }

    /// <summary>Actions run in order before the display wait. The first failure stops entry.</summary>
    public List<PluginActionStep> EnterActions { get; set; } = [];

    /// <summary>Which arrangement leaving Game Mode restores.</summary>
    public GameModeReturn Return { get; set; }

    /// <summary>Layout restored when <see cref="Return" /> is DesktopLayout.</summary>
    public DisplayLayout? DesktopLayout { get; set; }

    /// <summary>Driver display values applied after the Game Mode layout settles.</summary>
    public List<DisplayGpuPreference> GameDisplayGpu { get; set; } = [];

    /// <summary>Driver display values applied after the desktop layout settles.</summary>
    public List<DisplayGpuPreference> DesktopDisplayGpu { get; set; } = [];

    /// <summary>Audio preferences applied after the Game Mode layout settles.</summary>
    public AudioProfilePreference? GameAudio { get; set; }

    /// <summary>Audio preferences applied after the desktop layout settles.</summary>
    public AudioProfilePreference? DesktopAudio { get; set; }

    /// <summary>Actions run after the desktop is back. Every step runs; failures are reported.</summary>
    public List<PluginActionStep> LeaveActions { get; set; } = [];

    /// <summary>Actions run once when the resident session starts on the desktop.</summary>
    public List<PluginActionStep> DesktopStartupActions { get; set; } = [];

    /// <summary>Actions run after waking on the desktop, unless Game Mode is entering.</summary>
    public List<PluginActionStep> DesktopWakeActions { get; set; } = [];

    /// <summary>Displays remembered for the layout editor, present or not.</summary>
    public List<KnownDisplay> KnownDisplays { get; set; } = [];

    /// <summary>Remembered driver display and driver-wide capabilities for offline editing.</summary>
    public List<DisplayGpuCapability> KnownGpuCapabilities { get; set; } = [];
}
