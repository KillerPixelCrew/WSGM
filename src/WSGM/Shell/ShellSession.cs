using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, arms the
/// overlay (hotkey + edge swipes + home-exit), stays resident for the session.</summary>
public sealed class ShellSession
{
    // Replaced wholesale on every reload (see Reload) so this stays the same
    // instance the overlay, SessionModes and DisplayScale's saved-scale snapshot
    // live on — the volume OSD's UI-scale callback reads it long after boot.
    private AppConfig _config;
    private readonly bool _overlayTestOnly;
    private readonly bool _serviceBoot;
    private bool _tookOverFromExplorer;
    private SteamMonitor? _monitor;
    private SessionModes? _modes;
    private StartupAppWatcher? _startupWatcher;
    private OverlayController? _overlay;
    private TrayHost? _trayHost;
    private VolumeButtonService? _volumeButtons;
    private CardAcfWatcher? _cardAcfWatcher;
    private CardVolumeMonitor? _cardVolumes;
    private NetworkIndicatorService? _networkIndicator;
    private KeepAwakeService? _keepAwake;
    private BootSplash? _splash;
    // Non-null from the moment the service-boot splash becomes interactive until
    // the worker releases SessionModes' transition gate. The splash's desktop
    // recovery cancels through this owner instead of racing that gate.
    private BootTakeoverCancellation? _bootTakeover;
    // Replaced (not just cancelled) on every game-mode entry: a single cancelled
    // source would permanently kill boot syncing after the first desktop trip.
    private CancellationTokenSource _tabBootSyncCancellation = new();
    // True for the direct game-mode boot; the desktop-resume paths clear it, and
    // DesktopModeStarting/GameModeEntered keep it current afterwards.
    private volatile bool _inGameMode = true;
    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;
    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering.
    private readonly System.Threading.SemaphoreSlim _cefMasterGate = new(1, 1);
    // Live Wi-Fi-indicator gate: the applied state, so a reload can tell an
    // on->off transition from a repeat of the same value.
    private bool _wifiIndicatorEnabled;
    // Same for the injected download-queue sort buttons, with its own readiness
    // wait (replaced rather than cancelled, for the same reason as the tab sync).
    private bool _downloadSortEnabled;
    private CancellationTokenSource _downloadSortCancellation = new();
    // Field-rooted for the session lifetime: it owns a native power-setting
    // registration and the "did WSGM mute this?" flag.
    private DisplayOffMuteService? _displayMute;
    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private System.IO.FileSystemWatcher? _configWatcher;
    private System.Threading.Timer? _configDebounce;
    private readonly object _configDebounceGate = new();

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">Whether the logon service launched this process over a
    /// live, still-initializing explorer (--boot) — enables the takeover flow.</param>
    public ShellSession(AppConfig config, bool overlayTestOnly = false, bool serviceBoot = false)
    {
        _config = config;
        _cefMasterEnabled = config.Cef.Enabled;
        _wifiIndicatorEnabled = config.Cef.Enabled && config.Cef.WifiIndicator;
        _downloadSortEnabled = config.Cef.Enabled && config.Cef.DownloadQueueSort;
        SteamCef.SetMasterEnabled(config.Cef.Enabled);
        SteamInputShim.SetEnabled(config.SteamInputManagementEnabled);
        _overlayTestOnly = overlayTestOnly;
        _serviceBoot = serviceBoot;
    }

    /// <summary>Starts the shell's startup applications, home application, and overlay services.</summary>
    public void Start()
    {
        _monitor = new SteamMonitor();
        _modes = new SessionModes(_config, _monitor);
        // Session-lifetime on purpose (survives desktop trips): a Steam download must
        // keep the device awake in both modes, and the manual hold belongs to the user.
        // The automatic side is off in overlay-test mode: its poll drives the live
        // Steam client over CEF (and would write the debug flag into a Steam install
        // that never opted in), which the safe local modes must not do. The manual
        // toggle still works there — it only takes a local power request.
        _keepAwake = KeepAwakeService.StartNew(
            _monitor,
            AutoKeepAwakeEnabled(_config),
            DownloadMonitoringEnabled(_config),
            () => !_inGameMode || SteamUiReadiness.IsReady);
        _keepAwake.DownloadActivityChanged += OnDownloadActivityChanged;
        // --overlay-test shares the Settings preview's exposure: it has no boot takeover
        // and no watchdog behind it, so the mode row must not offer a real transition.
        _overlay = new OverlayController(_config, _monitor, _modes, _keepAwake, previewOnly: _overlayTestOnly);

        // The tray host must never coexist with explorer's taskbar (Z-order war
        // over FindWindow — see TrayHost): gone before explorer starts, back
        // after game mode kills it. Apps re-home their icons on each side's
        // TaskbarCreated broadcast.
        _modes.DesktopModeStarting += () =>
        {
            _inGameMode = false;
            _tabBootSyncCancellation.Cancel();
            // Tabs and the badge are game-mode surfaces; the ACF watcher only exists
            // to keep them fresh, so it stands down with them.
            ApplyCardServices(gameModeActive: false);
            _networkIndicator?.Dispose();
            _networkIndicator = null;
            _downloadSortCancellation.Cancel();
            _ = SteamPageBridge.DisableBadgeAsync();
            _ = SteamLibraryTabs.DisableAsync();
            _ = SteamNetworkIndicator.DisableAsync();
            _ = SteamDownloadSort.DisableAsync();
            _volumeButtons?.SetGameModeActive(false);
            _overlay?.AttachTrayHost(null);
            _trayHost?.Dispose();
            _trayHost = null;
        };
        _modes.GameModeEntered += () =>
        {
            _inGameMode = true;
            _trayHost = TrayHost.Create();
            if (_trayHost is not null)
            {
                _overlay?.AttachTrayHost(_trayHost);
            }
            _volumeButtons?.SetGameModeActive(true);
            ApplyCardServices(gameModeActive: true);
            if (!_overlayTestOnly && _wifiIndicatorEnabled)
            {
                _networkIndicator ??= NetworkIndicatorService.StartNew();
                _networkIndicator.Poke();
            }
            // Returning from desktop mode disabled tabs/badge and cancelled the boot
            // sync; re-inject without requiring an overlay open.
            KickTabBootSync();
            KickDownloadSort();
        };
        // A fresh Steam start while WSGM keeps running (client update, crash restart)
        // wipes the injected tabs and the resident badge with the old CEF session —
        // re-inject once the new UI is up.
        _monitor.SteamStarted += () =>
        {
            if (_inGameMode)
            {
                KickTabBootSync();
                KickDownloadSort();
                // The fresh CEF session also wiped the resident network-indicator
                // script — push again as soon as the poll loop next ticks.
                _networkIndicator?.Poke();
                // A restarted client rebuilds its folder list from libraryfolders.vdf,
                // which can bring back a library for a card that is no longer in the
                // reader — and no volume notification will fire to say so.
                _cardVolumes?.Kick("Steam restarted");
            }
        };

        if (_overlayTestOnly)
        {
            // Paused so a Steam exit can never trigger auto-relaunch/overlay-pop
            // reactions on a dev machine ("no apps started" contract); IsAlive
            // still updates for the HomeAppAlive display.
            _monitor.Paused = true;
            Log.Info("Overlay test mode (no apps started).");
            _overlay.ShowOverlay();
            return;
        }

        _volumeButtons = new VolumeButtonService(
            MessageWindow.Create(),
            () => DisplayScale.GetUiScalePercent(_config) / 100.0);
        _displayMute = new DisplayOffMuteService(MessageWindow.Create());
        _displayMute.ApplyConfig(_config.MuteWhileDisplayOff);
        _displayMute.SetDownloadActive(_keepAwake.DownloadActive);
        if (_config.MuteWhileDisplayOff && !_config.Cef.Enabled)
        {
            // The mute only engages while Steam reports a download, and that comes
            // from the CEF poll. An upgraded config can carry MuteWhileDisplayOff
            // true with Steam integration off, where every log line lives inside the
            // poll that never runs — so say it once here, or a pasted log shows
            // nothing at all for a feature the user can see switched on.
            Log.Warn(
                "Mute screen-off downloads is enabled but Steam integration is off; "
                + "download state is unavailable, so muting will never engage.");
        }

        // Refresh boot.json every session start so a stale Elevate/ExePath heals
        // itself before the next sign-in.
        BootManifestWriter.WriteCurrent(_config);

        // Service boot: the service launches WSGM at WTS_SESSION_LOGON — usually
        // BEFORE Winlogon has even started explorer (device-observed 2026-08-07:
        // gating this on IsRunningInSession made the takeover never run, leaving
        // explorer alive behind Big Picture next to our tray host). The takeover
        // owns every explorer state: its readiness poll waits for explorer to
        // appear AND finish logon prep, then shuts it down cleanly; if explorer
        // never shows within the 60 s cap it proceeds like a plain game-mode boot.
        if (_serviceBoot)
        {
            StartBootTakeover();
            return;
        }

        if (ExplorerControl.IsRunningInSession())
        {
            // A live desktop at --shell start means this is NOT a logon boot: it is
            // the update restart (updates only run in desktop mode) or a manual
            // start next to a desktop. Resume in desktop mode — no splash, no
            // startup apps, no Steam, no game posture/scale — with the overlay armed
            // so the panel is available; EnterGameMode brings everything back.
            Log.Info("Shell started with a live desktop — resuming in desktop mode (overlay armed).");
            // No DesktopModeStarting fires for a session that never entered game
            // mode, so clear the flag here: the game-mode-only CEF injections must
            // not start next to a live explorer (and nothing would retract them).
            _inGameMode = false;
            _monitor.Paused = true;
            _startupWatcher = new StartupAppWatcher(_config.StartupApps);
            WatchConfig();
            return;
        }

        // Boot recomputes the posture value, so game mode re-applies it each start.
        // Posture first: it changes the display scale, and the splash sizes itself
        // to the final screen metrics.
        _modes.ApplyGameModePosture();
        _volumeButtons.SetGameModeActive(true);
        // Initial game mode does not raise GameModeEntered. Start the same card
        // services explicitly or an entire direct-boot session misses every eject
        // and insert (device log, 2026-08-22).
        ApplyCardServices(gameModeActive: true);
        // Game-mode logon boot: host the tray now, before startup apps launch —
        // their Shell_NotifyIcon registrations need a living Shell_TrayWnd or
        // they only get an icon after the TaskbarCreated-driven retry (which
        // message-only tray windows never hear).
        _trayHost = TrayHost.Create();
        if (_trayHost is not null)
        {
            _overlay.AttachTrayHost(_trayHost);
        }
        if (_config.BootSplashEnabled)
        {
            _splash = new BootSplash(_config, SwitchToDesktopFromSplash);
            _overlay.OverlayShown += () => _splash?.Dismiss("quick access opened");
            _splash.Show();
        }
        _startupWatcher = new StartupAppWatcher(_config.StartupApps);
        WatchConfig();

        Task.Run(async () =>
        {
            try
            {
                await LaunchAppsAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Shell session launch sequence failed", ex);
            }
            // Boot has settled (splash gone, apps and Steam up) — drop the
            // startup memory before the shell disappears behind the game.
            await Task.Delay(TimeSpan.FromSeconds(90));
            MemoryTrim.TrimBestEffort("boot settled");
        });
    }

    /// <summary>Service-boot takeover: cover the booting desktop with the splash
    /// FIRST (before any posture change — the cover is the point of the early
    /// launch), let explorer finish its logon prep once, then cleanly shut it down
    /// and run the normal game-mode boot. The one-per-session explorer init is what
    /// keeps touch features (touch keyboard) alive in game mode.</summary>
    private void StartBootTakeover()
    {
        Log.Info("Boot cover: waiting for explorer logon prep.");
        _tookOverFromExplorer = true;
        var takeover = new BootTakeoverCancellation();
        _bootTakeover = takeover;

        if (_config.BootSplashEnabled)
        {
            _splash = new BootSplash(_config, SwitchToDesktopFromSplash);
            _overlay!.OverlayShown += () => _splash?.Dismiss("quick access opened");
            _splash.Show();
        }
        else
        {
            Log.Info("Boot splash disabled — takeover runs uncovered.");
        }

        _startupWatcher = new StartupAppWatcher(_config.StartupApps);
        WatchConfig();

        // Mode switches must not race the takeover (the overlay is live behind the
        // splash and its Desktop button would start a second explorer transition).
        _modes!.BeginTransition();

        Task.Run(async () =>
        {
            var tookOver = false;
            try
            {
                tookOver = await RunBootTakeoverAsync(takeover.Token);
            }
            catch (OperationCanceledException) when (takeover.DesktopRequested)
            {
                Log.Info("Boot takeover cancelled by the splash desktop recovery.");
            }
            catch (Exception ex)
            {
                Log.Error("Boot takeover failed", ex);
            }
            finally
            {
                // The flag guards the TAKEOVER only. Holding it across the launch
                // sequence too (StartupDelay + per-app stagger + SteamDelay) made
                // the splash's Switch-to-desktop hit TryBeginTransition and be
                // dropped with nothing but a Log.Warn; released here, that request
                // runs and LaunchAppsAsync's monitor-paused guard skips Big Picture
                // exactly as its comment already claims.
                _modes!.EndTransition();
                takeover.Complete();
            }

            var desktopRequested = takeover.DesktopRequested;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_bootTakeover, takeover))
                {
                    _bootTakeover = null;
                }
                if (desktopRequested)
                {
                    BeginDesktopModeFromSplash();
                }
            });
            takeover.Dispose();

            if (tookOver && !desktopRequested)
            {
                try
                {
                    await LaunchAppsAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("Shell session launch sequence failed", ex);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(90));
            MemoryTrim.TrimBestEffort("boot settled");
        });
    }

    /// <summary>Runs the takeover phase only (input-desktop barrier, explorer
    /// readiness, orderly exit, posture, tray host). Returns false when it failed
    /// open with explorer preserved — the caller then skips the launch sequence.</summary>
    /// <param name="cancellationToken">Cancelled by the splash's desktop recovery.
    /// Before the orderly exit it preserves Explorer; after that irreversible
    /// request began, it skips game-mode setup so the caller can restart Explorer.</param>
    private async Task<bool> RunBootTakeoverAsync(CancellationToken cancellationToken)
    {
        // Input-desktop barrier (era-proven): WTS_SESSION_LOGON fires while the
        // Welcome screen still owns the input desktop — proceeding then starts
        // Steam audibly behind LogonUI. WTS_SESSION_DESKTOP_READY never arrives
        // on this hardware; polling for winsta0\Default is the working signal.
        var desktopWatch = System.Diagnostics.Stopwatch.StartNew();
        while (!InputDesktop.IsDefaultInputDesktop())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (desktopWatch.Elapsed >= TimeSpan.FromSeconds(60))
            {
                Log.Warn("Input desktop never became winsta0\\Default within 60 s — proceeding anyway.");
                break;
            }
            await Task.Delay(250, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (desktopWatch.ElapsedMilliseconds > 250)
        {
            Log.Info($"Interactive desktop ready after {desktopWatch.ElapsedMilliseconds} ms.");
        }

        var settleDuration = TimeSpan.FromMilliseconds(Math.Max(0, _config.ExplorerLogonSettleMs));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        System.Diagnostics.Stopwatch? settle = null;
        long shellSeenMs = -1, taskbarSeenMs = -1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellWindow = NativeMethods.GetShellWindow() != 0;
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;
            var bigPicture = WindowFinder.FindWindow(Steam.ProcessNames, Steam.BigPictureWindowClass) != 0;
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
                settle = System.Diagnostics.Stopwatch.StartNew();
                Log.Info($"Explorer readiness: shell window after {shellSeenMs} ms, " +
                         $"taskbar after {taskbarSeenMs} ms — settling {(int)settleDuration.TotalMilliseconds} ms.");
            }
            else if (action == ExplorerReadinessAction.ProceedAccelerated)
            {
                Log.Info("Big Picture appeared during boot cover — accelerating takeover (invariant 7).");
                break;
            }
            else if (action == ExplorerReadinessAction.ProceedTimeout)
            {
                Log.Warn($"Explorer readiness timeout after {(int)ExplorerReadiness.MaxWait.TotalSeconds} s — proceeding anyway.");
                break;
            }
            else if (action == ExplorerReadinessAction.Proceed)
            {
                break;
            }
            await Task.Delay(250, cancellationToken);
        }

        // Already off the UI thread — the bounded exit wait never blocks the
        // splash's spinner/fade. Logs its own outcome. The budget covers
        // ExplorerControl's 8 s linger grace (waiting out a slow remnant is
        // cheaper than terminating it — that is what Winlogon respawns) AND the
        // respawn retry, which shares the same deadline.
        cancellationToken.ThrowIfCancellationRequested();
        var exited = ExplorerControl.ExitExplorerAndWait(TimeSpan.FromSeconds(30));
        // Posting Explorer's orderly-exit command is irreversible. A desktop
        // request that landed during the bounded wait must recover by starting
        // Explorer again, never continue into posture/tray/Steam game mode.
        cancellationToken.ThrowIfCancellationRequested();
        if (!exited && ExplorerControl.IsRunningInSession())
        {
            // Fail open (era-proven): never enter a half game mode next to a live
            // explorer. Resume like a desktop session — overlay armed, monitor
            // paused, no Steam start; the user can retry from quick access.
            Log.Warn("Boot takeover failed open — explorer preserved; resuming in desktop mode (overlay armed).");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _splash?.Dismiss("takeover failed open");
                // Same reason as the live-desktop resume: this session never
                // entered game mode, so the injections must stay stood down.
                _inGameMode = false;
                if (_monitor is not null)
                {
                    _monitor.Paused = true;
                }
            });
            return false;
        }

        var enteredGameMode = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            // Same order as the direct game-mode boot: posture (scale) with the
            // splash re-covering on the display change, then the tray host —
            // explorer is verifiably gone, so Create() can't race a dying taskbar.
            _modes!.ApplyGameModePosture();
            _trayHost = TrayHost.Create();
            if (_trayHost is not null)
            {
                _overlay?.AttachTrayHost(_trayHost);
            }
            _volumeButtons?.SetGameModeActive(true);
            // The service takeover is another initial entry, not a SessionModes
            // transition, so GameModeEntered does not initialize card services.
            ApplyCardServices(gameModeActive: true);
            return true;
        });
        if (!enteredGameMode || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return true;
    }

    /// <summary>Handles the boot splash's recovery/quickswitch action on the UI
    /// thread. During the service takeover, cancellation owns the eventual desktop
    /// transition; outside it, the ordinary session transition can start now.</summary>
    private void SwitchToDesktopFromSplash()
    {
        if (_bootTakeover?.RequestDesktop() == true)
        {
            // Pause immediately so even a worker already leaving the takeover
            // cannot race through LaunchAppsAsync into Big Picture.
            if (_monitor is not null)
            {
                _monitor.Paused = true;
            }
            Log.Info("Boot splash desktop request accepted — cancelling takeover.");
            return;
        }
        BeginDesktopModeFromSplash();
    }

    /// <summary>Starts the normal desktop transition and supplies windowed Steam.
    /// The caller must own the UI thread and, for a cancelled service takeover,
    /// release its transition gate first.</summary>
    private void BeginDesktopModeFromSplash()
    {
        _modes!.EnterDesktopMode();
        // The boot sequence skips its Big Picture start once the monitor is paused;
        // give the resulting desktop session a normal windowed Steam instead.
        _modes.StartSteamDesktop();
    }

    /// <summary>Name-based liveness check for the double-launch guard. Deliberately
    /// name-only (not full-path): MainModule of a cross-integrity process throws,
    /// and a same-named copy running from elsewhere still means the user's tool is
    /// up. Protocol/non-exe targets always report false.</summary>
    private static bool IsAppAlreadyRunning(string path)
    {
        try
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.SessionId == session)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Enumeration hiccups must not block the launch sequence.
        }
        return false;
    }

    /// <summary>Cancels any in-flight boot sync and starts a fresh one (waits for
    /// Steam's UI, then injects tabs and pushes the badge map). Safe to call on
    /// every trigger — SyncAllAsync's gate serializes overlapping runs (each queued
    /// caller still runs a full sync; they are not collapsed into one).</summary>
    private void KickTabBootSync()
    {
        _tabBootSyncCancellation.Cancel();
        _tabBootSyncCancellation.Dispose();
        _tabBootSyncCancellation = new CancellationTokenSource();
        _ = new LibraryTabManager().SyncOnBootAsync(_tabBootSyncCancellation.Token);
    }

    /// <summary>(Re)installs the injected download-queue sort buttons once Steam's UI
    /// is up. The readiness poll is what retries — the injection itself is attempted
    /// exactly once per Steam session, so a genuine script failure logs a single
    /// warning instead of refilling the capped log every few seconds.</summary>
    private void KickDownloadSort()
    {
        _downloadSortCancellation.Cancel();
        _downloadSortCancellation.Dispose();
        _downloadSortCancellation = new CancellationTokenSource();
        if (_overlayTestOnly || !_downloadSortEnabled)
        {
            return;
        }
        var token = _downloadSortCancellation.Token;
        _ = Task.Run(async () =>
        {
            var waitingForBigPicture = false;
            try
            {
                for (var attempt = 0; attempt < 30 && !token.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 3000 : 5000, token).ConfigureAwait(false);
                    if (!SteamUiReadiness.IsReady)
                    {
                        if (!waitingForBigPicture)
                        {
                            waitingForBigPicture = true;
                            Log.Info("Download queue sort (boot): waiting for the Big Picture window.");
                        }
                        continue;
                    }
                    if (waitingForBigPicture)
                    {
                        waitingForBigPicture = false;
                        Log.Info("Download queue sort (boot): Big Picture is ready; probing CEF.");
                    }
                    var probe = await SteamCef.EvaluateAsync(
                        "JSON.stringify(!!window.webpackChunksteamui)",
                        TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
                    if (probe.Reachable && probe.Value == "true")
                    {
                        await SteamDownloadSort.EnableAsync(token).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Desktop trip, a master-switch flip, or shutdown — nothing to report.
            }
            catch (Exception ex)
            {
                Log.Warn($"Download queue sort injection failed: {ex.Message}");
            }
        });
    }

    /// <summary>Starts or retracts the injected download-queue sort buttons to match a
    /// reloaded configuration, so the toggle applies without a re-logon.</summary>
    /// <param name="enabled">Whether the sort buttons should be injected.</param>
    private void ApplyDownloadSort(bool enabled)
    {
        if (_overlayTestOnly || enabled == _downloadSortEnabled)
        {
            _downloadSortEnabled = enabled;
            return;
        }
        _downloadSortEnabled = enabled;
        if (!enabled)
        {
            _downloadSortCancellation.Cancel();
            // When the master switch is going down too, its own retraction removes
            // the buttons — calling it here as well would race that.
            if (_cefMasterEnabled)
            {
                _ = SteamDownloadSort.DisableAsync();
            }
            Log.Info("Download queue sorting turned off.");
            return;
        }
        KickDownloadSort();
    }

    /// <summary>Mirrors the master CEF switch, retracting anything WSGM already
    /// injected on the way down. Ordering is load-bearing: the switch fails every
    /// evaluation closed, including WSGM's own retractions, so flipping it first
    /// would strand the injected tabs, badges and Wi-Fi AP in Steam until the client
    /// restarted — with the desktop-trip cleanup dead for the same reason. Both
    /// directions run through <c>_cefMasterGate</c> and re-read the field (the
    /// wanted state) once they own it, so a flip landing inside a retraction's
    /// three round-trips cannot leave the choke point closed while the field —
    /// and the equality guard that would have repaired it — say enabled.</summary>
    /// <param name="enabled">The reloaded <c>Cef.Enabled</c> value.</param>
    /// <summary>Applies a Steam Input Management change that arrived through a
    /// config reload.</summary>
    /// <remarks>
    /// The park/restore rename touches Steam's directory, so it runs off the UI
    /// thread. Reconciles are idempotent and serialized inside
    /// <see cref="SteamInputShim"/>, which is what lets the Settings save path and
    /// this watcher both fire without coordinating.
    /// </remarks>
    private static void ApplySteamInputManagement(bool enabled)
    {
        if (SteamInputShim.Enabled == enabled)
        {
            return;
        }
        SteamInputShim.SetEnabled(enabled);
        _ = System.Threading.Tasks.Task.Run(() => SteamInputShim.Reconcile("settings-change"));
    }

    private void ApplyCefMasterSwitch(bool enabled)
    {
        if (_cefMasterEnabled == enabled)
        {
            return;
        }
        _cefMasterEnabled = enabled;
        if (enabled)
        {
            _ = Task.Run(async () =>
            {
                await _cefMasterGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_cefMasterEnabled)
                    {
                        // Turned off again before this apply owned the gate — that
                        // apply's retraction owns the choke point now.
                        return;
                    }
                    SteamCef.SetMasterEnabled(true);
                }
                finally
                {
                    _cefMasterGate.Release();
                }
                // Field-mutating and fire-and-forget from the UI thread, like every
                // other caller.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ApplyCardServices(_inGameMode);
                    KickTabBootSync();
                    KickDownloadSort();
                });
            });
            return;
        }
        // The volume monitor owns autonomous CEF traffic. Stop it as soon as the
        // master gate closes; the ACF watcher remains because it is Steam-file only.
        ApplyCardServices(_inGameMode);
        // A boot sync still in its retry loop would otherwise re-inject the tabs
        // between the awaited DisableAsync and the choke point closing behind it,
        // stranding them until Steam restarts (the desktop trip cancels for the
        // same reason).
        _tabBootSyncCancellation.Cancel();
        _downloadSortCancellation.Cancel();
        _ = Task.Run(async () =>
        {
            await _cefMasterGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await SteamPageBridge.DisableBadgeAsync().ConfigureAwait(false);
                await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
                await SteamNetworkIndicator.DisableAsync().ConfigureAwait(false);
                await SteamDownloadSort.DisableAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Retracting injected Steam UI failed: {ex.Message}");
            }
            finally
            {
                // Only close the choke point while OFF is still the wanted state:
                // a re-enable that landed during these three round-trips already
                // reopened it, and the equality guard above means no later reload
                // would ever repair an overwrite here.
                if (!_cefMasterEnabled)
                {
                    SteamCef.SetMasterEnabled(false);
                    Log.Info("Steam CEF integration disabled — injected UI retracted.");
                }
                else
                {
                    Log.Info("Steam CEF integration was re-enabled during the retraction — " +
                             "leaving the choke point to the enable apply.");
                }
                _cefMasterGate.Release();
            }
        });
    }

    /// <summary>Whether the automatic download wake lock may poll Steam: its CEF
    /// query is autonomous Steam traffic, so it stays off in overlay-test mode
    /// alongside the other injections that mode excludes.</summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool AutoKeepAwakeEnabled(AppConfig config)
        => !_overlayTestOnly && config.Cef.Enabled && config.Cef.DownloadKeepAwake;

    /// <summary>Whether the shared Steam download poll has at least one consumer.
    /// The mute feature reuses the same answer even when its automatic wake lock is
    /// disabled; overlay-test still excludes all autonomous Steam traffic.</summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool DownloadMonitoringEnabled(AppConfig config)
        => !_overlayTestOnly
            && config.Cef.Enabled
            && (config.Cef.DownloadKeepAwake || config.MuteWhileDisplayOff);

    /// <summary>Marshals the shared poller's download transition onto the UI thread,
    /// where the display mute service and its timers are owned.</summary>
    /// <param name="active">Whether Steam reports an active download.</param>
    private void OnDownloadActivityChanged(bool active)
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _displayMute?.SetDownloadActive(active));

    /// <summary>Starts or stops the game-mode card services from one shared policy.</summary>
    /// <remarks>
    /// Initial direct boot and a later desktop-to-game transition are separate entry
    /// paths: only the latter raises <c>GameModeEntered</c>. Keeping their activation
    /// here prevents one path from silently losing volume notifications again.
    /// </remarks>
    /// <param name="gameModeActive">Whether the destination/current mode is game mode.</param>
    private void ApplyCardServices(bool gameModeActive)
    {
        var state = GameModeCardServicePolicy.Decide(
            gameModeActive, _overlayTestOnly, _cefMasterEnabled);

        if (state.WatchAppManifests)
        {
            _cardAcfWatcher ??= CardAcfWatcher.StartNew();
        }
        else
        {
            _cardAcfWatcher?.Dispose();
            _cardAcfWatcher = null;
        }

        if (state.ReconcileSteamLibraries)
        {
            // Card swaps are reconciled against Steam's install-folder list on the
            // volume notification itself. The callback refreshes both consumers of
            // the changed library membership after Steam accepts the reconcile.
            _cardVolumes ??= CardVolumeMonitor.StartNew(
                MessageWindow.Create(),
                () => _cefMasterEnabled,
                () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(KickTabBootSync);
                    return Task.CompletedTask;
                });
        }
        else
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        }
    }

    /// <summary>Starts or stops the Big Picture Wi-Fi indicator to match a reloaded
    /// configuration. Without this the feed keeps running (and keeps being recreated
    /// on every game-mode entry) after the user turns the toggle off, because the
    /// start gates read the boot-time configuration.</summary>
    /// <param name="enabled">Whether the indicator should be feeding Steam.</param>
    private void ApplyNetworkIndicator(bool enabled)
    {
        if (_overlayTestOnly || enabled == _wifiIndicatorEnabled)
        {
            _wifiIndicatorEnabled = enabled;
            return;
        }
        _wifiIndicatorEnabled = enabled;
        if (!enabled)
        {
            _networkIndicator?.Dispose();
            _networkIndicator = null;
            // When the master switch is going down too, its own retraction removes
            // the synthetic access point — calling it here as well would race that.
            if (_cefMasterEnabled)
            {
                _ = SteamNetworkIndicator.DisableAsync();
            }
            Log.Info("Big Picture Wi-Fi indicator turned off.");
            return;
        }
        if (_inGameMode)
        {
            _networkIndicator ??= NetworkIndicatorService.StartNew();
            _networkIndicator.Poke();
            Log.Info("Big Picture Wi-Fi indicator turned on.");
        }
    }

    private void WatchConfig()
    {
        try
        {
            _configWatcher = new System.IO.FileSystemWatcher(Log.Directory, "config.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName,
            };
            // The LOAD stays off the UI thread: it takes the cross-process config
            // mutex (2 s timeout) that a settings save holds across the write, the
            // splash-asset promotion and the boot manifest — 500 ms of debounce does
            // not reliably outlast that. Only the cheap, UI-affine apply is posted.
            void Reload(object? state)
                => _ = Task.Run(() =>
                {
                    var config = ConfigStore.Load();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // One instance for every reader: the volume OSD's UI-scale
                        // callback and DisplayScale's saved-scale snapshot must not
                        // drift onto different AppConfig objects.
                        _config = config;
                        ApplyCefMasterSwitch(config.Cef.Enabled);
                        ApplySteamInputManagement(config.SteamInputManagementEnabled);
                        ApplyNetworkIndicator(config.Cef.Enabled && config.Cef.WifiIndicator);
                        ApplyDownloadSort(config.Cef.Enabled && config.Cef.DownloadQueueSort);
                        _displayMute?.ApplyConfig(config.MuteWhileDisplayOff);
                        _overlay?.ApplyConfig(config);
                        _startupWatcher?.Apply(config.StartupApps);
                        _keepAwake?.ApplyConfig(
                            AutoKeepAwakeEnabled(config),
                            DownloadMonitoringEnabled(config));
                    });
                });
            // Changed/Renamed fire on threadpool threads — the swap must be locked
            // so two near-simultaneous events can't both dispose the same timer and
            // orphan one that still fires.
            void Debounce()
            {
                lock (_configDebounceGate)
                {
                    _configDebounce?.Dispose();
                    _configDebounce = new System.Threading.Timer(Reload, null, 500, System.Threading.Timeout.Infinite);
                }
            }
            _configWatcher.Changed += (_, _) => Debounce();
            _configWatcher.Renamed += (_, _) => Debounce();
            // Internal-buffer overflow or a directory-level error kills the change
            // events silently — settings would stop applying for the rest of the
            // session with nothing in the log to diagnose it from. Log, reload once
            // (the missed write is already on disk), and re-arm by restarting the
            // watch. Deliberately NOT a recreate: this handler would resubscribe
            // itself and a persistently failing directory would spin.
            _configWatcher.Error += (sender, e) =>
            {
                Log.Warn($"Config watcher error: {e.GetException().Message} — re-arming.");
                Debounce();
                try
                {
                    if (sender is System.IO.FileSystemWatcher watcher)
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.EnableRaisingEvents = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Config watcher could not be re-armed: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher not available: {ex.Message}");
        }
    }

    private async Task LaunchAppsAsync()
    {
        // Snapshot the token up front: KickTabBootSync (UI thread) disposes and
        // replaces the source, and reading .Token off the replaced instance later
        // throws ObjectDisposedException — which would abort the rest of this
        // sequence, including the Wi-Fi-indicator start below.
        var tabSyncToken = _tabBootSyncCancellation.Token;
        var haveApps = _config.StartupApps.Exists(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Path));
        if (haveApps && _config.StartupDelayMs > 0)
        {
            Log.Info($"Waiting {_config.StartupDelayMs} ms before the first startup app (boot settle).");
            await Task.Delay(_config.StartupDelayMs);
        }

        foreach (var app in _config.StartupApps)
        {
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.Path))
            {
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
            await Task.Delay(Math.Max(0, _config.StaggerDelayMs));
        }

        if (_config.SteamDelayMs > 0)
        {
            await Task.Delay(_config.SteamDelayMs);
        }

        // The splash's Switch-to-desktop (or the overlay's) may have fired while
        // this sequence was still sleeping — EnterDesktopMode paused the monitor,
        // and starting Big Picture now would slam it over the fresh desktop.
        if (_monitor is { Paused: true })
        {
            Log.Info("Skipping Steam start: desktop mode was requested during boot.");
            return;
        }


        // Shared start + warning flow (also behind the overlay's Steam button);
        // boot surfaces failures itself because this runs off the UI thread.
        // (steam://open/bigpicture adopts a Steam that explorer's own autostart
        // already brought up, so no duplicate check is needed for Steam itself.)
        var warning = _modes!.StartBigPicture();
        if (warning is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _splash?.Dismiss("Steam start warning");
                _overlay?.SetWarning(warning);
                _overlay?.ShowOverlay();
            });
        }

        // Inject the WSGM library tabs once Steam's UI has loaded, so they appear at
        // boot without the user opening the overlay. Fire-and-forget; self-limiting.
        _ = new LibraryTabManager().SyncOnBootAsync(tabSyncToken);

        // The initial boot enters game mode without a GameModeEntered event — start
        // the Wi-Fi indicator feed here; its own retries wait out Steam's UI.
        if (!_overlayTestOnly && _wifiIndicatorEnabled)
        {
            _networkIndicator ??= NetworkIndicatorService.StartNew();
        }
        // Same reason: inject the download-queue sort buttons at boot so they are
        // already there the first time the user opens the Downloads page.
        KickDownloadSort();
    }
}
