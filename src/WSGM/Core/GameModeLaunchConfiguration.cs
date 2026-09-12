using System;
using System.Collections.Generic;
using WindowsDeviceControl;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>How Game Mode arranges displays when it starts.</summary>
public enum GameModeLaunchKind
{
    /// <summary>Start on the display Windows already calls primary, adjusting scaling only.</summary>
    Default,
    /// <summary>Apply a saved layout, optionally after waiting for a display and running actions.</summary>
    Custom,
}

/// <summary>What the desktop returns to when Game Mode ends.</summary>
public enum GameModeReturn
{
    /// <summary>Whatever was on screen when Game Mode was entered.</summary>
    EntryArrangement,
    /// <summary>A saved desktop layout, regardless of what entry found.</summary>
    DesktopLayout,
}

/// <summary>One plugin action in a session automation list.</summary>
public sealed class PluginActionStep
{
    /// <summary>Configured provider identity.</summary>
    public PluginInstanceIdentity? Plugin { get; set; }

    /// <summary>Stable action ID within the provider.</summary>
    public string? ActionId { get; set; }

    /// <summary>Explicit action arguments. Missing values use admitted provider defaults.</summary>
    public Dictionary<string, PluginValue> Arguments { get; set; } = [];

    /// <summary>Deadline for this step alone, from 1 to 120 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>A display WSGM has seen, remembered so it can be configured while it is unplugged.
///
/// The reference setup has a TV behind an HDMI switch that exposes nothing until the switch selects
/// this PC, so the layout editor cannot rely on the display being enumerable while it is edited.
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

    /// <summary>When the display was last observed as connected.</summary>
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Everything Game Mode entry and leave do beyond starting Steam.
///
/// One record covers both directions because entry and leave are two halves of one transaction: the
/// layout Game Mode applies is only safe to write if this configuration also says how to get back.
/// </summary>
public sealed class GameModeLaunchConfiguration
{
    /// <summary>Whether entry applies a saved layout or only today's scaling handling.</summary>
    public GameModeLaunchKind Kind { get; set; }

    /// <summary>Layout applied on entry. Only used when <see cref="Kind"/> is Custom.</summary>
    public DisplayLayout? GameLayout { get; set; }

    /// <summary>Display to wait for before applying the layout. The wait has no timeout; the splash
    /// carries a Cancel button instead.</summary>
    public DisplayTargetIdentity? WaitForDisplay { get; set; }

    /// <summary>Actions run in order before the display wait. The first failure stops entry.</summary>
    public List<PluginActionStep> EnterActions { get; set; } = [];

    /// <summary>Which arrangement leaving Game Mode restores.</summary>
    public GameModeReturn Return { get; set; }

    /// <summary>Layout restored when <see cref="Return"/> is DesktopLayout.</summary>
    public DisplayLayout? DesktopLayout { get; set; }

    /// <summary>Actions run after the desktop is back. Every step runs; failures are reported.</summary>
    public List<PluginActionStep> LeaveActions { get; set; } = [];

    /// <summary>Actions run once when the resident session starts on the desktop.</summary>
    public List<PluginActionStep> DesktopStartupActions { get; set; } = [];

    /// <summary>Actions run after waking on the desktop, unless Game Mode is entering.</summary>
    public List<PluginActionStep> DesktopWakeActions { get; set; } = [];

    /// <summary>Displays remembered for the layout editor, present or not.</summary>
    public List<KnownDisplay> KnownDisplays { get; set; } = [];
}

/// <summary>What a Game Mode session owes the desktop if it does not come back cleanly.
///
/// The runtime owns this. Settings never writes it, so an editor open across a crash cannot discard
/// the layout that a recovery start has to restore.</summary>
public sealed class GameModeLaunchRecovery
{
    /// <summary>Layout to restore, written before Explorer exits and cleared after leave.</summary>
    public DisplayLayout? PendingReturnLayout { get; set; }

    /// <summary>When the pending layout was recorded.</summary>
    public DateTimeOffset? EnteredAt { get; set; }
}
