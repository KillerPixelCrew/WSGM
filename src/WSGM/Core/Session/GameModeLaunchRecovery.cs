using System;
using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     What a Game Mode session owes the desktop if it does not come back cleanly.
///     The runtime owns this. Settings never writes it, so an editor open across a crash cannot discard
///     the layout that a recovery start has to restore.
/// </summary>
public sealed class GameModeLaunchRecovery
{
    /// <summary>Layout to restore, written before Explorer exits and cleared after leave.</summary>
    public DisplayLayout? PendingReturnLayout { get; set; }

    /// <summary>Captured desktop audio state to restore after a Game Mode session.</summary>
    public AudioProfilePreference? PendingReturnAudio { get; set; }

    /// <summary>Captured driver values for the controls Game Mode changes, retained until desktop restoration.</summary>
    public List<DisplayGpuPreference> PendingReturnDisplayGpu { get; set; } = [];

    /// <summary>When the pending layout was recorded.</summary>
    public DateTimeOffset? EnteredAt { get; set; }
}
