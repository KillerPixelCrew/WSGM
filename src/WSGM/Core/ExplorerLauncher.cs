using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

internal static class ExplorerLauncher
{
    internal static Task<ScheduledTaskLaunchDisposition> StartAsync(UserDataContext context,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        return StartAsync(ElevationCheck.IsCurrentProcessElevated() == true,
            token => UnelevatedLauncher.TryStartViaScheduledTaskAsync(context, ExplorerControl.ExplorerPath,
                "", deadline, cancellationToken: token), StartDirect, cancellationToken);
    }

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
