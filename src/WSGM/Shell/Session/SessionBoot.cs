using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>
///     The session's way into its first mode: the boot cover and the Game Mode entry splash, the
///     service-boot takeover from Explorer, the startup apps with their relaunch watcher, the boot
///     launch sequence that asks Steam for Big Picture, and the verified desktop start.
/// </summary>
/// <param name="state">The session's live state.</param>
/// <param name="steamUi">The master switch and tab boot sync the launch sequence uses.</param>
/// <param name="surfaces">The mode switches a takeover or a preserved desktop settles through.</param>
/// <param name="desktopActions">The desktop startup actions a verified desktop start runs.</param>
/// <param name="modes">Reads the session's mode coordinator.</param>
/// <param name="monitor">Reads the session's Steam monitor.</param>
/// <param name="overlay">Reads the session's overlay.</param>
/// <param name="desktopHost">Reads the session's verified Explorer launch path.</param>
internal sealed class SessionBoot(
    SessionState state,
    SteamUiServices steamUi,
    GameModeSurfaces surfaces,
    DesktopLifecycleActions desktopActions,
    Func<SessionModes?> modes,
    Func<SteamMonitor?> monitor,
    Func<OverlayController?> overlay,
    Func<ExplorerDesktopHost?> desktopHost)
{
    /// <summary>How long a start waits for the interactive desktop before it proceeds anyway.</summary>
    private static readonly TimeSpan InputDesktopWait = TimeSpan.FromSeconds(60);

    // Non-null from the moment the service-boot splash becomes interactive until
    // the worker releases SessionModes' transition gate. The splash's desktop
    // recovery cancels through this owner instead of racing that gate.
    private BootTakeoverCancellation? _bootTakeover;

    private Task? _bootWork;
    private bool _gameModeEntryActive;
    private bool _holdingEntrySplash;
    private BootSplash? _splash;
    private StartupAppWatcher? _startupWatcher;
    private bool _tookOverFromExplorer;

    /// <summary>The boot worker: the takeover, the launch sequence or the desktop start, or a completed task.</summary>
    internal Task Work => _bootWork ?? Task.CompletedTask;

    /// <summary>Resolves Desktop startup from current preferences and the explicit resident modifier.</summary>
    /// <param name="startMode">The current configuration, which may be newer than the service's boot manifest.</param>
    /// <param name="desktopResident">Whether the launcher explicitly requested Desktop residency.</param>
    /// <returns>True when startup must preserve or recover Explorer instead of entering Game Mode.</returns>
    internal static bool StartsOnDesktop(SessionStartMode startMode, bool desktopResident)
    {
        return desktopResident || startMode == SessionStartMode.Desktop;
    }

    /// <summary>
    ///     Covers the screen with the boot splash when configured. Its timeout starts when the launch
    ///     sequence asks Steam for Big Picture, not here, because a service boot waits for the input
    ///     desktop and Explorer first. Opening the overlay dismisses the boot cover; a Game Mode entry
    ///     splash stays up, since the entry is still running behind it.
    /// </summary>
    internal void ShowBootSplashIfEnabled()
    {
        if (!state.Config.BootSplashEnabled || _splash is not null)
        {
            return;
        }

        // A Switch to desktop press before the takeover begins cancels it as soon as it starts.
        _bootTakeover ??= new BootTakeoverCancellation();
        _splash = new BootSplash(state.Config, SwitchToDesktopFromSplash);
        _splash.Show();
    }

    /// <summary>Arms the boot cover's Big Picture detection and timeout; an entry splash arms itself.</summary>
    private void ArmBootSplash()
    {
        if (!_holdingEntrySplash)
        {
            _splash?.ArmSteamDetection();
        }
    }

    /// <summary>Dismisses whichever splash is up. UI thread.</summary>
    /// <param name="reason">Why, for the log.</param>
    internal void DismissSplash(string reason)
    {
        _splash?.Dismiss(reason);
    }

    /// <summary>Ends the Game Mode entry's hold on the splash, and dismisses it unless the session is in Game Mode. UI thread.</summary>
    internal void SettleEntrySplash()
    {
        _holdingEntrySplash = false;
        _gameModeEntryActive = false;
        if (!state.InGameMode)
        {
            _splash?.Dismiss("desktop entry settled");
        }
    }

    /// <summary>Forgets the splash, once shutdown dismissed it. UI thread.</summary>
    internal void ReleaseSplash()
    {
        _splash = null;
    }

    /// <summary>
    ///     The splash the entry transaction writes its status into, created on demand so an
    ///     entry that starts from the desktop still gets a cover.
    /// </summary>
    /// <returns>The entry splash.</returns>
    internal BootSplash EnsureEntrySplash()
    {
        if (_splash is not null && _holdingEntrySplash)
        {
            return _splash;
        }

        _splash?.Dismiss("Game Mode entry starting");
        _holdingEntrySplash = true;
        // Unarmed: Big Picture has not been asked for yet, and the wait ahead has no deadline.
        BootSplash splash = new(state.Config, () =>
        {
            if (_gameModeEntryActive)
            {
                modes()?.GameModeEntry.Cancel();
                return;
            }

            SwitchToDesktopFromSplash();
        });
        _splash = splash;
        splash.Show();
        return splash;
    }

    /// <summary>Switches the entry splash's button between cancelling the entry and giving up on Game Mode. UI thread.</summary>
    /// <param name="cancellable">True before the entry's boundary, false after it.</param>
    internal void SetEntryCancellable(bool cancellable)
    {
        _gameModeEntryActive = cancellable;
        EnsureEntrySplash().SetActionLabel(
            cancellable ? "Cancel" : "Switch to desktop");
    }

    /// <summary>Starts the startup-app relaunch watcher once; a failure leaves the session without it.</summary>
    internal void WatchStartupApps()
    {
        ShellSession.TryStart("startup app watcher", () => _startupWatcher ??= new StartupAppWatcher(state.Config.StartupApps)
        {
            IsLaunchSuppressed = path => desktopHost()?.IsApplicationLaunchSuppressed(path) == true
        });
    }

    /// <summary>Applies a reloaded startup-app list to the watcher.</summary>
    /// <param name="config">The reloaded configuration.</param>
    internal void ApplyStartupApps(AppConfig config)
    {
        _startupWatcher?.Apply(config.StartupApps);
    }

    /// <summary>
    ///     Forgets the listed integrations' watch state, so a relaunch queued before a takeover never fires
    ///     after even a very short takeover.
    /// </summary>
    internal void ForgetDesktopAppRelaunches()
    {
        Dispatcher.UIThread.Post(() => _startupWatcher?.Forget(DesktopAppLifecycle.MatchesPath));
    }

    /// <summary>Starts the verified desktop start on the boot worker. The caller holds the transition.</summary>
    internal void StartDesktopSteam()
    {
        _bootWork = Task.Run(StartDesktopSteamAsync);
    }

    /// <summary>Starts the boot launch sequence on the boot worker.</summary>
    internal void StartLaunchSequence()
    {
        _bootWork = Task.Run(async () => { await RunLaunchSequenceAsync(); });
    }

    /// <summary>
    ///     Verifies or recovers Explorer before supplying windowed Steam. Owns the startup transition
    ///     gate until both have settled, so a concurrent Game Mode request cannot leave mixed modes.
    /// </summary>
    private async Task StartDesktopSteamAsync()
    {
        var desktopReady = false;
        try
        {
            await WaitForInputDesktopAsync(state.ShutdownToken).ConfigureAwait(false);
            // An input desktop is not proof that Winlogon's shell exists. Reuse the normal
            // identity-checked restore, which adopts a live shell and recovers a missing one.
            var desktop = await desktopHost()!.RestoreDesktopAsync(
                    TimeSpan.FromSeconds(20), state.ShutdownToken)
                .ConfigureAwait(false);
            desktopReady = desktop.Outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded;
            if (!desktopReady)
            {
                Log.Warn("Desktop startup could not verify Explorer; Steam startup remains paused.");
                modes()!.ReportWarning(SessionModes.ExplorerDesktopPendingWarning);
                return;
            }

            state.ShutdownToken.ThrowIfCancellationRequested();
            // Before the start, so a Steam autostart that reappeared cannot win the race.
            SteamAutostartService.ReapplyAtStart(state.Store);
            modes()!.SteamLifecycle.EnsureDesktop();
        }
        catch (OperationCanceledException) when (state.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Desktop session startup failed", ex);
            if (!desktopReady)
            {
                modes()!.ReportWarning(SessionModes.ExplorerDesktopPendingWarning);
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                modes()!.EndTransition();
                if (!state.ShutdownRequested)
                {
                    monitor()!.Paused = !desktopReady;
                    if (desktopReady)
                    {
                        desktopActions.Queue(true);
                    }
                }
            });
        }
    }

    /// <summary>
    ///     Input-desktop barrier (era-proven): WTS_SESSION_LOGON fires while the Welcome screen still
    ///     owns the input desktop, and proceeding then starts Steam audibly behind LogonUI.
    ///     WTS_SESSION_DESKTOP_READY never arrives on this hardware; polling for winsta0\Default is
    ///     the working signal. Proceeds anyway after <see cref="InputDesktopWait" />.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private static async Task WaitForInputDesktopAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var session = ProcessLookup.CurrentSessionId;

        var lockReported = false;
        while (true)
        {
            // Windows 11 can sign the user in ahead of time after a restart and show the lock screen,
            // which runs on winsta0\Default too. Nothing starts until the user has actually unlocked:
            // that wait has no deadline, since the session may stay locked for as long as they like.
            if (NativeShellProcess.IsSessionLocked(session))
            {
                if (!lockReported)
                {
                    lockReported = true;
                    Log.Info("Session is locked after a prestarted sign-in; waiting for the user to unlock it.");
                }

                // The desktop's own deadline counts from the unlock.
                watch.Restart();
            }
            else if (InputDesktop.IsDefaultInputDesktop())
            {
                break;
            }
            else if (watch.Elapsed >= InputDesktopWait)
            {
                Log.Warn("Input desktop never became winsta0\\Default within "
                         + $"{InputDesktopWait.TotalSeconds:0} s; proceeding anyway.");
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        if (watch.ElapsedMilliseconds > 250)
        {
            Log.Info($"Interactive desktop ready after {watch.ElapsedMilliseconds} ms.");
        }
    }

    /// <summary>
    ///     Runs the startup-app/Steam launch sequence, containing cancellation
    ///     and failure so a boot worker never faults.
    /// </summary>
    private async Task RunLaunchSequenceAsync()
    {
        try
        {
            await LaunchAppsAsync(state.ShutdownToken);
        }
        catch (OperationCanceledException) when (state.ShutdownToken.IsCancellationRequested)
        {
            Log.Info("Shell launch sequence cancelled for application shutdown.");
        }
        catch (Exception ex)
        {
            Log.Error("Shell session launch sequence failed", ex);
        }
        finally
        {
            // A sequence that ended before asking Steam must not leave the cover up with no deadline.
            Dispatcher.UIThread.Post(ArmBootSplash);
        }
    }

    /// <summary>
    ///     First half of the service-boot takeover: cover the booting desktop with the splash
    ///     FIRST (before any posture change — the cover is the point of the early launch). The
    ///     session then watches startup apps and config, and <see cref="RunTakeover" /> lets explorer
    ///     finish its logon prep once, cleanly shuts it down and runs the normal game-mode boot. The
    ///     one-per-session explorer init is what keeps touch features (touch keyboard) alive in game mode.
    /// </summary>
    internal void CoverForTakeover()
    {
        Log.Info("Boot cover: waiting for explorer logon prep.");
        _tookOverFromExplorer = true;
        // Session start normally showed the splash already, so it covered the booting desktop
        // while plugins, device integration and the overlay were still being built.
        ShowBootSplashIfEnabled();
        _bootTakeover ??= new BootTakeoverCancellation();
        if (_splash is null)
        {
            Log.Info("Boot splash disabled — takeover runs uncovered.");
        }
        else
        {
            overlay()!.OverlayShown += () =>
            {
                if (!_holdingEntrySplash)
                {
                    _splash?.Dismiss("quick access opened");
                }
            };
        }
    }

    /// <summary>Holds the transition gate and runs the takeover on the boot worker, then the launch sequence.</summary>
    internal void RunTakeover()
    {
        var takeover = _bootTakeover ??= new BootTakeoverCancellation();

        // Mode switches must not race the takeover (the overlay is live behind the
        // splash and its Desktop button would start a second explorer transition).
        modes()!.BeginTransition();

        _bootWork = Task.Run(async () =>
        {
            var result = BootTakeoverResult.DesktopRestoreRequired;
            try
            {
                result = await RunBootTakeoverAsync(takeover.Token);
            }
            catch (OperationCanceledException) when (takeover.DesktopRequested)
            {
                Log.Info("Boot takeover cancelled by the splash desktop recovery.");
            }
            catch (OperationCanceledException) when (takeover.ShutdownRequested
                                                     || state.ShutdownToken.IsCancellationRequested)
            {
                Log.Info("Boot takeover cancelled for application shutdown.");
            }
            catch (Exception ex)
            {
                Log.Error("Boot takeover failed", ex);
            }
            finally
            {
                // The gate guards the TAKEOVER only, not the launch sequence:
                // released here, the splash's Switch-to-desktop can run and
                // LaunchAppsAsync's monitor-paused guard skips Big Picture.
                modes()!.EndTransition();
                takeover.Complete();
            }

            var desktopRequested = takeover.DesktopRequested;
            if (takeover.ShutdownRequested || state.ShutdownRequested)
            {
                if (ReferenceEquals(_bootTakeover, takeover))
                {
                    _bootTakeover = null;
                }

                takeover.Dispose();
                return;
            }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_bootTakeover, takeover))
                    {
                        _bootTakeover = null;
                    }

                    if (state.ShutdownRequested)
                    {
                        return;
                    }

                    if (desktopRequested)
                    {
                        BeginDesktopModeFromSplash();
                        return;
                    }

                    // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                    switch (result)
                    {
                        case BootTakeoverResult.DesktopPreserved:
                            ResumePreservedDesktopAfterBootFailure();
                            break;
                        case BootTakeoverResult.DesktopRestoreRequired:
                            BeginDesktopModeAfterBootFailure();
                            break;
                    }
                });
            }
            finally
            {
                if (ReferenceEquals(_bootTakeover, takeover))
                {
                    _bootTakeover = null;
                }

                takeover.Dispose();
            }

            if (result is BootTakeoverResult.EnteredGameMode
                && !desktopRequested
                && !state.ShutdownRequested)
            {
                await RunLaunchSequenceAsync();
            }
        });
    }

    /// <summary>
    ///     Runs the takeover phase only (input-desktop barrier, explorer
    ///     readiness, orderly exit, posture, tray host). Returns false when it failed
    ///     open with explorer preserved — the caller then skips the launch sequence.
    /// </summary>
    /// <param name="cancellationToken">
    ///     Cancelled by the splash's desktop recovery.
    ///     Before the orderly exit it preserves Explorer; after that irreversible
    ///     request began, it skips game-mode setup so the caller can restart Explorer.
    /// </param>
    private async Task<BootTakeoverResult> RunBootTakeoverAsync(CancellationToken cancellationToken)
    {
        await WaitForInputDesktopAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var settleDuration = TimeSpan.FromMilliseconds(Math.Max(0, state.Config.ExplorerLogonSettleMs));
        var watch = Stopwatch.StartNew();
        Stopwatch? settle = null;
        long shellSeenMs = -1, taskbarSeenMs = -1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellWindow = NativeMethods.GetShellWindow() != 0;
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;
            var bigPicture = Steam.IsBigPictureVisible;
            if (shellWindow && shellSeenMs < 0)
            {
                shellSeenMs = watch.ElapsedMilliseconds;
            }

            if (taskbar && taskbarSeenMs < 0)
            {
                taskbarSeenMs = watch.ElapsedMilliseconds;
            }

            // Explorer's topmost taskbar and its logon windows appear after the cover and land above
            // it; put the cover back on top on every poll until Explorer is asked to leave.
            if (shellWindow || taskbar)
            {
                Dispatcher.UIThread.Post(() => _splash?.ReassertCover());
            }

            // The invariant-7 acceleration exists solely so an OPAQUE cover never
            // sits over a live BP window. With the splash disabled there is no
            // cover, so report no BP and let explorer finish its logon prep — that
            // one-per-session init is what keeps touch features alive in game mode.
            var coveredBigPicture = bigPicture && _splash?.IsCovering == true;
            var action = ExplorerReadiness.Decide(shellWindow, taskbar, coveredBigPicture,
                watch.Elapsed, settle?.Elapsed, settleDuration, ExplorerReadiness.MaxWait);
            if (action == ExplorerReadinessAction.BeginSettle)
            {
                settle = Stopwatch.StartNew();
                Log.Info($"Explorer readiness: shell window after {shellSeenMs} ms, " +
                         $"taskbar after {taskbarSeenMs} ms — settling {(int)settleDuration.TotalMilliseconds} ms.");
            }
            else if (action == ExplorerReadinessAction.ProceedAccelerated)
            {
                Log.Info("Big Picture appeared during boot cover — accelerating takeover.");
                // The cover has to fade off the live window now, not when the launch sequence asks.
                Dispatcher.UIThread.Post(ArmBootSplash);
                break;
            }
            else if (action == ExplorerReadinessAction.ProceedTimeout)
            {
                Log.Warn(
                    $"Explorer readiness timeout after {(int)ExplorerReadiness.MaxWait.TotalSeconds} s — proceeding anyway.");
                break;
            }
            else if (action == ExplorerReadinessAction.Proceed)
            {
                break;
            }

            await Task.Delay(250, cancellationToken);
        }

        // Boot and resident entry share the bounded orderly exit and retired-shell cleanup.
        // Every failed exit returns through verified desktop recovery.
        cancellationToken.ThrowIfCancellationRequested();
        var host = desktopHost();
        var preparation = host is null
            ? new ExplorerPreparationResult(false, "host-unavailable")
            : await host.PrepareForExplorerExitAsync(cancellationToken).ConfigureAwait(false);
        if (!preparation.Prepared)
        {
            Log.Warn("Boot takeover refused before Explorer exit because no verified jobless "
                     + $"shell launch owner could be retained ({preparation.Detail}).");
            bool desktopPresent;
            try
            {
                desktopPresent = NativeMethods.GetShellWindow() != 0
                                 || NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;
            }
            catch (Exception ex)
            {
                Log.Error("Checking desktop after refused boot takeover failed", ex);
                desktopPresent = false;
            }

            return desktopPresent
                ? BootTakeoverResult.DesktopPreserved
                : BootTakeoverResult.DesktopRestoreRequired;
        }

        ForgetDesktopAppRelaunches();
        var exited = await desktopHost()!.ExitExplorerAndWaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        // Posting Explorer's orderly-exit command is irreversible. A desktop
        // request that landed during the bounded wait must recover by starting
        // Explorer again, never continue into posture/tray/Steam game mode.
        cancellationToken.ThrowIfCancellationRequested();
        if (!exited)
        {
            Log.Warn("Boot takeover did not complete; restoring and verifying the desktop.");
            return BootTakeoverResult.DesktopRestoreRequired;
        }

        var enteredGameMode = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            // Same order as the direct game-mode boot: posture (scale) with the
            // splash re-covering on the display change, then the tray host —
            // explorer is verifiably gone, so Create() can't race a dying taskbar.
            modes()!.ApplyGameModePosture();
            surfaces.Enter();
            return true;
        });
        if (!enteredGameMode || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return BootTakeoverResult.EnteredGameMode;
    }

    /// <summary>
    ///     Handles the boot splash's recovery/quickswitch action on the UI
    ///     thread. During the service takeover, cancellation owns the eventual desktop
    ///     transition; outside it, the ordinary session transition can start now.
    /// </summary>
    private void SwitchToDesktopFromSplash()
    {
        if (_bootTakeover?.RequestDesktop() == true)
        {
            // Pause immediately so even a worker already leaving the takeover
            // cannot race through LaunchAppsAsync into Big Picture.
            monitor()?.Paused = true;
            Log.Info("Boot splash desktop request accepted — cancelling takeover.");
            return;
        }

        BeginDesktopModeFromSplash();
    }

    /// <summary>
    ///     Starts the normal desktop transition and supplies windowed Steam.
    ///     The caller must own the UI thread and, for a cancelled service takeover,
    ///     release its transition gate first.
    /// </summary>
    private void BeginDesktopModeFromSplash()
    {
        // The boot sequence skips its Big Picture start once the monitor is paused. The desktop
        // transition supplies windowed Steam itself, after Explorer's taskbar owner is verified.
        modes()!.EnterDesktopMode();
    }

    /// <summary>
    ///     Completes a refused boot takeover without starting another Explorer. The original
    ///     taskbar owner is still present, so dismissing the opaque cover is the recovery operation.
    /// </summary>
    private void ResumePreservedDesktopAfterBootFailure()
    {
        _splash?.Dismiss("takeover refused");
        surfaces.SettleOnPreservedDesktop();
        // The session settles on the preserved desktop, which is an ordinary desktop steady
        // state: watch Steam again rather than staying in the transition's paused state.
        monitor()?.Paused = false;
        modes()!.ReportWarning(SessionModes.ExplorerTakeoverRefusedWarning);
        modes()!.SteamLifecycle.EnsureDesktop();
    }

    /// <summary>
    ///     Starts the ordinary verified desktop restoration after boot crossed an uncertain
    ///     Explorer-exit boundary. The transition gate has already been released by the caller.
    /// </summary>
    internal void BeginDesktopModeAfterBootFailure()
    {
        _splash?.Dismiss("takeover recovery");
        modes()!.ReportWarning(SessionModes.ExplorerExitFailedWarning);
        modes()!.EnterDesktopMode();
    }

    /// <summary>
    ///     Name-based liveness check for the double-launch guard. Deliberately
    ///     name-only (not full-path): MainModule of a cross-integrity process throws,
    ///     and a same-named copy running from elsewhere still means the user's tool is
    ///     up. Protocol/non-exe targets always report false.
    /// </summary>
    private static bool IsAppAlreadyRunning(string path)
    {
        try
        {
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                   && ProcessLookup.FindProcessIds(
                       Path.GetFileNameWithoutExtension(path)).Count > 0;
        }
        catch
        {
            // Enumeration hiccups must not block the launch sequence.
            return false;
        }
    }

    private async Task LaunchAppsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Before Steam, for the same reason as on the desktop path: a reappeared autostart entry
        // must not be the one that wins the race to start Steam.
        SteamAutostartService.ReapplyAtStart(state.Store);
        // Steam-backed tools can activate the client themselves, before WSGM's own launch.
        SteamCef.EnsureRemoteDebuggingEnabled(Steam.InstallDirectory, steamUi.MasterEnabled);
        var haveApps = state.Config.StartupApps.Exists(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Path));
        if (haveApps && state.Config.StartupDelayMs > 0)
        {
            Log.Info($"Waiting {state.Config.StartupDelayMs} ms before the first startup app (boot settle).");
            await Task.Delay(state.Config.StartupDelayMs, cancellationToken);
        }

        foreach (var app in state.Config.StartupApps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.Path))
            {
                continue;
            }

            if (desktopHost()?.IsApplicationLaunchSuppressed(app.Path) == true)
            {
                Log.Info($"Desktop integration startup suppressed during Game Mode: {app.Path}");
                continue;
            }

            // Explorer processed Run keys/Startup folder during the takeover's
            // settle window — tools registered in both places must not launch twice.
            if (_tookOverFromExplorer && IsAppAlreadyRunning(app.Path))
            {
                Log.Info($"Startup app already running (explorer autostart) — skipping: {app.Path}");
                continue;
            }

            Log.Info($"Starting startup app: {app.Path} {app.Args}{(app.Elevated ? " (elevated)" : "")}");
            AppLauncher.Start(app.Path, app.Args, app.Elevated);
            await Task.Delay(Math.Max(0, state.Config.StaggerDelayMs), cancellationToken);
        }

        if (state.Config.SteamDelayMs > 0)
        {
            await Task.Delay(state.Config.SteamDelayMs, cancellationToken);
        }

        // The splash's Switch-to-desktop (or the overlay's) may have fired while
        // this sequence was still sleeping — EnterDesktopMode paused the monitor,
        // and starting Big Picture now would slam it over the fresh desktop.
        if (monitor() is { Paused: true })
        {
            Log.Info("Skipping Steam start: desktop mode was requested during boot.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Shared start + warning flow (also behind the overlay's Steam button);
        // boot surfaces failures itself because this runs off the UI thread.
        // (steam://open/bigpicture adopts a Steam that explorer's own autostart
        // already brought up, so no duplicate check is needed for Steam itself.)
        await Dispatcher.UIThread.InvokeAsync(ArmBootSplash);
        var warning = modes()!.SteamLifecycle.StartBigPicture();
        if (warning is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (state.ShutdownRequested)
                {
                    return;
                }

                _splash?.Dismiss("Steam start warning");
                overlay()?.SetWarning(warning);
                overlay()?.ShowOverlay();
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Inject the WSGM library tabs once Steam's UI has loaded, so they appear at
        // boot without the user opening the overlay. This runs on the boot worker, and the
        // cancellation source belongs to the UI thread, where GameModeEntered and SteamStarted
        // replace it; reading it here could start a sync on a source those handlers had just
        // cancelled, and the tabs never appeared.
        Dispatcher.UIThread.Post(steamUi.TabBootSync.Kick);
    }

    /// <summary>Cancels a takeover that is still running, for application shutdown.</summary>
    internal void RequestShutdown()
    {
        _bootTakeover?.RequestShutdown();
    }

    /// <summary>Disposes the startup-app watcher.</summary>
    /// <param name="steps">The shutdown's step runner.</param>
    internal void DisposeStartupWatcher(ShutdownSteps steps)
    {
        steps.DisposeOwner("startup app watcher", ref _startupWatcher);
    }
}
