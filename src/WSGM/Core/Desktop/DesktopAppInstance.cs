using System;

namespace WSGM.Core;

/// <summary>An exact current-session process captured before desktop takeover.</summary>
/// <param name="Rule">Exit and restart protocol selected for this process.</param>
/// <param name="ProcessId">Captured PID; must be revalidated with start time and image before stopping.</param>
/// <param name="StartTime">Process creation time in UTC, protecting against PID reuse.</param>
/// <param name="ExecutablePath">Full captured image path used for identity checks and restoration.</param>
/// <param name="Elevated">Whether restoration must retain high integrity rather than use a medium task.</param>
internal sealed record DesktopAppInstance(
    DesktopAppRule Rule,
    int ProcessId,
    DateTime StartTime,
    string ExecutablePath,
    bool Elevated = false);
