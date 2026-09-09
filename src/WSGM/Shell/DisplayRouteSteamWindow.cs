using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

internal static class DisplayRouteSteamWindow
{
    internal static async Task<bool> PlaceAsync(DisplayTargetIdentity target, Func<bool> cancelled, CancellationToken cancellationToken)
    {
        long started = Environment.TickCount64;
        while (Environment.TickCount64 - started < 15000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cancelled()) { return false; }
            var window = WindowFinder.FindWindow(Steam.ProcessNames, Steam.BigPictureWindowClass);
            if (window != 0)
            {
                var path = DisplayTopology.CaptureActive().Paths.FirstOrDefault(path => target.Matches(path.Target));
                return path is not null && DisplayWindowPlacement.TryPlace(window, path.SourceName);
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }
}
