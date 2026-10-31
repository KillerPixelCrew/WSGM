using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs the shared desktop return of one mode coordinator through <see cref="DesktopReturnSequence" />
///     and remembers whether the last one finished cleanly, so a repeated desktop request is a no-op.
/// </summary>
/// <param name="modes">The owning coordinator, whose Steam owner and warnings the return uses.</param>
/// <param name="host">The session's verified Explorer launch path.</param>
/// <param name="monitor">The Steam monitor the return pauses or resumes.</param>
internal sealed class DesktopReturn(SessionModes modes, ExplorerDesktopHost host, SteamMonitor? monitor)
{
    // UI-thread only: written when a return settles, cleared when an entry starts.
    private bool _complete;

    /// <summary>Whether the last return restored Explorer without a warning. UI thread.</summary>
    internal bool Complete => _complete;

    /// <summary>Forgets the last return, because an entry is leaving the desktop again. UI thread.</summary>
    internal void Reset()
    {
        _complete = false;
    }

    /// <summary>
    ///     Runs the shared desktop return: Big Picture closed, the desktop layout and audio
    ///     restored, Game Mode retired, Explorer restored, then the leave actions. Shows its own
    ///     warnings, including the pending-desktop one when Explorer was not restored.
    /// </summary>
    /// <param name="layout">The layout to restore, or null for the recorded or configured one.</param>
    /// <param name="runLeaveActions">Whether the configured leave actions run.</param>
    /// <returns>Whether Explorer was restored.</returns>
    internal async Task<bool> RunAsync(DisplayLayout? layout, bool runLeaveActions)
    {
        var hooks = modes.Hooks;
        var warnings = new List<string>();
        var backend = new DesktopReturnBackend(modes, host, hooks, layout, warnings);
        var restored = await DesktopReturnSequence.RunAsync(backend, runLeaveActions, (phase, ex) =>
        {
            Log.Error(phase + " failed", ex);
            warnings.Add(phase + ": " + ex.Message);
        }, Log.Info).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _complete = restored && warnings.Count == 0;
                monitor?.Paused = !restored;
                if (restored)
                {
                    modes.SteamLifecycle.EnsureDesktop();
                }
                else
                {
                    warnings.Add(SessionModes.ExplorerDesktopPendingWarning);
                }

                if (warnings.Count > 0)
                {
                    modes.ReportWarning(string.Join(" ", warnings));
                }
            });
        }
        finally
        {
            hooks.SteamUiBigPictureRequestSettled();
        }

        Log.Info($"Desktop return completed: restored={restored}, warnings={warnings.Count}, "
                 + $"complete={_complete}.");
        return restored;
    }
}
