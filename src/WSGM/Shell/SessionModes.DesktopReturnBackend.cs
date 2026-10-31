using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class SessionModes
{
    /// <summary>The desktop return's effects over this coordinator, the Explorer host and the session's hooks.</summary>
    private sealed class DesktopReturnBackend(
        SessionModes modes,
        ExplorerDesktopHost host,
        SessionModeHooks hooks,
        DisplayLayout? layout,
        List<string> warnings) : IDesktopReturnBackend
    {
        // The recovery record read once by the layout phase and reused by the audio phase.
        private GameModeLaunchRecovery? _recovery;

        public Task ExitBigPictureAsync()
        {
            return modes.ExitBigPictureAndSettleAsync();
        }

        public async Task<bool> RestoreLayoutAsync()
        {
            var current = modes.Store.Read().RequireConfig();
            _recovery = current.GameModeLaunchRecovery;
            DisplayScale.ApplyDesktopMode(modes.Store, current);
            var services = hooks.Entry;
            var layoutWarning = await services.ApplyReturnLayoutAsync(layout, _recovery).ConfigureAwait(false);
            if (layoutWarning is not null)
            {
                warnings.Add(layoutWarning);
            }

            var graphicsWarning = await services.ApplyReturnDisplayGpuAsync(_recovery).ConfigureAwait(false);
            if (graphicsWarning is not null)
            {
                warnings.Add(graphicsWarning);
            }

            return layoutWarning is null && graphicsWarning is null;
        }

        public async Task<bool> RestoreAudioAsync()
        {
            // A layout phase that failed before its read leaves the record to be read here.
            var recovery = _recovery ?? modes.Store.Read().RequireConfig().GameModeLaunchRecovery;
            var warning = await hooks.Entry.ApplyReturnAudioAsync(recovery).ConfigureAwait(false);
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            return warning is null;
        }

        public async Task RetireGameModeAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => modes.DesktopModeStarting?.Invoke());
        }

        public async Task<bool> RestoreExplorerAsync()
        {
            if (Volatile.Read(ref modes._shutdownRequested) != 0 || ApplicationShutdownRequest.SessionEnding)
            {
                return false;
            }

            var result = await RestoreDesktopSafelyAsync(host, "Explorer desktop restoration failed",
                    hooks.ShutdownCancellation)
                .ConfigureAwait(false);
            var restored = result.Outcome is not ExplorerDesktopOutcome.Failed;
            if (!restored)
            {
                // The whole desktop return is abandoned here, leaving game mode retired and no
                // desktop. One warning among the patch traffic hid that for a whole session; say it
                // once, as an error, with the reason the observer recorded.
                Log.Error("Desktop return abandoned: Explorer was not restored "
                          + $"({result.Route}, {result.Detail}, {result.Elapsed.TotalMilliseconds:0} ms).");
                return false;
            }

            if (result.Outcome is ExplorerDesktopOutcome.Degraded)
            {
                if (result.MatchesCapturedQuality)
                {
                    Log.Info("Desktop shell restored with the captured job-bound characteristics; diagnostic quality remains Degraded.");
                }
                else
                {
                    warnings.Add("Desktop shell: restored but unverified (" + result.Detail + ").");
                }
            }

            await Dispatcher.UIThread.InvokeAsync(hooks.DesktopReady);
            return true;
        }

        public async Task RunLeaveActionsAsync()
        {
            if (Volatile.Read(ref modes._shutdownRequested) != 0)
            {
                return;
            }

            var steps = await hooks.Entry.RunLeaveActionsAsync().ConfigureAwait(false);
            warnings.AddRange(steps.Where(step => !step.Succeeded)
                .Select(step => "Leave Game Mode action: " + step.Detail));
        }

        public Task ClearPendingReturnAsync()
        {
            return hooks.Entry.PersistPendingReturnAsync(null, null);
        }
    }
}
