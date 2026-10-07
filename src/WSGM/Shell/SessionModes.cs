using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Session-mode coordinator: owns the game/desktop mode transitions
///     (explorer, display scale, Steam open/close, monitor pause) and
///     the shared Steam start + warning flow. ShellSession uses it at boot, the
///     overlay's buttons drive it at runtime; OverlayController stays the UI owner
///     (lease lifecycle, window) and surfaces warnings via <see cref="SteamStartFailed" />.
/// </summary>
public sealed class SessionModes
{
    /// <summary>The warning shown when the required Steam installation cannot be found.</summary>
    private const string SteamNotFoundWarning =
        "Steam was not found on this PC. Install Steam — WSGM is Steam-exclusive.";

    /// <summary>The warning shown when Steam Big Picture could not be started.</summary>
    public const string BigPictureStartFailedWarning = "Couldn't start Steam Big Picture.";

    /// <summary>The warning shown when the windowed desktop Steam client could not be started.</summary>
    private const string SteamStartFailedWarning = "Couldn't start Steam.";

    /// <summary>
    ///     The warning shown when explorer refused its orderly exit and the
    ///     session stayed in desktop mode (fail open, never a half game mode).
    /// </summary>
    public const string ExplorerExitFailedWarning =
        "Game Mode entry stopped because Explorer did not finish leaving. Desktop recovery was requested.";

    /// <summary>
    ///     The warning shown when a dispatched Explorer may still be initializing, so WSGM
    ///     deliberately avoids creating a competing replacement taskbar.
    /// </summary>
    public const string ExplorerDesktopPendingWarning =
        "Windows Explorer did not finish starting. WSGM will not create a competing taskbar; sign out or reboot to recover the desktop.";

    /// <summary>
    ///     The warning shown when the current desktop cannot safely supply a normal shell
    ///     launch owner, most commonly after upgrading beside an older job-bound Explorer.
    /// </summary>
    public const string ExplorerTakeoverRefusedWarning =
        "Game Mode could not safely take over this Windows Explorer. Desktop mode was preserved; sign out or reboot once before retrying.";

    private const uint WmClose = 0x0010;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);

    /// <summary>How long Steam stays closed before an automatic relaunch.</summary>
    private static readonly TimeSpan SteamRelaunchDelay = TimeSpan.FromSeconds(10);

    // Upper bound for an unresponsive exit. Healthy and retired-shell paths finish on observation.
    internal static readonly TimeSpan ExplorerExitTimeout = TimeSpan.FromSeconds(30);

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

    private readonly ExplorerDesktopHost? _desktopHost;
    private readonly Lock _homeLaunchGate = new();
    private readonly SessionModeHooks? _hooks;
    private readonly Func<bool, bool, bool, bool> _launchSteamDesktop;
    private readonly SteamMonitor? _monitor;
    private readonly SteamInputShim? _steamInputShim;
    private readonly Func<bool> _steamInstalled;
    private readonly Func<bool> _steamRunning;
    private readonly ConfigStore? _store;

    private AppConfig _config;

    // Both UI-thread only: set when an entry starts, read and cleared by the entry's settle
    // callback, which is posted back to the UI thread.
    private bool _desktopRequested;
    private bool _desktopReturnComplete;
    private CancellationTokenSource? _entryCancellation;

    // The running Explorer transition, completed when it ends; null while none runs.
    private TaskCompletionSource? _explorerTransition;

    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;
    private IDisposable? _pendingSteamRelaunch;
    private int _shutdownRequested;
    private int _steamClosedByUser;

    /// <summary>
    ///     Creates a preview-only coordinator. Desktop/game transition requests are inert
    ///     because Settings and other safe previews do not own an Explorer recovery host.
    /// </summary>
    /// <param name="config">The initial configuration controlling display posture and launch behavior.</param>
    /// <param name="monitor">
    ///     The optional Steam monitor to pause or resume during transitions. Its exits drive the
    ///     auto-relaunch policy for the monitor's lifetime.
    /// </param>
    public SessionModes(AppConfig config, SteamMonitor? monitor)
        : this(config, monitor, static () => Steam.IsRunning, static () => Steam.IsInstalled, null)
    {
    }

    /// <summary>Creates the mode owner with the desktop Steam probes and launch operation it uses.</summary>
    /// <param name="config">Configuration retained until ApplyConfig replaces it.</param>
    /// <param name="monitor">Borrowed monitor whose exit notifications drive relaunch policy.</param>
    /// <param name="steamRunning">Fresh Steam-process presence probe.</param>
    /// <param name="steamInstalled">Steam installation probe.</param>
    /// <param name="launchSteamDesktop">Optional launcher receiving input-management, unelevated-launch and CEF switches; null uses the production launcher.</param>
    internal SessionModes(AppConfig config, SteamMonitor? monitor, Func<bool> steamRunning,
        Func<bool> steamInstalled, Func<bool, bool, bool, bool>? launchSteamDesktop)
    {
        _config = config;
        _monitor = monitor;
        _steamRunning = steamRunning ?? throw new ArgumentNullException(nameof(steamRunning));
        _steamInstalled = steamInstalled ?? throw new ArgumentNullException(nameof(steamInstalled));
        _launchSteamDesktop = launchSteamDesktop ?? LaunchSteamDesktop;
        _desktopHost = null;
        if (_monitor is not null)
        {
            _monitor.SteamExited += OnSteamExited;
        }
    }

    /// <summary>Creates the coordinator with the session-owned verified Explorer launch path.</summary>
    /// <param name="config">The initial configuration controlling display posture and launch behavior.</param>
    /// <param name="monitor">The optional Steam monitor to pause or resume during transitions.</param>
    /// <param name="desktopHost">The session's verified Explorer launch path.</param>
    /// <param name="store">The process-owned persistence and data roots.</param>
    /// <param name="steamInputShim">The process's Steam Input shim, reconciled before every Steam cold start.</param>
    /// <param name="hooks">The session's half of every transition.</param>
    internal SessionModes(
        AppConfig config,
        SteamMonitor? monitor,
        ExplorerDesktopHost desktopHost,
        ConfigStore store,
        SteamInputShim steamInputShim,
        SessionModeHooks hooks)
        : this(config, monitor)
    {
        ArgumentNullException.ThrowIfNull(desktopHost);
        ArgumentNullException.ThrowIfNull(hooks);
        _desktopHost = desktopHost;
        _store = store;
        _steamInputShim = steamInputShim;
        _hooks = hooks;
    }

    private ConfigStore Store =>
        _store ?? throw new InvalidOperationException("Preview modes cannot persist display recovery.");

    private SteamInputShim SteamInputShim =>
        _steamInputShim ?? throw new InvalidOperationException("Preview modes cannot start Steam.");

    private SessionModeHooks Hooks =>
        _hooks ?? throw new InvalidOperationException("Preview modes cannot change the session mode.");

    /// <summary>
    ///     Whether the user closed Steam deliberately. The monitor's pause only covers a
    ///     transition, so this is what keeps the desktop session from starting Steam straight back up
    ///     after an explicit Close Steam. Cleared by any request that wants Steam running again.
    /// </summary>
    public bool SteamClosedByUser => Volatile.Read(ref _steamClosedByUser) != 0;

    /// <summary>
    ///     True while explorer is being brought up or down (mode switch or the
    ///     boot takeover). Mode-switch requests arriving in that window are ignored —
    ///     two concurrent explorer transitions produced exactly the device-observed
    ///     mess of duplicate shutdowns and refused tray hosts (2026-08-07).
    /// </summary>
    public bool TransitionInProgress => Volatile.Read(ref _explorerTransition) is not null;

    /// <summary>
    ///     Raised (on the caller's thread) when <see cref="StartOrFocusSteam" />
    ///     could not bring Steam up, with the user-facing warning text.
    /// </summary>
    public event Action<string>? SteamStartFailed;

    /// <summary>
    ///     Raised on the UI thread when Steam exited in game mode with auto-relaunch off: the overlay is the
    ///     only surface left, so its owner shows it.
    /// </summary>
    public event Action? SteamExitShowOverlayRequested;

    /// <summary>
    ///     Raised (on the UI thread — the transition posts back there after the
    ///     off-thread Big Picture close) during a desktop-mode transition, after Steam
    ///     left Big Picture but BEFORE explorer starts. Listeners that own per-game-mode
    ///     resources which must not coexist with explorer (the tray host's Shell_TrayWnd
    ///     — explorer's taskbar creates its own) tear down here.
    /// </summary>
    public event Action? DesktopModeStarting;

    /// <summary>
    ///     Raised (on the UI thread — the transition completes there after the
    ///     off-thread explorer shutdown) after a game-mode transition has removed
    ///     explorer from the session. Listeners recreate per-game-mode resources
    ///     (tray host) here.
    /// </summary>
    public event Action? GameModeEntered;

    /// <summary>Surfaces a shell-transition warning through the overlay's existing warning path.</summary>
    /// <param name="warning">User-facing transition warning forwarded to SteamStartFailed subscribers.</param>
    internal void ReportWarning(string warning)
    {
        SteamStartFailed?.Invoke(warning);
    }

    /// <summary>
    ///     Applies a freshly loaded config (settings saved in another process).
    ///     Reloads replace the config wholesale, so no runtime state may live on it.
    /// </summary>
    /// <param name="config">Replacement configuration retained by this session; call on the UI thread.</param>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
    }

    /// <summary>Applies the Steam exit policy. UI thread: the monitor raises its exit from its own tick.</summary>
    private void OnSteamExited()
    {
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.ShowOverlay:
                SteamExitShowOverlayRequested?.Invoke();
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
    ///     Re-decides at fire time: a config reload replaces <c>_config</c> wholesale, the
    ///     session may have changed mode or begun shutting down, and the user may have closed
    ///     Steam while the delay ran.
    /// </summary>
    private void RelaunchSteamAfterExit()
    {
        _pendingSteamRelaunch = null;
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.RelaunchBigPicture:
                StartOrFocusSteam();
                return;
            case SteamExitReaction.RelaunchDesktop:
                EnsureSteamDesktop();
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
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            return SteamExitReaction.Ignore;
        }

        return SteamExitPolicy.Decide(
            !ExplorerControl.IsDesktopShellRunning(),
            _config.SteamAutoRelaunch,
            _monitor?.Paused == true,
            SteamClosedByUser);
    }

    /// <summary>
    ///     Applies game mode's 100% display scaling. Windows exclusively
    ///     owns device posture and touch-keyboard policy.
    /// </summary>
    public void ApplyGameModePosture()
    {
        DisplayScale.ApplyGameMode(Store);
    }

    /// <summary>
    ///     Brings up the game-mode surfaces and lets the Steam monitor react again. Called on
    ///     the UI thread once the entry transaction has committed.
    /// </summary>
    internal void CommitGameMode()
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            throw new OperationCanceledException("Application shutdown refuses Game Mode entry.");
        }

        GameModeEntered?.Invoke();
        _monitor?.Paused = false;
    }

    /// <summary>
    ///     Marks an explorer transition as running (boot takeover uses this
    ///     directly; the mode switches go through <see cref="TryBeginTransition" />).
    /// </summary>
    internal void BeginTransition()
    {
        Interlocked.CompareExchange(ref _explorerTransition,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);
    }

    /// <summary>Ends the running transition and releases its waiters. Always pair with Begin/TryBegin.</summary>
    internal void EndTransition()
    {
        Interlocked.Exchange(ref _explorerTransition, null)?.TrySetResult();
    }

    /// <summary>Prevents another shell transition from starting during application teardown.</summary>
    internal void RequestShutdown()
    {
        Volatile.Write(ref _shutdownRequested, 1);
        _pendingSteamRelaunch?.Dispose();
        _pendingSteamRelaunch = null;
        if (_monitor is not null)
        {
            _monitor.SteamExited -= OnSteamExited;
        }

        CancelGameModeEntry();
    }

    /// <summary>
    ///     Waits for the one already-running shell transition to leave its Explorer and UI
    ///     boundaries. The application shutdown coordinator supplies the sole outer deadline.
    /// </summary>
    /// <returns>Completion when no transition remains. This method has no timeout; the shutdown owner must bound its wait.</returns>
    internal async Task WaitForTransitionAsync()
    {
        while (Volatile.Read(ref _explorerTransition) is { } transition)
        {
            await transition.Task.ConfigureAwait(false);
        }
    }

    internal bool TryBeginTransition(string reason)
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Warn($"Ignoring {reason}: application shutdown is in progress.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _explorerTransition,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null) is not null)
        {
            Log.Warn($"Ignoring {reason}: an explorer transition is already in progress.");
            return false;
        }

        if (Volatile.Read(ref _shutdownRequested) == 0)
        {
            return true;
        }

        EndTransition();
        Log.Warn($"Ignoring {reason}: application shutdown is in progress.");
        return false;
    }

    /// <summary>
    ///     Desktop mode: stop reacting to Steam (no auto-relaunch, no overlay
    ///     pop), drop Steam out of Big Picture, bring the desktop up. Returns
    ///     immediately — the blocking Big Picture close, display-scale restore and
    ///     explorer start run off the UI thread so the overlay never freezes; only the
    ///     monitor pause (before anything can react to Steam leaving) and
    ///     <see cref="DesktopModeStarting" /> stay UI-thread work.
    /// </summary>
    public void EnterDesktopMode()
    {
        var desktopHost = _desktopHost;
        if (desktopHost is null)
        {
            Log.Info("Ignoring desktop-mode switch in preview-only SessionModes.");
            return;
        }

        if (_entryCancellation is not null)
        {
            // The entry's settle callback honours this once, on this thread, after the attempt ends.
            _desktopRequested = true;
            CancelGameModeEntry();
            return;
        }

        if (_desktopReturnComplete)
        {
            return;
        }

        if (!TryBeginTransition("desktop-mode switch"))
        {
            return;
        }

        Log.Info("Entering desktop mode.");
        _monitor?.Paused = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await ReturnToDesktopAsync(null, true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Desktop-mode transition failed", ex);
            }
            finally
            {
                EndTransition();
            }
        });
    }

    /// <summary>
    ///     Runs the shared desktop return: Big Picture closed, the desktop layout and audio
    ///     restored, Game Mode retired, Explorer restored, then the leave actions. Shows its own
    ///     warnings, including the pending-desktop one when Explorer was not restored.
    /// </summary>
    /// <param name="layout">The layout to restore, or null for the recorded or configured one.</param>
    /// <param name="runLeaveActions">Whether the configured leave actions run.</param>
    /// <returns>Whether Explorer was restored.</returns>
    internal async Task<bool> ReturnToDesktopAsync(
        DisplayLayout? layout, bool runLeaveActions)
    {
        var hooks = Hooks;
        var warnings = new List<string>();
        var backend = new DesktopReturnBackend(this, _desktopHost!, hooks, layout, warnings);
        var restored = await DesktopReturnSequence.RunAsync(backend, runLeaveActions, (phase, ex) =>
        {
            Log.Error(phase + " failed", ex);
            warnings.Add(phase + ": " + ex.Message);
        }, Log.Info).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _desktopReturnComplete = restored && warnings.Count == 0;
                _monitor?.Paused = !restored;
                if (restored)
                {
                    EnsureSteamDesktop();
                }
                else
                {
                    warnings.Add(ExplorerDesktopPendingWarning);
                }

                if (warnings.Count > 0)
                {
                    SteamStartFailed?.Invoke(string.Join(" ", warnings));
                }
            });
        }
        finally
        {
            hooks.SteamUiBigPictureRequestSettled();
        }

        return restored;
    }

    /// <summary>
    ///     Starts the windowed Steam client a desktop session is expected to have, so it
    ///     inherits WSGM's integrity instead of the user's own autostart. No-op while shutting down,
    ///     when Steam already runs, or after the user closed Steam deliberately.
    /// </summary>
    public void EnsureSteamDesktop()
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
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
        // Read at start time, not captured: a config reload replaces _config wholesale.
        var config = _config;
        if (!_launchSteamDesktop(config.SteamInputManagementEnabled, config.SteamLaunchUnelevated, config.Cef.Enabled))
        {
            SteamStartFailed?.Invoke(SteamStartFailedWarning);
        }
    }

    private bool LaunchSteamDesktop(bool manageInput, bool unelevated, bool cefEnabled)
    {
        return Steam.LaunchDesktop(Store.Context, SteamInputShim, manageInput, unelevated, cefEnabled).Started;
    }

    /// <summary>
    ///     Game mode: runs the entry transaction on a worker and returns immediately.
    ///     Monitoring stays paused and game-mode resources are not created until Explorer is verifiably
    ///     gone. Every step before that exit is undoable and cancellable; see
    ///     <see cref="GameModeEntryTransaction" /> for the order and why Big Picture now follows the
    ///     exit rather than preceding it.
    /// </summary>
    public void EnterGameMode()
    {
        if (_desktopHost is null || _hooks is not { } hooks)
        {
            Log.Info("Ignoring game-mode switch in preview-only SessionModes.");
            return;
        }

        if (hooks.IsGameMode())
        {
            StartOrFocusSteam();
            return;
        }

        if (!TryBeginTransition("game-mode switch"))
        {
            return;
        }

        _desktopReturnComplete = false;
        Log.Info("Entering game mode.");
        // Desktop mode already pauses it, but make the transition transactional:
        // no Steam lifecycle edge may react until Explorer is confirmed gone.
        _monitor?.Paused = true;
        var cancellation = new CancellationTokenSource();
        _entryCancellation = cancellation;
        _desktopRequested = false;
        // The session's current configuration, read on this thread when the entry starts.
        var launch = _config.GameModeLaunch;
        // ReSharper disable once MethodSupportsCancellation
        _ = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                var result = await new GameModeEntryTransaction(hooks.Entry, launch)
                    .RunAsync(cancellation.Token).ConfigureAwait(false);
                entered = result.Outcome == GameModeEntryOutcome.Entered;
                if (result.Warning is { } warning)
                {
                    await Dispatcher.UIThread
                        .InvokeAsync(() => SteamStartFailed?.Invoke(warning));
                }
            }
            catch (Exception ex)
            {
                // The transaction returns every outcome, failed recovery included; only a bug lands here.
                Log.Error("Game-mode transition failed", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    SteamStartFailed?.Invoke(ExplorerExitFailedWarning));
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
                    EndTransition();
                    Dispatcher.UIThread.Post(() => SettleEntry(cancellation, entered));
                }
            }
        });
    }

    /// <summary>
    ///     Ends an entry attempt on the UI thread, where desktop requests arrive: a request that
    ///     landed during the attempt is honoured once, and only when the attempt entered Game Mode.
    /// </summary>
    /// <param name="cancellation">The attempt's cancellation.</param>
    /// <param name="entered">Whether the attempt entered Game Mode.</param>
    private void SettleEntry(CancellationTokenSource cancellation, bool entered)
    {
        cancellation.Dispose();
        if (!ReferenceEquals(_entryCancellation, cancellation))
        {
            return;
        }

        _entryCancellation = null;
        var desktopRequested = _desktopRequested;
        _desktopRequested = false;
        if (desktopRequested && entered)
        {
            EnterDesktopMode();
        }
    }

    /// <summary>
    ///     Requests desktop recovery from an active entry. An in-flight Explorer/display
    ///     operation settles first, then cancellation returns through the shared recovery sequence.
    ///     UI thread only, like the field it reads.
    /// </summary>
    internal void CancelGameModeEntry()
    {
        _entryCancellation?.Cancel();
    }

    /// <summary>
    ///     Runs the verified desktop restore, converting an exception into the fail-open
    ///     result. The exception may have happened after an anchor/scheduler launch crossed its
    ///     boundary, so it reports the launch as dispatched — Unknown is unsafe for recreating a
    ///     competing Shell_TrayWnd.
    /// </summary>
    private static async Task<ExplorerDesktopResult> RestoreDesktopSafelyAsync(
        ExplorerDesktopHost desktopHost,
        string failureContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return await desktopHost.RestoreDesktopAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(failureContext, ex);
            return new ExplorerDesktopResult(
                ExplorerDesktopOutcome.Failed,
                ExplorerDesktopRoute.ScheduledTaskRecovery,
                0,
                0,
                ex.Message,
                true,
                false,
                TimeSpan.Zero);
        }
    }

    /// <summary>
    ///     Asks Steam to leave Big Picture (Steam keeps running). No-op if
    ///     Steam isn't running.
    /// </summary>
    public static void ExitBigPicture()
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
                Hooks.PrepareSteamUiForDesktopAsync,
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

        Log.Error("Steam kept its Big Picture window through both close requests "
                  + $"(hwnd 0x{window:X}, hung={NativeMethods.IsHungAppWindow(window)}). The desktop "
                  + "is being restored underneath it, so Explorer may come up behind a window "
                  + "Steam is no longer servicing.");
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
    private static async Task PrepareSteamUiAsync(Func<Task> prepare, string request, TimeSpan budget)
    {
        try
        {
            var work = prepare();
            var first = await Task
                .WhenAny(work, Task.Delay(budget))
                .ConfigureAwait(false);
            if (first == work)
            {
                await work.ConfigureAwait(false);
            }
            else
            {
                Log.Warn($"Steam UI retraction did not finish before the {request}; "
                         + "continuing with the transition.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI retraction before the {request} failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Deliberately stops Steam (graceful steam://exit). Pauses the monitor
    ///     first so neither auto-relaunch nor the exit-overlay reaction fires.
    /// </summary>
    public void CloseSteam()
    {
        Volatile.Write(ref _steamClosedByUser, 1);
        _monitor?.Paused = true;
        Log.Info("Closing Steam (steam://exit).");
        AppLauncher.StartProtocol(Steam.ExitUrl);
    }

    /// <summary>
    ///     Start and focus are the same operation: steam://open/bigpicture
    ///     re-activates a running Big Picture (UIPI-proof) and boots Steam when it
    ///     isn't running. Re-arms the monitor (desktop mode and close-Steam pause it).
    ///     Failures surface through <see cref="SteamStartFailed" />.
    /// </summary>
    public void StartOrFocusSteam()
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Info("Ignoring Steam start/focus: application shutdown is in progress.");
            return;
        }

        Volatile.Write(ref _steamClosedByUser, 0);
        _monitor?.Paused = false;
        if (_monitor?.IsAlive == true)
        {
            FocusSteam();
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
                SteamStartFailed?.Invoke(warning);
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
                Hooks.PrepareSteamUiForBigPictureAsync,
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

        FocusSteam(true);
        return null;
    }

    /// <summary>
    ///     Requests a Big Picture launch using the current configuration, unless shutdown has begun.
    /// </summary>
    /// <returns>A user-facing launch warning, or null when launch was accepted or shutdown suppressed it. Null does not establish Steam or Big Picture readiness.</returns>
    public string? StartBigPicture()
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
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
        // Read at launch time, not captured: a config reload replaces _config wholesale, and both
        // the cold start and the auto-relaunch after Steam exits come through here.
        var result = Steam.LaunchBigPicture(Store.Context, SteamInputShim, _config.SteamInputManagementEnabled,
            _config.SteamLaunchUnelevated, _config.Cef.Enabled);
        return result.Started ? null : BigPictureStartFailedWarning;
    }

    /// <summary>Brings Steam Big Picture to the foreground when the monitor sees it alive.</summary>
    /// <param name="force">
    ///     Whether to skip the monitor's up-to-5 s-stale liveness poll because the
    ///     caller has already established that Steam is running.
    /// </param>
    public void FocusSteam(bool force = false)
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
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

    private sealed class DesktopReturnBackend(
        SessionModes modes,
        ExplorerDesktopHost host,
        SessionModeHooks hooks,
        DisplayLayout? layout,
        List<string> warnings) : IDesktopReturnBackend
    {
        public Task ExitBigPictureAsync()
        {
            return modes.ExitBigPictureAndSettleAsync();
        }

        public async Task<bool> RestoreLayoutAsync()
        {
            DisplayScale.ApplyDesktopMode(modes.Store, modes.Store.Read().RequireConfig());
            var services = hooks.Entry;
            if (layout is not null)
            {
                var result = await services.ApplyLayoutAsync(layout, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!result.Applied)
                {
                    warnings.Add("Desktop display layout: " + DisplayText.Layout(result));
                }

                return result.Applied;
            }

            var warning = await services.ApplyReturnLayoutAsync().ConfigureAwait(false);
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            return warning is null;
        }

        public async Task<bool> RestoreAudioAsync()
        {
            var warning = await hooks.Entry.ApplyReturnAudioAsync().ConfigureAwait(false);
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
                warnings.Add("Desktop shell: restored but unverified (" + result.Detail + ").");
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

/// <summary>The session's half of the live mode transitions, supplied once when the coordinator is built.</summary>
/// <param name="Entry">
///     The displays, audio, plugin actions, splash and desktop recovery record the entry and the
///     desktop return work through.
/// </param>
/// <param name="PrepareSteamUiForBigPictureAsync">
///     Awaited (bounded) immediately before a transition asks Steam for Big Picture, so the owner can
///     retract injected Steam UI state and close its transport first: the request rebuilds Steam's
///     whole front-end, and that rebuild must see stock client state.
/// </param>
/// <param name="PrepareSteamUiForDesktopAsync">
///     Awaited (bounded) immediately before the desktop return asks Steam to close Big Picture, for
///     the same reason: closing rebuilds Steam's front-end just as opening does.
/// </param>
/// <param name="SteamUiBigPictureRequestSettled">
///     Invoked when a transition that may have requested Big Picture has settled, on every outcome,
///     so the owner can lift the hold above. Idempotent.
/// </param>
/// <param name="GameModeEntrySettled">
///     Invoked once an entry attempt has settled, on every outcome, so the owner can dismiss the
///     splash. Idempotent.
/// </param>
/// <param name="DesktopReady">Invoked on the UI thread once Explorer is restored.</param>
/// <param name="IsGameMode">Whether the session is in Game Mode.</param>
/// <param name="ShutdownCancellation">Cancels an in-flight desktop restoration when the session stops.</param>
internal sealed record SessionModeHooks(
    IGameModeEntryBackend Entry,
    Func<Task> PrepareSteamUiForBigPictureAsync,
    Func<Task> PrepareSteamUiForDesktopAsync,
    Action SteamUiBigPictureRequestSettled,
    Action GameModeEntrySettled,
    Action DesktopReady,
    Func<bool> IsGameMode,
    CancellationToken ShutdownCancellation = default);
