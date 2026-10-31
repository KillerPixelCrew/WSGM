using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Runs one coordinator's Game Mode entry attempts through <see cref="GameModeEntryTransaction" />
///     on a worker, owns the running attempt's cancellation, and honours a desktop request that
///     arrived while it ran once the attempt has settled.
/// </summary>
/// <param name="modes">The owning coordinator, whose transition gate the attempt holds.</param>
/// <param name="monitor">The Steam monitor the attempt keeps paused.</param>
internal sealed class GameModeEntry(SessionModes modes, SteamMonitor? monitor)
{
    // Both UI-thread only: set when an entry starts, read and cleared by the entry's settle
    // callback, which is posted back to the UI thread.
    private CancellationTokenSource? _cancellation;
    private bool _desktopRequested;

    /// <summary>Whether an entry attempt is running. UI thread.</summary>
    internal bool Active => _cancellation is not null;

    /// <summary>
    ///     Starts an entry attempt on a worker. The caller holds the coordinator's transition, which
    ///     the attempt ends.
    /// </summary>
    /// <param name="hooks">The session's half of the transition.</param>
    /// <param name="launch">The launch settings in force when the entry started.</param>
    /// <param name="wasInGameMode">Whether Explorer came back under an active Game Mode.</param>
    internal void Start(SessionModeHooks hooks, GameModeLaunchConfiguration launch, bool wasInGameMode)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _desktopRequested = false;
        // ReSharper disable once MethodSupportsCancellation
        _ = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                if (wasInGameMode)
                {
                    Log.Warn("Explorer returned while Game Mode was active; restoring Desktop state before re-entering Game Mode.");
                    if (!await hooks.Entry.ReturnToDesktopAsync(null, true).ConfigureAwait(false))
                    {
                        return;
                    }

                    cancellation.Token.ThrowIfCancellationRequested();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        modes.DesktopReturn.Reset();
                        monitor?.Paused = true;
                    });
                }

                var result = await new GameModeEntryTransaction(hooks.Entry, launch)
                    .RunAsync(cancellation.Token).ConfigureAwait(false);
                entered = result.Outcome == GameModeEntryOutcome.Entered;
                if (result.Warning is { } warning)
                {
                    await Dispatcher.UIThread
                        .InvokeAsync(() => modes.ReportWarning(warning));
                }
            }
            catch (Exception ex)
            {
                // The transaction returns every outcome, failed recovery included; only a bug lands here.
                Log.Error("Game-mode transition failed", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    modes.ReportWarning(SessionModes.ExplorerExitFailedWarning));
            }
            finally
            {
                try
                {
                    hooks.SteamUiBigPictureRequestSettled();
                    hooks.GameModeEntrySettled();
                }
                finally
                {
                    modes.EndTransition();
                    Dispatcher.UIThread.Post(() => Settle(cancellation, entered));
                }
            }
        });
    }

    /// <summary>
    ///     Records a desktop request against the running attempt and cancels it; the settle callback
    ///     honours the request once, on the UI thread, after the attempt ends.
    /// </summary>
    internal void RequestDesktop()
    {
        _desktopRequested = true;
        Cancel();
    }

    /// <summary>
    ///     Requests desktop recovery from an active entry. An in-flight Explorer/display
    ///     operation settles first, then cancellation returns through the shared recovery sequence.
    ///     UI thread only, like the field it reads.
    /// </summary>
    internal void Cancel()
    {
        _cancellation?.Cancel();
    }

    /// <summary>
    ///     Ends an entry attempt on the UI thread, where desktop requests arrive: a request that
    ///     landed during the attempt is honoured once, and only when the attempt entered Game Mode.
    /// </summary>
    /// <param name="cancellation">The attempt's cancellation.</param>
    /// <param name="entered">Whether the attempt entered Game Mode.</param>
    private void Settle(CancellationTokenSource cancellation, bool entered)
    {
        cancellation.Dispose();
        if (!ReferenceEquals(_cancellation, cancellation))
        {
            return;
        }

        _cancellation = null;
        var desktopRequested = _desktopRequested;
        _desktopRequested = false;
        if (desktopRequested && entered)
        {
            modes.EnterDesktopMode();
        }
    }
}
