using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Interop;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>How long a start waits for the interactive desktop before it proceeds anyway.</summary>
    private static readonly TimeSpan InputDesktopWait = TimeSpan.FromSeconds(60);

    private readonly DesktopActionAdmission _desktopActionAdmission = new();
    private readonly SemaphoreSlim _displayActionGate = new(1, 1);

    // Non-null from the moment the service-boot splash becomes interactive until
    // the worker releases SessionModes' transition gate. The splash's desktop
    // recovery cancels through this owner instead of racing that gate.
    private BootTakeoverCancellation? _bootTakeover;

    private Task? _bootWork;
    private bool _gameModeEntryActive;
    private bool _holdingEntrySplash;
    private DisplayLayout? _pendingReturnLayout;
    private bool _tookOverFromExplorer;

    private async Task NotifyPluginModeAsync(PluginSessionMode mode)
    {
        try
        {
            await _pluginHost.SetModeAsync(mode, Deadline.After(TimeSpan.FromSeconds(5)), _shutdownCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Plugin mode transition did not complete", ex);
        }
    }

    /// <summary>
    ///     Creates the game-mode-only surfaces in one shared order: tray host
    ///     first (startup apps' Shell_NotifyIcon registrations need a living
    ///     Shell_TrayWnd, or they only get an icon after the TaskbarCreated-driven retry,
    ///     which message-only tray windows never hear), volume buttons, then card
    ///     services. Direct boot and the service takeover are separate entry paths from
    ///     the desktop-to-game transition, so each initial entry calls this explicitly.
    /// </summary>
    private void EnterGameModeSurfaces()
    {
        _ = NotifyPluginModeAsync(PluginSessionMode.Game);
        _trayHost ??= TrayHost.Create()
                      ?? throw new InvalidOperationException("The Game Mode tray could not be created.");
        _overlay?.AttachTrayHost(_trayHost);
        _volumeButtons?.SetGameModeActive(true);
        ApplyCardServices(true);
    }

    /// <summary>
    ///     Covers the screen with the boot splash when configured; the overlay
    ///     opening dismisses it.
    /// </summary>
    private void ShowBootSplashIfEnabled()
    {
        if (!_config.BootSplashEnabled)
        {
            return;
        }

        _splash = new BootSplash(_config, SwitchToDesktopFromSplash);
        _overlay!.OverlayShown += () => _splash?.Dismiss("quick access opened");
        _splash.Show();
    }

    /// <summary>
    ///     Supplies the windowed Steam client a desktop session is expected to have. Waits for
    ///     the input desktop first, for the same reason the boot takeover does: a sign-in start can run
    ///     while LogonUI still owns the screen, and Steam started then is audible behind it.
    /// </summary>
    private async Task StartDesktopSteamAsync()
    {
        try
        {
            await WaitForInputDesktopAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            // Before the start, so a Steam autostart that reappeared cannot win the race.
            SteamAutostartService.ReapplyAtStart(_store);
            _modes!.EnsureSteamDesktop();
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Desktop Steam start failed", ex);
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
        while (!InputDesktop.IsDefaultInputDesktop())
        {
            if (watch.Elapsed >= InputDesktopWait)
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

    private void WatchStartupAppsAndConfig()
    {
        _startupWatcher = new StartupAppWatcher(_config.StartupApps)
        {
            IsLaunchSuppressed = path => _desktopHost?.IsApplicationLaunchSuppressed(path) == true,
            LaunchGeneration = path => _desktopHost?.ApplicationLaunchGeneration(path) ?? 0
        };
        WatchConfig();
    }

    /// <summary>
    ///     Runs the startup-app/Steam launch sequence, containing cancellation
    ///     and failure so a boot worker never faults.
    /// </summary>
    private async Task RunLaunchSequenceAsync()
    {
        try
        {
            await LaunchAppsAsync(_shutdownCancellation.Token);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            Log.Info("Shell launch sequence cancelled for application shutdown.");
        }
        catch (Exception ex)
        {
            Log.Error("Shell session launch sequence failed", ex);
        }
    }

    /// <summary>
    ///     Service-boot takeover: cover the booting desktop with the splash
    ///     FIRST (before any posture change — the cover is the point of the early
    ///     launch), let explorer finish its logon prep once, then cleanly shut it down
    ///     and run the normal game-mode boot. The one-per-session explorer init is what
    ///     keeps touch features (touch keyboard) alive in game mode.
    /// </summary>
    private void StartBootTakeover()
    {
        Log.Info("Boot cover: waiting for explorer logon prep.");
        _tookOverFromExplorer = true;
        var takeover = new BootTakeoverCancellation();
        _bootTakeover = takeover;

        ShowBootSplashIfEnabled();
        if (_splash is null)
        {
            Log.Info("Boot splash disabled — takeover runs uncovered.");
        }

        WatchStartupAppsAndConfig();

        // Mode switches must not race the takeover (the overlay is live behind the
        // splash and its Desktop button would start a second explorer transition).
        _modes!.BeginTransition();

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
                                                     || _shutdownCancellation.IsCancellationRequested)
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
                _modes!.EndTransition();
                takeover.Complete();
            }

            var desktopRequested = takeover.DesktopRequested;
            if (takeover.ShutdownRequested || _shutdownRequested)
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

                    if (_shutdownRequested)
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
                && !_shutdownRequested)
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

        var settleDuration = TimeSpan.FromMilliseconds(Math.Max(0, _config.ExplorerLogonSettleMs));
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

            // The invariant-7 acceleration exists solely so an OPAQUE cover never
            // sits over a live BP window. With the splash disabled there is no
            // cover, so report no BP and let explorer finish its logon prep — that
            // one-per-session init is what keeps touch features alive in game mode.
            var coveredBigPicture = bigPicture && _splash is not null;
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
        var preparation = _desktopHost is null
            ? new ExplorerPreparationResult(false, "host-unavailable")
            : await _desktopHost.PrepareForExplorerExitAsync(cancellationToken).ConfigureAwait(false);
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

        var exited = await _desktopHost!.ExitExplorerAndWaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
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
            _modes!.ApplyGameModePosture();
            EnterGameModeSurfaces();
            return true;
        });
        if (!enteredGameMode || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return BootTakeoverResult.EnteredGameMode;
    }

    /// <summary>
    ///     Answers Switch to Desktop in Steam's power menu with the transition the overlay's Return to
    ///     Desktop starts. Refused outside Game Mode and while another transition runs.
    /// </summary>
    private async Task<SteamUiCommandResult> SwitchToDesktopFromSteamAsync(CancellationToken cancellationToken)
    {
        var refusal = "WSGM could not start the switch.";
        var started = await RunUiActionAsync(() =>
        {
            if (_modes is null || !_inGameMode)
            {
                refusal = "WSGM is not in Game Mode.";
                return false;
            }

            if (_modes.TransitionInProgress)
            {
                refusal = "A mode switch is already in progress.";
                return false;
            }

            Log.Info("Switch to Desktop selected in Steam's power menu.");
            _modes.EnterDesktopMode();
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return started ? SteamUiCommandResult.Applied : new SteamUiCommandResult(false, refusal);
    }

    /// <summary>Records whether the session is in Game Mode, for everything that follows the mode.</summary>
    /// <param name="inGameMode">Whether the session is in Game Mode.</param>
    private void SetInGameMode(bool inGameMode)
    {
        _inGameMode = inGameMode;
        _steamPowerMenu?.SetGameMode(inGameMode);
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
            _monitor?.Paused = true;
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
        _modes!.EnterDesktopMode();
    }

    /// <summary>
    ///     Completes a refused boot takeover without starting another Explorer. The original
    ///     taskbar owner is still present, so dismissing the opaque cover is the recovery operation.
    /// </summary>
    private void ResumePreservedDesktopAfterBootFailure()
    {
        _splash?.Dismiss("takeover refused");
        SetInGameMode(false);
        _ = NotifyPluginModeAsync(PluginSessionMode.Desktop);
        RequestSteamUiTransportGateCheck();
        // The session settles on the preserved desktop, which is an ordinary desktop steady
        // state: watch Steam again rather than staying in the transition's paused state.
        _monitor?.Paused = false;
        _modes!.ReportWarning(SessionModes.ExplorerTakeoverRefusedWarning);
        _modes.EnsureSteamDesktop();
    }

    /// <summary>
    ///     Starts the ordinary verified desktop restoration after boot crossed an uncertain
    ///     Explorer-exit boundary. The transition gate has already been released by the caller.
    /// </summary>
    private void BeginDesktopModeAfterBootFailure()
    {
        _splash?.Dismiss("takeover recovery");
        _modes!.ReportWarning(SessionModes.ExplorerExitFailedWarning);
        _modes.EnterDesktopMode();
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
                   && WindowFinder.FindProcessIds(
                       Path.GetFileNameWithoutExtension(path)).Count > 0;
        }
        catch
        {
            // Enumeration hiccups must not block the launch sequence.
            return false;
        }
    }

    /// <summary>
    ///     The splash the entry transaction writes its status into, created on demand so an
    ///     entry that starts from the desktop still gets a cover.
    /// </summary>
    private BootSplash EnsureEntrySplash()
    {
        if (_splash is not null && _holdingEntrySplash)
        {
            return _splash;
        }

        _splash?.Dismiss("Game Mode entry starting");
        _holdingEntrySplash = true;
        // Unarmed: Big Picture has not been asked for yet, and the wait ahead has no deadline.
        BootSplash splash = new(_config, () =>
        {
            if (_gameModeEntryActive)
            {
                _modes?.CancelGameModeEntry();
                return;
            }

            SwitchToDesktopFromSplash();
        }, false);
        _splash = splash;
        splash.Show();
        return splash;
    }

    private PluginActionSequence ActionSequence()
    {
        return new PluginActionSequence(new PluginHostActionInvoker(_pluginHost), Log.Info);
    }

    private DisplayArrivalWaiter CreateArrivalWaiter()
    {
        return new DisplayArrivalWaiter(
            new ShellDisplayPresence(),
            new ShellDisplayChangeSignal(_displayChangeWindow),
            Task.Delay);
    }

    /// <summary>Runs the desktop startup or wake action list, coalesced.</summary>
    /// <param name="startup">True for the startup list, false for the wake list.</param>
    /// <remarks>
    ///     The work starts synchronously inside the posted callback, and a fault that escapes it is
    ///     rethrown on the dispatcher, as an async callback would have raised it there.
    /// </remarks>
    private void QueueDesktopActions(bool startup)
    {
        Dispatcher.UIThread.Post(() =>
            _ = RunDesktopActionsAsync(startup).ContinueWith(
                static task => Dispatcher.UIThread.Post(() => task.GetAwaiter().GetResult()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default));
    }

    private async Task RunDesktopActionsAsync(bool startup)
    {
        var configured = startup
            ? _config.GameModeLaunch.DesktopStartupActions
            : _config.GameModeLaunch.DesktopWakeActions;
        if (configured.Count == 0 || _shutdownRequested || _overlayTestOnly || !_desktopActionAdmission.TryBegin(
                _inGameMode, _modes?.TransitionInProgress != false, Environment.TickCount64))
        {
            return;
        }

        var acquired = false;
        try
        {
            await _displayActionGate.WaitAsync(_shutdownCancellation.Token);
            acquired = true;
            var config = await Task.Run(_store.Load, _shutdownCancellation.Token);
            var steps = startup
                ? config.GameModeLaunch.DesktopStartupActions
                : config.GameModeLaunch.DesktopWakeActions;
            if (_shutdownRequested || _inGameMode || _modes?.TransitionInProgress != false
                || steps.Count == 0)
            {
                return;
            }

            await _commonPluginStartup.WaitAsync(_shutdownCancellation.Token);
            Task powerReady;
            lock (_devicePowerGate)
            {
                powerReady = _devicePowerWork;
            }

            await powerReady.WaitAsync(_shutdownCancellation.Token);
            // Re-checked after both waits: a Game Mode entry can have started meanwhile, and a
            // desktop action list must never fire into a session that is leaving the desktop.
            if (_shutdownRequested || _inGameMode || _modes?.TransitionInProgress != false)
            {
                return;
            }

            foreach (var step in await new PluginActionSequence(
                         new PluginHostActionInvoker(_pluginHost)).RunAllAsync(steps, _shutdownCancellation.Token))
            {
                Log.Info($"Desktop {(startup ? "startup" : "wake")} action "
                         + $"{step.Step.Plugin?.PluginId}/{step.Step.Plugin?.InstanceId}:{step.Step.ActionId}: "
                         + $"{step.Outcome}: {step.Detail}");
                if (!step.Succeeded)
                {
                    _modes?.ReportWarning($"Desktop action: {step.Detail}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Desktop lifecycle actions failed", ex);
        }
        finally
        {
            if (acquired)
            {
                _displayActionGate.Release();
            }

            _desktopActionAdmission.End();
        }
    }

    private async Task LaunchAppsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Before Steam, for the same reason as on the desktop path: a reappeared autostart entry
        // must not be the one that wins the race to start Steam.
        SteamAutostartService.ReapplyAtStart(_store);
        var haveApps = _config.StartupApps.Exists(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Path));
        if (haveApps && _config.StartupDelayMs > 0)
        {
            Log.Info($"Waiting {_config.StartupDelayMs} ms before the first startup app (boot settle).");
            await Task.Delay(_config.StartupDelayMs, cancellationToken);
        }

        foreach (var app in _config.StartupApps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.Path))
            {
                continue;
            }

            if (_desktopHost?.IsApplicationLaunchSuppressed(app.Path) == true)
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
            await Task.Delay(Math.Max(0, _config.StaggerDelayMs), cancellationToken);
        }

        if (_config.SteamDelayMs > 0)
        {
            await Task.Delay(_config.SteamDelayMs, cancellationToken);
        }

        // The splash's Switch-to-desktop (or the overlay's) may have fired while
        // this sequence was still sleeping — EnterDesktopMode paused the monitor,
        // and starting Big Picture now would slam it over the fresh desktop.
        if (_monitor is { Paused: true })
        {
            Log.Info("Skipping Steam start: desktop mode was requested during boot.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Shared start + warning flow (also behind the overlay's Steam button);
        // boot surfaces failures itself because this runs off the UI thread.
        // (steam://open/bigpicture adopts a Steam that explorer's own autostart
        // already brought up, so no duplicate check is needed for Steam itself.)
        var warning = _modes!.StartBigPicture();
        if (warning is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_shutdownRequested)
                {
                    return;
                }

                _splash?.Dismiss("Steam start warning");
                _overlay?.SetWarning(warning);
                _overlay?.ShowOverlay();
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Inject the WSGM library tabs once Steam's UI has loaded, so they appear at
        // boot without the user opening the overlay. This runs on the boot worker, and the
        // cancellation source belongs to the UI thread, where GameModeEntered and SteamStarted
        // replace it; reading it here could start a sync on a source those handlers had just
        // cancelled, and the tabs never appeared.
        Dispatcher.UIThread.Post(KickTabBootSync);
    }

    /// <summary>
    ///     The session's half of the Game Mode entry transaction. Everything here needs state
    ///     the session owns — the splash, the plugin host, the config lock and the shutdown token — so
    ///     it is a view onto the session rather than a free-standing service.
    /// </summary>
    private sealed class ShellGameModeEntryServices(ShellSession session) : IGameModeEntryServices
    {
        public GameModeLaunchConfiguration ReadLaunch()
        {
            return _store.Load().GameModeLaunch;
        }

        public void SetStatus(string line)
        {
            Log.Info($"Game Mode entry: {line}.");
            Dispatcher.UIThread.Post(() => session.EnsureEntrySplash().SetStatus(line));
        }

        public async Task ArmSteamDetectionAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                session.EnsureEntrySplash().ArmSteamDetection());
        }

        public void SetCancellable(bool cancellable)
        {
            Dispatcher.UIThread.Post(() =>
            {
                session._gameModeEntryActive = cancellable;
                session.EnsureEntrySplash().SetActionLabel(
                    cancellable ? "Cancel" : "Switch to desktop");
            });
        }

        public Task<DisplayArrangement> ObserveAsync()
        {
            return Task.Run(DisplayLayouts.Observe, session._shutdownCancellation.Token);
        }

        public async Task<DisplayArrangement> WaitForDisplaysAsync(
            IReadOnlyList<DisplayTargetIdentity> targets,
            CancellationToken cancellationToken)
        {
            Log.Info($"Display wait requested: {JsonSerializer.Serialize(targets)}");
            var elapsed = Stopwatch.StartNew();
            var observed =
                await session.CreateArrivalWaiter().WaitAsync(targets, cancellationToken).ConfigureAwait(false);
            Log.Info($"Display wait settled after {elapsed.ElapsedMilliseconds} ms: {observed.Fingerprint}");
            return observed;
        }

        public Task<DisplayLayoutResult> ApplyLayoutAsync(
            DisplayLayout layout, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return DisplayLayoutDiagnostics.Apply(layout, DisplayLayouts.Apply,
                    DisplayLayouts.Observe, Log.Info, Log.Warn);
            }, CancellationToken.None);
        }

        public Task<AudioProfilePreference?> CaptureAudioAsync(CancellationToken cancellationToken)
        {
            return session._audioProfiles?.CaptureAsync(cancellationToken)
                   ?? Task.FromResult<AudioProfilePreference?>(null);
        }

        public Task<AudioProfileApplyResult> ApplyAudioAsync(
            AudioProfilePreference? preference,
            CancellationToken cancellationToken)
        {
            return session._audioProfiles?.ApplyAsync(preference, cancellationToken)
                   ?? Task.FromResult(new AudioProfileApplyResult([]));
        }

        public Task PersistPendingReturnAsync(DisplayLayout? layout, AudioProfilePreference? audio)
        {
            return Task.Run(() =>
            {
                session._pendingReturnLayout = layout;
                _store.Mutate(fresh =>
                {
                    fresh.GameModeLaunchRecovery.PendingReturnLayout = layout;
                    fresh.GameModeLaunchRecovery.PendingReturnAudio = audio;
                    fresh.GameModeLaunchRecovery.EnteredAt =
                        layout is null && audio is null ? null : DateTimeOffset.UtcNow;
                });
            });
        }

        public Task<bool> RestorePendingReturnAsync(CancellationToken cancellationToken)
        {
            return GameModeReturnRecovery.RestorePendingAsync(session._store, cancellationToken, session._audioProfiles);
        }

        public async Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(
            CancellationToken cancellationToken)
        {
            var launch = ReadLaunch();
            await session._commonPluginStartup.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await session.ActionSequence()
                .RunUntilFailureAsync(launch.EnterActions, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync()
        {
            return session.ActionSequence().RunAllAsync(ReadLaunch().LeaveActions, CancellationToken.None);
        }

        public async Task<string?> ApplyReturnLayoutAsync()
        {
            var launch = ReadLaunch();
            var layout = session._pendingReturnLayout
                         ?? _store.Load().GameModeLaunchRecovery.PendingReturnLayout
                         ?? (launch.Return == GameModeReturn.DesktopLayout ? launch.DesktopLayout : null);
            if (layout is null)
            {
                return null;
            }

            var result =
                await ApplyLayoutAsync(layout, CancellationToken.None).ConfigureAwait(false);
            return result.Applied ? null : "Desktop display layout: " + result.Detail;
        }

        public async Task<string?> ApplyReturnAudioAsync()
        {
            var launch = ReadLaunch();
            var audio = launch.DesktopAudio
                        ?? _store.Load().GameModeLaunchRecovery.PendingReturnAudio;
            var result = await ApplyAudioAsync(audio, CancellationToken.None).ConfigureAwait(false);
            return result.Succeeded
                ? null
                : "Desktop audio: " + string.Join(" ", result.Operations
                    .Where(static operation => !operation.Succeeded)
                    .Select(static operation => operation.Name + " " + operation.Detail));
        }
    }
}

/// <summary>Outcome of the service-boot Explorer takeover phase.</summary>
internal enum BootTakeoverResult
{
    /// <summary>Explorer exited safely and game-mode shell resources were created.</summary>
    EnteredGameMode,

    /// <summary>The original desktop stayed intact and only the boot cover must be removed.</summary>
    DesktopPreserved,

    /// <summary>The exit boundary is uncertain and the verified desktop restoration must run.</summary>
    DesktopRestoreRequired
}
