using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Dispatches terminal Explorer recovery while preserving uncertainty across launch mechanisms.</summary>
internal static class ExplorerLauncher
{
    /// <summary>Prefers a medium-integrity scheduled task when recovering from an elevated process.</summary>
    /// <param name="context">User-data context for the temporary scheduled-task definition.</param>
    /// <param name="deadline">Absolute UTC deadline for scheduler dispatch.</param>
    /// <param name="cancellationToken">Checked before dispatch and forwarded to the scheduler.</param>
    /// <returns>Dispatch certainty, not desktop readiness; callers must observe the resulting shell.</returns>
    internal static Task<ScheduledTaskLaunchDisposition> StartAsync(UserDataContext context,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        return StartAsync(ElevationCheck.IsCurrentProcessElevated() == true,
            token => UnelevatedLauncher.TryStartViaScheduledTaskAsync(context, ExplorerControl.ExplorerPath,
                "", deadline, cancellationToken: token), StartDirect, cancellationToken);
    }

    /// <summary>Falls back to direct launch only when scheduler dispatch is known not to have occurred.</summary>
    /// <param name="elevated">Whether to attempt de-elevation through the scheduler first.</param>
    /// <param name="schedule">Bounded scheduler dispatch delegate.</param>
    /// <param name="startDirect">Last-resort direct launch delegate using current integrity.</param>
    /// <param name="cancellationToken">Cancels before either dispatch; forwarded to the scheduler.</param>
    /// <returns>The selected route's dispatch certainty; Unknown suppresses direct fallback.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before dispatch.</exception>
    internal static async Task<ScheduledTaskLaunchDisposition> StartAsync(bool elevated,
        Func<CancellationToken, Task<ScheduledTaskLaunchDisposition>> schedule,
        Func<ScheduledTaskLaunchDisposition> startDirect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (elevated)
        {
            var disposition = await schedule(cancellationToken).ConfigureAwait(false);
            if (disposition != ScheduledTaskLaunchDisposition.NotDispatched)
            {
                return disposition;
            }

            Log.Warn("Explorer de-elevation was not dispatched; using the current integrity as the last resort.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return startDirect();
    }

    private static ScheduledTaskLaunchDisposition StartDirect()
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(ExplorerControl.ExplorerPath) { UseShellExecute = true }
        };
        try
        {
            return process.Start() ? ScheduledTaskLaunchDisposition.Dispatched : ScheduledTaskLaunchDisposition.Unknown;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("Explorer launch consent was declined.");
            return ScheduledTaskLaunchDisposition.NotDispatched;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Explorer direct launch outcome could not be confirmed", ex);
            return ScheduledTaskLaunchDisposition.Unknown;
        }
    }
}
