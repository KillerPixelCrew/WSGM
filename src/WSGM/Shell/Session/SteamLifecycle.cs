using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Starts, focuses, closes and relaunches the session's Steam client for <see cref="SessionModes" />:
///     the shared Steam start + warning flow, the exit policy, the Big Picture close of a desktop return
///     and the restart that activates Steam's debugging port.
/// </summary>
/// <remarks>
///     Owned by one <see cref="SessionModes" />, whose configuration, warnings, transition gate and
///     shutdown flag it uses. Its own close work counts as a transition there.
/// </remarks>
internal sealed class SteamLifecycle
{
    /// <summary>The warning shown when Steam Big Picture could not be started.</summary>
    internal const string BigPictureStartFailedWarning = "Couldn't start Steam Big Picture.";

    /// <summary>The warning shown when the required Steam installation cannot be found.</summary>
    private const string SteamNotFoundWarning =
        "Steam was not found on this PC. Install Steam — WSGM is Steam-exclusive.";

    /// <summary>The warning shown when the windowed desktop Steam client could not be started.</summary>
    private const string SteamStartFailedWarning = "Couldn't start Steam.";

    private const uint WmClose = 0x0010;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);

    /// <summary>How long Steam stays closed before an automatic relaunch.</summary>
    private static readonly TimeSpan SteamRelaunchDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long the Steam UI retraction may delay the Big Picture request. Bounded so a
    ///     broken CEF session can never block the mode switch itself.
    /// </summary>
    private static readonly TimeSpan SteamUiPrepareTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long the desktop return waits for Steam's Big Picture window to actually go away,
    ///     per close attempt. The rest of the return — the display-scale restore and the Explorer
    ///     restart — runs underneath that window otherwise, and on 2026-09-26 it did: Steam's CEF
    ///     renderer had stopped answering, nothing consumed the close URL, and the fresh Explorer
    ///     never answered a liveness probe for the whole 20 s restore budget.
    /// </summary>
    /// <remarks>
    ///     Two attempts plus the retraction below are the worst case the user waits before Explorer
    ///     starts, so the whole exit is deliberately shorter than that 20 s budget. Big Picture
    ///     normally closes well inside the first attempt.
    /// </remarks>
    private static readonly TimeSpan BigPictureCloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How often the Big Picture close wait re-reads Steam's window.</summary>
    private static readonly TimeSpan BigPictureClosePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     How long the Steam UI retraction may delay the Big Picture close. Shorter than the
    ///     request budget: on the way out the retraction is a courtesy to Steam's rebuild, and the
    ///     one case that needs it most is the one where Steam has stopped answering entirely.
    /// </summary>
    private static readonly TimeSpan SteamUiDesktopPrepareTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _homeLaunchGate = new();
    private readonly Func<bool, bool, bool, bool> _launchSteamDesktop;
    private readonly SessionModes _modes;
    private readonly SteamMonitor? _monitor;
    private readonly CancellationTokenSource _steamControlCancellation = new();
    private readonly SemaphoreSlim _steamControlGate = new(1, 1);
    private readonly Func<bool> _steamInstalled;
    private readonly Func<bool> _steamRunning;

    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;
    private IDisposable? _pendingSteamRelaunch;
    private int _steamCefRecoveryInProgress;
    private int _steamClosedByUser;
    private volatile Task _steamCloseWork = Task.CompletedTask;

    /// <summary>Creates the Steam owner of one mode coordinator and subscribes to its monitor's exits.</summary>
    /// <param name="modes">The owning coordinator.</param>
    /// <param name="monitor">Borrowed monitor whose exit notifications drive relaunch policy.</param>
    /// <param name="steamRunning">Fresh Steam-process presence probe.</param>
    /// <param name="steamInstalled">Steam installation probe.</param>
    /// <param name="launchSteamDesktop">
    ///     Optional launcher receiving input-management, unelevated-launch and CEF switches; null
    ///     uses the production launcher.
    /// </param>
    internal SteamLifecycle(SessionModes modes, SteamMonitor? monitor, Func<bool> steamRunning,
        Func<bool> steamInstalled, Func<bool, bool, bool, bool>? launchSteamDesktop)
    {
        _modes = modes;
        _monitor = monitor;
        _steamRunning = steamRunning ?? throw new ArgumentNullException(nameof(steamRunning));
        _steamInstalled = steamInstalled ?? throw new ArgumentNullException(nameof(steamInstalled));
        _launchSteamDesktop = launchSteamDesktop ?? LaunchSteamDesktop;
        if (_monitor is not null)
        {
            _monitor.SteamExited += OnSteamExited;
        }
    }

    /// <summary>
    ///     Whether the user closed Steam deliberately. The monitor's pause only covers a
    ///     transition, so this is what keeps the desktop session from starting Steam straight back up
    ///     after an explicit Close Steam. Cleared by any request that wants Steam running again.
    /// </summary>
    internal bool ClosedByUser => Volatile.Read(ref _steamClosedByUser) != 0;

    /// <summary>Whether a requested Steam close is still settling, which counts as a transition.</summary>
    internal bool Closing => !_steamCloseWork.IsCompleted;

    /// <summary>The requested Steam close still running, or a completed task.</summary>
    internal Task CloseWork => _steamCloseWork;

    /// <summary>Stops relaunching and controlling Steam during application teardown.</summary>
    internal void RequestShutdown()
    {
        _steamControlCancellation.Cancel();
        _pendingSteamRelaunch?.Dispose();
        _pendingSteamRelaunch = null;
        if (_monitor is not null)
        {
            _monitor.SteamExited -= OnSteamExited;
        }
    }

    /// <summary>Applies the Steam exit policy. UI thread: the monitor raises its exit from its own tick.</summary>
    private void OnSteamExited()
    {
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.ShowOverlay:
                _modes.RequestOverlayAfterSteamExit();
                return;
            case SteamExitReaction.RelaunchBigPicture:
            case SteamExitReaction.RelaunchDesktop:
                Log.Info($"Steam exited — auto-relaunching in {SteamRelaunchDelay.TotalSeconds:0} s.");
                _pendingSteamRelaunch?.Dispose();
                _pendingSteamRelaunch = DispatcherTimer.RunOnce(RelaunchSteamAfterExit, SteamRelaunchDelay);
                return;
            case SteamExitReaction.Ignore:
            default:
                Log.Info("Steam exited — leaving it closed.");
                return;
        }
    }

    /// <summary>
    ///     Re-decides at fire time: a config reload replaces the configuration wholesale, the
    ///     session may have changed mode or begun shutting down, and the user may have closed
    ///     Steam while the delay ran.
    /// </summary>
    private void RelaunchSteamAfterExit()
    {
        _pendingSteamRelaunch = null;
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.RelaunchBigPicture:
                StartOrFocus();
                return;
            case SteamExitReaction.RelaunchDesktop:
                EnsureDesktop();
                return;
            case SteamExitReaction.Ignore:
            case SteamExitReaction.ShowOverlay:
            default:
                Log.Info("Auto-relaunch skipped: the session no longer wants Steam started.");
                return;
        }
    }

    /// <summary>
    ///     Reads the live session state the policy needs. Explorer's presence is the same
    ///     signal the overlay's own mode button uses to tell desktop from game mode.
    /// </summary>
    private SteamExitReaction DecideSteamExitReaction()
    {
        if (_modes.ShutdownRequested)
        {
            return SteamExitReaction.Ignore;
        }

        return SteamExitPolicy.Decide(
            !ExplorerControl.IsDesktopShellRunning(),
            _modes.Config.SteamAutoRelaunch,
            _monitor?.Paused == true,
            ClosedByUser);
    }

    /// <summary>
    ///     Starts the windowed Steam client a desktop session is expected to have, so it
    ///     inherits WSGM's integrity instead of the user's own autostart. No-op while shutting down,
    ///     when Steam already runs, or after the user closed Steam deliberately.
    /// </summary>
    internal void EnsureDesktop()
    {
        if (Volatile.Read(ref _steamCefRecoveryInProgress) != 0)
        {
            return;
        }

        if (_modes.ShutdownRequested)
        {
            Log.Info("Ignoring desktop Steam start: application shutdown is in progress.");
            return;
        }

        if (Volatile.Read(ref _steamClosedByUser) != 0)
        {
            Log.Info("Skipping desktop Steam start: Steam was closed deliberately.");
            return;
        }

        if (_steamRunning())
        {
            return;
        }

        if (!_steamInstalled())
        {
            Log.Warn("Desktop Steam start skipped: no Steam installation was detected.");
            return;
        }

        Log.Info("Starting Steam (desktop mode, no Big Picture).");
        // Read at start time, not captured: a config reload replaces the configuration wholesale.
        var config = _modes.Config;
        if (!_launchSteamDesktop(config.SteamInputManagementEnabled, config.SteamLaunchUnelevated, config.Cef.Enabled))
        {
            _modes.ReportWarning(SteamStartFailedWarning);
        }
    }

    private bool LaunchSteamDesktop(bool manageInput, bool unelevated, bool cefEnabled)
    {
        return Steam.LaunchDesktop(_modes.Store.Context, _modes.SteamInputShim, manageInput, unelevated, cefEnabled)
            .Started;
    }

    /// <summary>
    ///     Asks Steam to leave Big Picture (Steam keeps running). No-op if
    ///     Steam isn't running.
    /// </summary>
    internal static void ExitBigPicture()
    {
        // Live check, not the up-to-5 s-stale monitor poll: entering desktop mode
        // right after Steam started must still send the close URL.
        if (!Steam.IsRunning)
        {
            return;
        }

        Log.Info("Exiting Steam Big Picture.");
        AppLauncher.StartProtocol(Steam.CloseBigPictureUrl);
    }

    /// <summary>
    ///     The desktop return's Big Picture exit: retract injected Steam UI and close the
    ///     transport, ask Steam to leave Big Picture, then wait for that window to go away before the
    ///     rest of the return runs. No-op when Steam isn't running.
    /// </summary>
    /// <returns>Completion after the close attempts settle or their waits expire; the window may still exist on failure.</returns>
    internal async Task ExitBigPictureAndSettleAsync()
    {
        // Live check, not the up-to-5 s-stale monitor poll: entering desktop mode
        // right after Steam started must still send the close URL.
        if (!Steam.IsRunning)
        {
            return;
        }

        await PrepareSteamUiAsync(
                _modes.Hooks.PrepareSteamUiForDesktopAsync,
                "Big Picture close",
                SteamUiDesktopPrepareTimeout)
            .ConfigureAwait(false);
        ExitBigPicture();
        if (await WaitForBigPictureToCloseAsync(BigPictureCloseTimeout).ConfigureAwait(false))
        {
            return;
        }

        // A hung renderer may ignore the protocol request; try WM_CLOSE directly before returning.
        var window = await Task.Run(Steam.FindBigPictureWindow).ConfigureAwait(false);
        if (window == IntPtr.Zero)
        {
            return;
        }

        var hung = NativeMethods.IsHungAppWindow(window);
        Log.Warn($"Steam's Big Picture window (hwnd 0x{window:X}) outlived the close request "
                 + $"(hung={hung}); posting WM_CLOSE to it directly.");
        NativeMethods.PostMessageW(window, WmClose, 0, 0);
        if (await WaitForBigPictureToCloseAsync(BigPictureCloseTimeout).ConfigureAwait(false))
        {
            return;
        }

        Log.Warn("Steam ignored both Big Picture close requests; enforcing Steam shutdown before restoring the desktop.");
        if (!await Steam.StopAsync(TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None,
                    entireProcessTree: true)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("Steam remained active after forced shutdown during desktop return.");
        }
    }

    /// <summary>Waits, bounded, for Steam's Big Picture window to disappear.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>Whether the window was gone before the timeout.</returns>
    private static async Task<bool> WaitForBigPictureToCloseAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (!await Task.Run(() => Steam.IsBigPictureVisible).ConfigureAwait(false))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(BigPictureClosePollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Runs one bounded Steam UI retraction before a Big Picture mode change. A broken CEF
    ///     session delays the switch by at most <see cref="SteamUiPrepareTimeout" /> and never blocks it.
    /// </summary>
    /// <remarks>
    ///     The budget is handed to the owner, which closes its transport when the budget expires: only
    ///     stopping the wait here left a slow retraction sending patch traffic into the front-end Steam
    ///     was already rebuilding for the request that followed. The wait keeps its own bound as well.
    /// </remarks>
    private static async Task PrepareSteamUiAsync(Func<CancellationToken, Task> prepare, string request,
        TimeSpan budget)
    {
        using var deadline = new CancellationTokenSource(budget);
        try
        {
            var work = prepare(deadline.Token);
            var first = await Task
                .WhenAny(work, Task.Delay(budget + TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
            if (first == work)
            {
                await work.ConfigureAwait(false);
            }
            else
            {
                Log.Warn($"Steam UI retraction did not return before the {request}; "
                         + "continuing with the transition and its transport closed.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI retraction before the {request} failed: {ex.Message}");
        }
    }

    /// <summary>Closes Steam within a bounded grace, then forces exit if needed. UI thread.</summary>
    /// <remarks>Pauses the monitor before stopping, owns its worker, and keeps automatic relaunch off.</remarks>
    internal void Close()
    {
        if (_modes.ShutdownRequested)
        {
            return;
        }

        Volatile.Write(ref _steamClosedByUser, 1);
        _monitor?.Paused = true;
        _pendingSteamRelaunch?.Dispose();
        _pendingSteamRelaunch = null;
        if (!_steamCloseWork.IsCompleted)
        {
            return;
        }

        _steamCloseWork = Task.Run(async () =>
        {
            var token = _steamControlCancellation.Token;
            var acquired = false;
            try
            {
                await _steamControlGate.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                if (!await Steam.StopAsync(token, entireProcessTree: true).ConfigureAwait(false))
                {
                    _modes.ReportWarning(
                        "Steam could not be closed, including forced shutdown. Check wsgm.log for details.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Session shutdown cancels any stop that has not yet completed.
            }
            finally
            {
                if (acquired)
                {
                    _steamControlGate.Release();
                }
            }
        });
        Log.Observe(_steamCloseWork, "Closing Steam");
    }

    /// <summary>
    ///     Start and focus are the same operation: steam://open/bigpicture
    ///     re-activates a running Big Picture (UIPI-proof) and boots Steam when it
    ///     isn't running. Re-arms the monitor (desktop mode and close-Steam pause it).
    ///     Failures surface through <see cref="SessionModes.SteamStartFailed" />.
    /// </summary>
    internal void StartOrFocus()
    {
        if (Volatile.Read(ref _steamCefRecoveryInProgress) != 0)
        {
            return;
        }

        if (_modes.ShutdownRequested)
        {
            Log.Info("Ignoring Steam start/focus: application shutdown is in progress.");
            return;
        }

        if (!_steamCloseWork.IsCompleted)
        {
            return;
        }

        Volatile.Write(ref _steamClosedByUser, 0);
        _monitor?.Paused = false;
        if (_monitor?.IsAlive == true)
        {
            Focus();
            return;
        }

        if (!TryBeginHomeLaunch())
        {
            return;
        }

        try
        {
            var warning = StartBigPicture();
            if (warning is not null)
            {
                _modes.ReportWarning(warning);
            }
        }
        finally
        {
            EndHomeLaunch();
        }
    }

    /// <summary>
    ///     The entry's one Big Picture request, made while the monitor stays paused: retracts the
    ///     Steam UI first, then starts Steam or re-activates the running client into Big Picture.
    /// </summary>
    /// <returns>A warning when Big Picture could not be started, otherwise null.</returns>
    internal async Task<string?> RequestBigPictureWhilePausedAsync()
    {
        Volatile.Write(ref _steamClosedByUser, 0);
        await PrepareSteamUiAsync(
                _modes.Hooks.PrepareSteamUiForBigPictureAsync,
                "Big Picture request",
                SteamUiPrepareTimeout)
            .ConfigureAwait(false);

        // Live check, not the up-to-5 s-stale monitor poll: a desktop session leaves Steam running
        // windowed, so the protocol has to re-activate that client into Big Picture rather than
        // start a second cold one.
        if (!Steam.IsRunning)
        {
            return StartBigPicture();
        }

        Focus(true);
        return null;
    }

    /// <summary>
    ///     Requests a Big Picture launch using the current configuration, unless shutdown has begun.
    /// </summary>
    /// <returns>
    ///     A user-facing launch warning, or null when launch was accepted or shutdown suppressed it. Null does not
    ///     establish Steam or Big Picture readiness.
    /// </returns>
    internal string? StartBigPicture()
    {
        if (Volatile.Read(ref _steamCefRecoveryInProgress) != 0)
        {
            return "Steam is restarting to enable WSGM's Steam integration.";
        }

        if (_modes.ShutdownRequested)
        {
            Log.Info("Ignoring Big Picture start: application shutdown is in progress.");
            return null;
        }

        if (!Steam.IsInstalled)
        {
            Log.Warn("Steam is not installed — showing overlay instead.");
            return SteamNotFoundWarning;
        }

        Log.Info("Starting Steam Big Picture.");
        // Read at launch time, not captured: a config reload replaces the configuration wholesale, and
        // both the cold start and the auto-relaunch after Steam exits come through here.
        var result = Steam.LaunchBigPicture(_modes.Store.Context, _modes.SteamInputShim,
            _modes.Config.SteamInputManagementEnabled, _modes.Config.SteamLaunchUnelevated,
            _modes.Config.Cef.Enabled);
        return result.Started ? null : BigPictureStartFailedWarning;
    }

    /// <summary>
    ///     Restarts Steam with debugging enabled after its browser process started without it, including
    ///     forced exit. Returns once the replacement was launched; its port is the gate worker's to observe.
    /// </summary>
    /// <param name="bigPicture">Whether the replacement should open in Big Picture.</param>
    /// <param name="cancellationToken">Interrupts recovery and prevents later stop or launch effects.</param>
    /// <returns>True when recovery was attempted; false when session policy deferred it.</returns>
    internal async Task<bool> RestartForCefAsync(bool bigPicture, CancellationToken cancellationToken)
    {
        if (ClosedByUser || !_modes.Config.Cef.Enabled
                         || !_modes.TryBeginTransition("Steam CEF activation"))
        {
            return false;
        }

        Volatile.Write(ref _steamCefRecoveryInProgress, 1);
        var wasPaused = false;
        var acquired = false;
        try
        {
            await _steamControlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
        }
        catch
        {
            Volatile.Write(ref _steamCefRecoveryInProgress, 0);
            _modes.EndTransition();
            throw;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                wasPaused = _monitor?.Paused == true;
                if (_monitor is not null)
                {
                    _monitor.Paused = true;
                }

                _pendingSteamRelaunch?.Dispose();
                _pendingSteamRelaunch = null;
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (ClosedByUser || !_modes.Config.Cef.Enabled || _modes.ShutdownRequested)
            {
                return false;
            }

            if (!SteamCef.EnsureRemoteDebuggingEnabled(Steam.InstallDirectory, enabled: true))
            {
                _modes.ReportWarning("Couldn't enable Steam debugging. Check wsgm.log for the file access error.");
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (ClosedByUser || !_modes.Config.Cef.Enabled || _modes.ShutdownRequested)
            {
                return false;
            }

            // The listener may have appeared while recovery was acquiring the transition.
            if (SteamCef.GetRemoteDebuggingState(out _) != SteamCefDebuggingState.Closed)
            {
                return true;
            }

            Log.Warn("Steam's browser process started without remote debugging; restarting Steam to activate it.");
            if (!await Steam.StopAsync(cancellationToken).ConfigureAwait(false))
            {
                _modes.ReportWarning(
                    "Steam could not be stopped for WSGM's Steam integration. Check wsgm.log for details.");
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (ClosedByUser || _modes.ShutdownRequested)
            {
                return true;
            }

            var config = _modes.Config;
            var result = bigPicture
                ? Steam.LaunchBigPicture(_modes.Store.Context, _modes.SteamInputShim,
                    config.SteamInputManagementEnabled, config.SteamLaunchUnelevated, config.Cef.Enabled)
                : Steam.LaunchDesktop(_modes.Store.Context, _modes.SteamInputShim,
                    config.SteamInputManagementEnabled, config.SteamLaunchUnelevated, config.Cef.Enabled);
            result.Process?.Dispose();
            if (!result.Started)
            {
                _modes.ReportWarning("Couldn't restart Steam for WSGM's Steam integration.");
                return true;
            }

            // No wait for the port here: the replacement may update or sync first, and the gate worker
            // reads its browser process's command line when it appears.
            Log.Info("Steam restarted with remote debugging enabled.");
            return true;
        }
        finally
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_monitor is not null)
                    {
                        _monitor.Paused = wasPaused || ClosedByUser;
                    }
                });
            }
            finally
            {
                if (acquired)
                {
                    _steamControlGate.Release();
                }

                Volatile.Write(ref _steamCefRecoveryInProgress, 0);
                _modes.EndTransition();
            }
        }
    }

    /// <summary>Brings Steam Big Picture to the foreground when the monitor sees it alive.</summary>
    /// <param name="force">
    ///     Whether to skip the monitor's up-to-5 s-stale liveness poll because the
    ///     caller has already established that Steam is running.
    /// </param>
    internal void Focus(bool force = false)
    {
        if (!_steamCloseWork.IsCompleted || Volatile.Read(ref _steamCefRecoveryInProgress) != 0)
        {
            return;
        }

        if (_modes.ShutdownRequested)
        {
            Log.Info("Ignoring Steam focus: application shutdown is in progress.");
            return;
        }

        if (force || _monitor?.IsAlive == true)
        {
            // Protocol re-activation self-focuses even against an elevated target.
            AppLauncher.StartProtocol(Steam.OpenBigPictureUrl);
        }
    }

    private bool TryBeginHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            if (_homeLaunchInProgress || DateTime.UtcNow - _lastHomeLaunchUtc < HomeLaunchCooldown)
            {
                Log.Warn("Skipping duplicate home-app start request.");
                return false;
            }

            _homeLaunchInProgress = true;
            return true;
        }
    }

    private void EndHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            _homeLaunchInProgress = false;
            _lastHomeLaunchUtc = DateTime.UtcNow;
        }
    }
}
