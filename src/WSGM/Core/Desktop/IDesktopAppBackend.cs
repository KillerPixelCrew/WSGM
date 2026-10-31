using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Process operations kept separate from the session's restoration ownership.</summary>
internal interface IDesktopAppBackend
{
    /// <summary>Captures restorable current-session instances of an explicitly cataloged application.</summary>
    /// <param name="rule">Known process names and exit protocol to inspect.</param>
    /// <returns>Identity snapshots; capture failures propagate rather than losing restoration authority.</returns>
    IReadOnlyList<DesktopAppInstance> Capture(DesktopAppRule rule);

    /// <summary>Revalidates and stops one captured application using its approved exit protocol.</summary>
    /// <param name="instance">Captured identity; PID reuse must not grant authority over a replacement.</param>
    /// <param name="cancellationToken">Cancels waiting; does not undo an already-dispatched exit.</param>
    /// <returns>A task completing when that instance exits; failures or cancellation propagate.</returns>
    Task StopAsync(DesktopAppInstance instance, CancellationToken cancellationToken);

    /// <summary>Checks whether the captured executable is running in the current session.</summary>
    /// <param name="instance">Application rule and executable path to match.</param>
    /// <returns>True for any matching current executable instance, including a replacement PID.</returns>
    bool IsRunning(DesktopAppInstance instance);

    /// <summary>Restores one captured executable while preserving its integrity level.</summary>
    /// <param name="instance">Executable and restart arguments captured before takeover.</param>
    /// <param name="deadline">Absolute UTC budget for restoration and observing launch.</param>
    /// <returns>Dispatch certainty; Unknown must not trigger another launch attempt.</returns>
    Task<ScheduledTaskLaunchDisposition> RestartAsync(DesktopAppInstance instance, DateTimeOffset deadline);
}
