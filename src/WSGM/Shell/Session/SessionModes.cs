using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Session-mode coordinator: owns the game/desktop mode transitions and the gate that
///     serializes them. Its <see cref="SteamLifecycle" /> owner carries the shared Steam start + warning flow,
///     <see cref="GameModeEntry" /> the entry attempts and <see cref="DesktopReturn" /> the return.
///     ShellSession uses it at boot, the overlay's buttons drive it at runtime; OverlayController stays
///     the UI owner (lease lifecycle, window) and surfaces warnings via <see cref="SteamStartFailed" />.
/// </summary>
public sealed class SessionModes
{
    /// <summary>
    ///     The warning shown when explorer refused its orderly exit and the
    ///     session stayed in desktop mode (fail open, never a half game mode).
    /// </summary>
    public const string ExplorerExitFailedWarning =
        "Windows did not complete the Game Mode switch after enforced Explorer shutdown. Desktop recovery was requested.";

    /// <summary>
    ///     The warning shown when a dispatched Explorer may still be initializing, so WSGM
    ///     deliberately avoids creating a competing replacement taskbar.
    /// </summary>
    public const string ExplorerDesktopPendingWarning =
        "Windows did not restore Explorer after forced recovery. Try switching to Desktop again; see wsgm.log.";

    /// <summary>
    ///     The warning shown when the current desktop cannot safely supply a normal shell
    ///     launch owner, most commonly after upgrading beside an older job-bound Explorer.
    /// </summary>
    public const string ExplorerTakeoverRefusedWarning =
        "Windows did not complete preparation for Game Mode. Desktop recovery was requested; see wsgm.log.";

    // Upper bound for an unresponsive exit. Healthy and retired-shell paths finish on observation.
    internal static readonly TimeSpan ExplorerExitTimeout = TimeSpan.FromSeconds(30);

    private readonly ExplorerDesktopHost? _desktopHost;
    private readonly DesktopReturn? _desktopReturn;
    private readonly SessionModeHooks? _hooks;
    private readonly SteamMonitor? _monitor;
    private readonly SteamInputShim? _steamInputShim;
    private readonly ConfigStore? _store;

    // The running Explorer transition, completed when it ends; null while none runs.
    private TaskCompletionSource? _explorerTransition;

    private int _shutdownRequested;

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
    /// <param name="launchSteamDesktop">
    ///     Optional launcher receiving input-management, unelevated-launch and CEF switches; null
    ///     uses the production launcher.
    /// </param>
    internal SessionModes(AppConfig config, SteamMonitor? monitor, Func<bool> steamRunning,
        Func<bool> steamInstalled, Func<bool, bool, bool, bool>? launchSteamDesktop)
    {
        Config = config;
        _monitor = monitor;
        _desktopHost = null;
        SteamLifecycle = new SteamLifecycle(this, monitor, steamRunning, steamInstalled, launchSteamDesktop);
        GameModeEntry = new GameModeEntry(this, monitor);
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
        _desktopReturn = new DesktopReturn(this, desktopHost, monitor);
    }

    /// <summary>Starts, focuses, closes and relaunches Steam for this coordinator.</summary>
    internal SteamLifecycle SteamLifecycle { get; }

    /// <summary>Runs this coordinator's Game Mode entry attempts.</summary>
    internal GameModeEntry GameModeEntry { get; }

    /// <summary>Runs this coordinator's desktop return; live coordinators only.</summary>
    internal DesktopReturn DesktopReturn =>
        _desktopReturn ?? throw new InvalidOperationException("Preview modes cannot return to the desktop.");

    /// <summary>The configuration in force; replaced wholesale by <see cref="ApplyConfig" />.</summary>
    internal AppConfig Config { get; private set; }

    /// <summary>Whether application teardown has closed transition admission.</summary>
    internal bool ShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>The process-owned persistence and data roots; live coordinators only.</summary>
    internal ConfigStore Store =>
        _store ?? throw new InvalidOperationException("Preview modes cannot persist display recovery.");

    /// <summary>The process's Steam Input shim; live coordinators only.</summary>
    internal SteamInputShim SteamInputShim =>
        _steamInputShim ?? throw new InvalidOperationException("Preview modes cannot start Steam.");

    /// <summary>The session's half of every transition; live coordinators only.</summary>
    internal SessionModeHooks Hooks =>
        _hooks ?? throw new InvalidOperationException("Preview modes cannot change the session mode.");

    /// <summary>
    ///     True while Explorer is being brought up or down, or a requested Steam close is settling.
    ///     Mode-switch requests arriving in that window are ignored —
    ///     two concurrent explorer transitions produced exactly the device-observed
    ///     mess of duplicate shutdowns and refused tray hosts (2026-08-07).
    /// </summary>
    public bool TransitionInProgress => Volatile.Read(ref _explorerTransition) is not null
                                        || SteamLifecycle.Closing;

    /// <summary>
    ///     Raised on the UI thread when Steam could not be started or a session transition
    ///     needs a user-facing warning. No warning is delivered after shutdown starts.
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

    /// <summary>Surfaces a shell-transition warning on the UI thread through the overlay's existing warning path.</summary>
    /// <param name="warning">User-facing transition warning forwarded to SteamStartFailed subscribers.</param>
    /// <remarks>
    ///     Background Steam recovery must not construct an overlay window or update its view model
    ///     on the worker. Posting also keeps warning presentation from interrupting the transport gate.
    /// </remarks>
    internal void ReportWarning(string warning)
    {
        if (ShutdownRequested)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReportWarning(warning));
            return;
        }

        Log.Warn("Session transition warning: " + warning);
        SteamStartFailed?.Invoke(warning);
    }

    /// <summary>Asks the overlay's owner to show it because Steam left game mode with nothing behind it. UI thread.</summary>
    internal void RequestOverlayAfterSteamExit()
    {
        SteamExitShowOverlayRequested?.Invoke();
    }

    /// <summary>Tells listeners the desktop return is retiring Game Mode, before Explorer starts. UI thread.</summary>
    internal void RaiseDesktopModeStarting()
    {
        DesktopModeStarting?.Invoke();
    }

    /// <summary>
    ///     Applies a freshly loaded config (settings saved in another process).
    ///     Reloads replace the config wholesale, so no runtime state may live on it.
    /// </summary>
    /// <param name="config">Replacement configuration retained by this session; call on the UI thread.</param>
    public void ApplyConfig(AppConfig config)
    {
        Config = config;
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
        if (ShutdownRequested)
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
        SteamLifecycle.RequestShutdown();
        GameModeEntry.Cancel();
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

        await SteamLifecycle.CloseWork.ConfigureAwait(false);
    }

    /// <summary>Starts a transition unless shutdown, a Steam close or another transition is in progress.</summary>
    /// <param name="reason">What the transition is, for the log.</param>
    /// <returns>Whether the caller now holds the transition and must end it.</returns>
    internal bool TryBeginTransition(string reason)
    {
        if (ShutdownRequested)
        {
            Log.Warn($"Ignoring {reason}: application shutdown is in progress.");
            return false;
        }

        if (SteamLifecycle.Closing)
        {
            Log.Warn($"Ignoring {reason}: Steam is still closing.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _explorerTransition,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null) is not null)
        {
            Log.Warn($"Ignoring {reason}: an explorer transition is already in progress.");
            return false;
        }

        if (!ShutdownRequested)
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
        if (_desktopReturn is not { } desktopReturn)
        {
            Log.Info("Ignoring desktop-mode switch in preview-only SessionModes.");
            return;
        }

        if (GameModeEntry.Active)
        {
            // The entry's settle callback honours this once, on this thread, after the attempt ends.
            GameModeEntry.RequestDesktop();
            return;
        }

        if (desktopReturn.Complete)
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
                await desktopReturn.RunAsync(null, true).ConfigureAwait(false);
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

        var wasInGameMode = hooks.IsGameMode();
        if (wasInGameMode && !ExplorerControl.IsDesktopShellRunning())
        {
            SteamLifecycle.StartOrFocus();
            return;
        }

        if (!TryBeginTransition("game-mode switch"))
        {
            return;
        }

        DesktopReturn.Reset();
        Log.Info("Entering game mode.");
        // Desktop mode already pauses it, but make the transition transactional:
        // no Steam lifecycle edge may react until Explorer is confirmed gone.
        _monitor?.Paused = true;
        // The session's current configuration, read on this thread when the entry starts.
        GameModeEntry.Start(hooks, Config.GameModeLaunch, wasInGameMode);
    }
}
