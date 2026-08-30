using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, arms the
/// overlay (hotkey + edge swipes + home-exit), stays resident for the session.</summary>
public sealed class ShellSession : IAsyncDisposable
{
    // Replaced wholesale on every reload (see Reload) so this stays the same
    // instance the overlay, SessionModes and DisplayScale's saved-scale snapshot
    // live on — the volume OSD's UI-scale callback reads it long after boot.
    private AppConfig _config;
    private readonly bool _overlayTestOnly;
    private readonly bool _serviceBoot;
    private readonly bool _suppressDeviceIntegration;
    private bool _tookOverFromExplorer;
    private SteamMonitor? _monitor;
    private SessionModes? _modes;
    private ExplorerDesktopHost? _desktopHost;
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
    private Task? _bootWork;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private volatile bool _shutdownRequested;
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
    private Task? _startupTask;
    private DeviceCoordinator? _deviceCoordinator;
    private IDeviceOverlaySource? _deviceOverlay;
    private PerformanceService? _performance;
    private RefreshRatePairingService? _refreshPairing;

    /// <summary>
    /// The one audio manager for this session, shared by the taskbar's status cluster and Steam's
    /// audio namespace.
    /// </summary>
    /// <remarks>
    /// Session-scoped because the taskbar is not: it comes and goes, and Steam's audio store has to
    /// answer for the whole session. A second manager would enumerate endpoints twice and could
    /// disagree with the taskbar about which device is default.
    /// </remarks>
    private AudioManager? _audio;

    /// <summary>
    /// The one radio manager for this session, shared by the taskbar's status cluster and Steam's
    /// network surface.
    /// </summary>
    /// <remarks>
    /// Session-scoped for the same reason as the audio manager, and idle by default: scanning costs
    /// power and only makes sense while a network list is on screen.
    /// </remarks>
    private RadioManager? _radios;
    private int _pairedFrameLimit = -1;
    private PerformanceOverlayBridge? _performanceOverlay;
    private PersistentSteamUiTransport? _steamUiTransport;
    private RunningApplicationMonitor? _runningApplications;
    private ForegroundWindowWatcher? _foregroundWindows;
    private DeviceProfileApplier? _profileApplier;
    private AutoTdpService? _autoTdp;
    private RunningApplicationCoordinator? _runningApplicationTargets;
    private SteamUiSessionHost? _steamUi;
    private MessageWindow? _messageWindow;
    private readonly object _devicePowerGate = new();
    private Task _devicePowerWork = Task.CompletedTask;
    private bool _deviceSuspended;
    private bool _disposed;

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">Whether the logon service launched this process over a
    /// live, still-initializing explorer (--boot) — enables the takeover flow.</param>
    /// <param name="suppressDeviceIntegration">Whether an installer rollback that could not verify
    /// old DeviceHost exit must restore shell mode without admitting a new hardware cycle.</param>
    public ShellSession(
        AppConfig config,
        bool overlayTestOnly = false,
        bool serviceBoot = false,
        bool suppressDeviceIntegration = false)
    {
        _config = config;
        _cefMasterEnabled = config.Cef.Enabled;
        _wifiIndicatorEnabled = config.Cef.Enabled && config.Cef.WifiIndicator;
        _downloadSortEnabled = config.Cef.Enabled && config.Cef.DownloadQueueSort;
        SteamCef.SetMasterEnabled(config.Cef.Enabled);
        SteamInputShim.SetEnabled(config.SteamInputManagementEnabled);
        _overlayTestOnly = overlayTestOnly;
        _serviceBoot = serviceBoot;
        _suppressDeviceIntegration = suppressDeviceIntegration;
    }

    internal static bool ShouldStartDeviceCoordinator(
        bool overlayTestOnly,
        bool suppressDeviceIntegration) => !overlayTestOnly && !suppressDeviceIntegration;

    /// <summary>Starts device admission off-thread, then creates shell and overlay services on the UI thread.</summary>
    /// <returns>The complete asynchronous session-start operation.</returns>
    public Task StartAsync()
    {
        _startupTask ??= StartUnderDeviceAdmissionAsync();
        return _startupTask;
    }

    internal static Task<DeviceCoordinator?> AdmitDeviceCoordinatorAsync(
        AppConfig config,
        bool overlayTestOnly,
        bool suppressDeviceIntegration,
        CancellationToken cancellationToken,
        Func<AppConfig, CancellationToken, Task<DeviceCoordinator?>>? startAsync = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!ShouldStartDeviceCoordinator(overlayTestOnly, suppressDeviceIntegration))
        {
            return Task.FromResult<DeviceCoordinator?>(null);
        }

        Func<AppConfig, CancellationToken, Task<DeviceCoordinator?>> factory =
            startAsync ?? DeviceCoordinator.TryStartAsync;
        return factory(config, cancellationToken);
    }

    private async Task StartUnderDeviceAdmissionAsync()
    {
        DeviceCoordinator? coordinator = null;
        bool coordinatorAdopted = false;
        try
        {
            coordinator = await AdmitDeviceCoordinatorAsync(
                _config,
                _overlayTestOnly,
                _suppressDeviceIntegration,
                _shutdownCancellation.Token).ConfigureAwait(false);
            if (_shutdownRequested)
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _deviceCoordinator = coordinator;
                coordinatorAdopted = true;
                StartOnUiThread();
            });
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!coordinatorAdopted && coordinator is not null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void StartOnUiThread()
    {
        // The resident shell is the sole device-cycle authority. Overlay test deliberately never
        // creates this object, opens its IPC, discovers packages, or starts DeviceHost.
        if (!_overlayTestOnly)
        {
            _messageWindow = MessageWindow.Create();
            _messageWindow.SessionEnding += OnSessionEnding;
            // The device cycle follows the session it belongs to. Without these the Claw's
            // controller, motion, OEM and suppressor services stayed live across a lock and a
            // system sleep, and the fresh cycle generation the resume contract requires was never
            // established afterwards.
            _messageWindow.SessionLocked += OnSessionLocked;
            _messageWindow.SessionUnlocked += OnSessionUnlocked;
            _messageWindow.SystemSuspending += OnSystemSuspending;
            _messageWindow.SystemResumed += OnSystemResumed;
            if (_deviceCoordinator is not null)
            {
                _deviceOverlay = new DeviceOverlayBridge(_deviceCoordinator);
            }
            else if (_suppressDeviceIntegration)
            {
                Log.Warn("Device cycle: suppressed for an installer rollback with unverified prior host state.");
            }
        }
        else
        {
            _deviceOverlay = new SimulatedDeviceOverlaySource();
        }

        _performance = new PerformanceService(
            _overlayTestOnly ? new SimulatedRtssAdapter() : new RtssNativeAdapter(),
            _overlayTestOnly ? PersistSimulatedPerformancePolicyAsync : PersistPerformancePolicyAsync,
            BuildPerformancePolicy(_config, forceEnabled: _overlayTestOnly));
        _performanceOverlay = new PerformanceOverlayBridge(_performance);

        // Overlay-test runs without a real display to move, and pairing is the one performance
        // concern that changes hardware state rather than an RTSS profile.
        if (!_overlayTestOnly)
        {
            _refreshPairing = new RefreshRatePairingService();
            _refreshPairing.SetStrategy(_config.Performance.FrameLimitStrategy);
            _performance.StateChanged += OnPerformanceStateForPairing;
        }
        if (!_overlayTestOnly)
        {
            _steamUiTransport = new PersistentSteamUiTransport();
            _runningApplications = new RunningApplicationMonitor(
                new SteamRunningApplicationProbe(_steamUiTransport));

            // The second identity source. It feeds the same monitor rather than driving policy on
            // its own, so per-application settings also work on the desktop and for titles Steam
            // never launched — which is the only way the overlay's per-game rows mean anything
            // outside a Steam game.
            _foregroundWindows = new ForegroundWindowWatcher();
            _foregroundWindows.ApplicationChanged += OnForegroundApplicationChanged;
            if (_deviceCoordinator is { } deviceCoordinator)
            {
                _autoTdp = new AutoTdpService(
                    new RtssFrametimeReader(),
                    deviceCoordinator.CapabilitySnapshot,
                    (capabilityId, instanceId, value, token) =>
                        deviceCoordinator.ExecuteCapabilityAsync(
                            capabilityId,
                            instanceId,
                            value,
                            TimeSpan.FromSeconds(5),
                            CapabilityCommandOrigin.AutomaticControl,
                            token),
                    TargetFrametimeMs);
                _autoTdp.Apply(_config.DeviceIntegration.AutoTdpEnabled);
                // The coordinator surfaces AutoTDP on the Device page but never owns its lifetime;
                // it only reads the state to render a row.
                deviceCoordinator.AttachAutoTdpStatus(() => _autoTdp!.Status);
                // A power limit the user set by hand pauses control permanently. The hook is rooted
                // here because this is where both objects exist; every surface's write already goes
                // through the coordinator, so this is the one place that sees all of them.
                deviceCoordinator.AttachAutoTdpManualOverride(watts => _autoTdp?.NoteManualChange(watts));

                // Reads the descriptor at apply time rather than caching one: a plugin republishes
                // its capabilities across a cycle, and a curve checked against a stale descriptor is
                // exactly the case this check exists to catch.
                _profileApplier = new DeviceProfileApplier(
                    capabilityId => deviceCoordinator.CapabilitySnapshot()
                        .FirstOrDefault(view => string.Equals(
                            view.Descriptor.CapabilityId,
                            capabilityId,
                            StringComparison.Ordinal))?.Descriptor,
                    async (capabilityId, value, token) =>
                    {
                        CapabilityCommandResult result = await deviceCoordinator
                            .ExecuteCapabilityAsync(
                                capabilityId,
                                null,
                                value,
                                TimeSpan.FromSeconds(5),
                                CapabilityCommandOrigin.AutomaticControl,
                                token).ConfigureAwait(false);
                        // Unverified counts as applied: many EC writes have no readback, and
                        // treating the absence of confirmation as failure would report every one of
                        // them as broken. A timeout does not count — whether it was written is
                        // unknown, and claiming success there is the one answer that misleads.
                        return result.Outcome is CommandOutcome.AppliedVerified
                            or CommandOutcome.AppliedUnverified;
                    });
                // Without this the Device page and the native QAM row only refreshed when some
                // unrelated device event happened to arrive, so an AutoTDP transition — including
                // the pause above — could sit invisible for as long as the session stayed quiet.
                _autoTdp.StatusChanged += OnAutoTdpStatusChanged;
            }

            _runningApplicationTargets = new RunningApplicationCoordinator(
                _runningApplications,
                _performance.SetTargetAsync,
                _deviceCoordinator is null
                    ? null
                    : ApplyRunningApplicationTargetAsync);
        }

        _monitor = new SteamMonitor();
        if (!_overlayTestOnly)
        {
            _desktopHost = new ExplorerDesktopHost();
        }
        _modes = _desktopHost is null
            ? new SessionModes(_config, _monitor)
            : new SessionModes(_config, _monitor, _desktopHost);
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
        // Started here rather than by the taskbar, because Steam's audio namespace has to answer
        // while the taskbar is closed. Overlay-test keeps the old behaviour and lets the status
        // cluster own its own, since no Steam surface exists there to serve.
        if (!_overlayTestOnly)
        {
            _audio = new AudioManager();
            _audio.Start();

            // Not started here: scanning is expensive and belongs to whichever surface is showing a
            // network list. The manager exists for the whole session so Steam's Internet page can
            // drive it, but it stays idle until something asks.
            _radios = new RadioManager();
        }

        _overlay = new OverlayController(
            _config,
            _monitor,
            _modes,
            _keepAwake,
            previewOnly: _overlayTestOnly,
            device: _deviceOverlay,
            performance: _performanceOverlay,
            audio: _audio,
            radios: _radios);

        // WSGM's own navigation runs on the managed canonical stream when one is delivering, and on
        // SDL otherwise. Subscribed here rather than inside the overlay because this is where both
        // objects exist: the coordinator owns the stream and the controller owns the surfaces.
        // Nothing is unsubscribed on device teardown — the manager simply stops raising, and the
        // router falls back to SDL, which never stopped running.
        if (_deviceCoordinator is { } canonicalSource && _overlay is { } overlay)
        {
            // Posted, never called inline. This event is raised from DeviceHostClient's registered
            // ThreadPool wait and runs straight into GamepadNavigation, which reads window
            // visibility and mutates Avalonia focus and controls — UI-thread-owned state that a
            // worker thread must not touch. The rate is bounded by design: the manager raises this
            // only while a WSGM surface has captured input.
            canonicalSource.UiSampleReceived += sample =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => overlay.SubmitCanonicalSample(sample));
            canonicalSource.StateChanged += state =>
            {
                if (state is not DeviceCycleState.Active)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(overlay.ManagedInputLost);
                }
            };
            // The cycle staying Active is not the same as samples still arriving. Disabling
            // controller management runs make-safe and leaves the cycle Active while the plugin
            // stops publishing, so without this the router waited on a source that had gone quiet
            // and WSGM's own surfaces stopped answering a controller SDL could already see.
            canonicalSource.ControllerStatusChanged += status =>
            {
                if (status.State is not ControllerManagementState.Active)
                {
                    Log.Info(
                        $"Managed UI input falls back to SDL: controller management is "
                        + $"{status.State} ({status.Detail}).");
                    Avalonia.Threading.Dispatcher.UIThread.Post(overlay.ManagedInputLost);
                }
            };
        }

        _deviceCoordinator?.ConfigureOemActions(new DeviceOemActionServices
        {
            ToggleOverlayAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOverlay();
                return _overlay is not null;
            }, cancellationToken),
            ToggleSteamQuickAccessAsync = cancellationToken => RunUiActionAsync(() =>
                _monitor?.IsAlive is true
                && Steam.IsBigPictureVisible
                && Steam.TrySendBigPictureShortcut(BigPictureShortcut.QuickAccess),
                cancellationToken),
            ToggleDevicePageAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ShowDevicePage();
                return _overlay is not null;
            }, cancellationToken),
            ToggleTaskbarAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleTaskbar();
                return _overlay is not null;
            }, cancellationToken),
            ToggleDesktopGameModeAsync = cancellationToken => RunUiActionAsync(() =>
            {
                if (_modes is null)
                {
                    return false;
                }

                if (ExplorerControl.IsRunningInSession())
                {
                    _modes.EnterGameMode();
                }
                else
                {
                    _modes.EnterDesktopMode();
                }

                return true;
            }, cancellationToken),
            ToggleOnScreenKeyboardAsync = static _ => Task.FromResult(false),
            CyclePerformanceProfileAsync = static _ => Task.FromResult(false),
            CyclePerformanceOverlayLevelAsync = CyclePerformanceOverlayLevelAsync,
            SetRearButtonAsync = static (_, _) => Task.FromResult(false),
        });
        if (!_overlayTestOnly)
        {
            _steamUi = new SteamUiSessionHost(
                _steamUiTransport
                    ?? throw new InvalidOperationException("Steam UI transport was not created."),
                cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOverlay();
                return _overlay is not null;
            }, cancellationToken),
                _deviceCoordinator,
                _performance,
                _audio,
                _radios);
            _steamUi.SetPerfSupport(ReadNativeQamPerfSupport);
            _steamUi.Apply(_config.Cef.Enabled && _config.Cef.NativeQuickAccess);
            ApplyGlyphConfig(_config);
            if (_deviceCoordinator is not null)
            {
                // Two sources change the active profile: the package publishing its profiles, and
                // the user changing the selection mode. Both land on the same apply.
                _deviceCoordinator.PhysicalGlyphProfilesChanged += OnPhysicalGlyphProfilesChanged;
            }
        }

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
            _messageWindow!,
            () => DisplayScale.GetUiScalePercent(_config) / 100.0);
        _displayMute = new DisplayOffMuteService(_messageWindow!);
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

        _bootWork = Task.Run(async () =>
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
            _ = TrimAfterBootSettlesAsync(_shutdownCancellation.Token);
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
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
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
                    }
                    else if (result is BootTakeoverResult.DesktopPreserved)
                    {
                        ResumePreservedDesktopAfterBootFailure();
                    }
                    else if (result is BootTakeoverResult.DesktopRestoreRequired)
                    {
                        BeginDesktopModeAfterBootFailure();
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
            _ = TrimAfterBootSettlesAsync(_shutdownCancellation.Token);
        });
    }

    /// <summary>Runs the takeover phase only (input-desktop barrier, explorer
    /// readiness, orderly exit, posture, tray host). Returns false when it failed
    /// open with explorer preserved — the caller then skips the launch sequence.</summary>
    /// <param name="cancellationToken">Cancelled by the splash's desktop recovery.
    /// Before the orderly exit it preserves Explorer; after that irreversible
    /// request began, it skips game-mode setup so the caller can restart Explorer.</param>
    private async Task<BootTakeoverResult> RunBootTakeoverAsync(CancellationToken cancellationToken)
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
        ExplorerPreparationResult preparation = _desktopHost is null
            ? new ExplorerPreparationResult(false, ExplorerShellRejection.ProcessUnavailable, "host-unavailable")
            : await _desktopHost.PrepareForExplorerExitAsync(cancellationToken).ConfigureAwait(false);
        if (!preparation.Prepared)
        {
            Log.Warn("Boot takeover refused before Explorer exit because no verified jobless "
                + $"shell launch owner could be retained ({preparation.Detail}).");
            bool desktopPresent;
            try
            {
                desktopPresent = ExplorerControl.IsRunningInSession()
                    || NativeMethods.GetShellWindow() != 0
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
        var exited = ExplorerControl.ExitExplorerAndWait(TimeSpan.FromSeconds(30));
        // Posting Explorer's orderly-exit command is irreversible. A desktop
        // request that landed during the bounded wait must recover by starting
        // Explorer again, never continue into posture/tray/Steam game mode.
        cancellationToken.ThrowIfCancellationRequested();
        if (!exited)
        {
            bool explorerStillRunning;
            try
            {
                explorerStillRunning = ExplorerControl.IsRunningInSession();
            }
            catch (Exception ex)
            {
                Log.Error("Checking Explorer after failed boot takeover failed", ex);
                explorerStillRunning = false;
            }
            Log.Warn(explorerStillRunning
                ? "Boot takeover failed open — explorer was preserved."
                : "Boot takeover could not prove Explorer exited and no live shell remains — restoring desktop.");
            return explorerStillRunning
                ? BootTakeoverResult.DesktopPreserved
                : BootTakeoverResult.DesktopRestoreRequired;
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

        return BootTakeoverResult.EnteredGameMode;
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
        // The boot sequence skips its Big Picture start once the monitor is paused. Windowed
        // Steam starts only after Explorer's actual taskbar owner has been verified.
        _modes!.EnterDesktopMode(startSteamDesktop: true);
    }

    /// <summary>Completes a refused boot takeover without starting another Explorer. The original
    /// taskbar owner is still present, so dismissing the opaque cover is the recovery operation.</summary>
    private void ResumePreservedDesktopAfterBootFailure()
    {
        _splash?.Dismiss("takeover refused");
        _inGameMode = false;
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        _modes!.ReportWarning(SessionModes.ExplorerTakeoverRefusedWarning);
    }

    /// <summary>Starts the ordinary verified desktop restoration after boot crossed an uncertain
    /// Explorer-exit boundary. The transition gate has already been released by the caller.</summary>
    private void BeginDesktopModeAfterBootFailure()
    {
        _splash?.Dismiss("takeover recovery");
        _modes!.ReportWarning(SessionModes.ExplorerExitFailedWarning);
        _modes.EnterDesktopMode();
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
        if (_shutdownRequested)
        {
            return;
        }
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
        if (_shutdownRequested)
        {
            return;
        }
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
                if (_steamUi is not null)
                {
                    try
                    {
                        await _steamUi.DisableAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Retracting the native Steam UI patch failed: {ex.Message}");
                    }
                }

                try
                {
                    await SteamPageBridge.DisableBadgeAsync().ConfigureAwait(false);
                    await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
                    await SteamNetworkIndicator.DisableAsync().ConfigureAwait(false);
                    await SteamDownloadSort.DisableAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Retracting legacy injected Steam UI failed: {ex.Message}");
                }
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

    private void OnSessionLocked() => QueueDevicePowerTransition(suspend: true, "session locked");

    private void OnSessionUnlocked() => QueueDevicePowerTransition(suspend: false, "session unlocked");

    private void OnSystemSuspending() => QueueDevicePowerTransition(suspend: true, "system suspending");

    private void OnSystemResumed() => QueueDevicePowerTransition(suspend: false, "system resumed");

    /// <summary>Quiesces or revives the device cycle with the session it belongs to.</summary>
    /// <param name="suspend">Whether the cycle should quiesce.</param>
    /// <param name="reason">The notification that asked for it, for the log.</param>
    /// <remarks>
    /// Edge-triggered and serialized, because the four notifications overlap: a sleep started from
    /// the lock screen delivers a lock and a suspend, and Windows sends both resume events for one
    /// wake. Neither coordinator call is idempotent — resume advances the cycle generation — so
    /// only a real transition is forwarded, and each one waits for the previous to finish.
    /// </remarks>
    private void QueueDevicePowerTransition(bool suspend, string reason)
    {
        if (_deviceCoordinator is not { } coordinator)
        {
            return;
        }

        lock (_devicePowerGate)
        {
            if (_deviceSuspended == suspend)
            {
                Log.Info(
                    $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): the "
                    + $"cycle is already {(suspend ? "suspended" : "running")}.");
                return;
            }

            _deviceSuspended = suspend;
            _devicePowerWork = ApplyDevicePowerTransitionAsync(
                _devicePowerWork,
                coordinator,
                suspend,
                reason);
        }
    }

    private static async Task ApplyDevicePowerTransitionAsync(
        Task previous,
        DeviceCoordinator coordinator,
        bool suspend,
        string reason)
    {
        // Never faults: the continuation below reports its own failures and returns normally, so
        // awaiting the previous transition cannot throw here.
        await previous.ConfigureAwait(false);
        try
        {
            if (suspend)
            {
                await coordinator.SuspendAsync().ConfigureAwait(false);
            }
            else
            {
                await coordinator.ResumeAsync().ConfigureAwait(false);
            }

            Log.Info($"Device cycle {(suspend ? "suspended" : "resumed")}: {reason}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Device cycle {(suspend ? "suspend" : "resume")} failed ({reason})", ex);
        }
    }

    /// <summary>Forwards an AutoTDP transition to the surfaces that render it.</summary>
    /// <param name="status">The projection AutoTDP just published.</param>
    /// <remarks>
    /// Raised from AutoTDP's own tick loop, so it is posted to the dispatcher before the overlay
    /// bridge and the native-QAM row rebuild anything: both are UI-owned.
    /// </remarks>
    private void OnAutoTdpStatusChanged(AutoTdpStatus status)
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _deviceCoordinator?.NoteAutoTdpStatusChanged());

    /// <summary>Hands a foreground application change to the running-application monitor.</summary>
    /// <param name="executable">Foreground executable file name.</param>
    /// <remarks>
    /// Straight through, with no policy of its own: the monitor's projection decides whether this
    /// identity is used at all, so the precedence between Steam and the foreground stays in the one
    /// pure function that can be tested.
    /// </remarks>
    private void OnForegroundApplicationChanged(string executable)
        => _runningApplications?.ReportForeground(executable);

    /// <summary>Reports what the device can back for Steam's reactivated performance panel.</summary>
    /// <returns>The support the panel decides each control's availability from.</returns>
    /// <remarks>
    /// This session is the only place that can answer: the frame-limit notches come from the
    /// pairing service's runtime mode discovery, and variable refresh rate from the device plugin's
    /// published capability. Reporting a control as unsupported hides it, which is why every branch
    /// here fails toward "not supported" — a hidden control is always better than one whose writes
    /// go nowhere.
    /// <para>
    /// The manual refresh-rate row is offered only under <c>FrameLimitOnly</c>. Under the pairing
    /// strategies WSGM chooses the refresh rate itself to match the cap, and a manual row would
    /// fight the pairing on every change.
    /// </para>
    /// </remarks>
    private NativeQamPerfSupport ReadNativeQamPerfSupport()
    {
        RefreshRatePairingService? pairing = _refreshPairing;
        IReadOnlyList<int> options = pairing?.FrameLimitOptions() ?? [];
        bool manualRefresh = _config.Performance.FrameLimitStrategy is FrameLimitStrategy.FrameLimitOnly;

        bool vrr = false;
        if (_deviceCoordinator is { } coordinator)
        {
            vrr = coordinator.CapabilitySnapshot().Any(view =>
                view.Descriptor.Role is CapabilityRole.VariableRefreshRate
                && view.Projection.State.Available);
        }

        IReadOnlyList<int> refreshRates = manualRefresh
            ? DisplayProfiles.EnumerateAcceptedRefreshRates()
            : [];
        return new NativeQamPerfSupport(
            options,
            vrr,
            manualRefresh && refreshRates.Count > 0,
            refreshRates.Count > 0 ? refreshRates.Min() : null,
            refreshRates.Count > 0 ? refreshRates.Max() : null);
    }

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
                        if (_disposed)
                        {
                            return;
                        }
                        // One instance for every reader: the volume OSD's UI-scale
                        // callback and DisplayScale's saved-scale snapshot must not
                        // drift onto different AppConfig objects.
                        _config = config;
                        ApplyDeviceConfig(config);
                        ApplyPerformanceConfig(config);
                        ApplyCefMasterSwitch(config.Cef.Enabled);
                        if (config.Cef.Enabled)
                        {
                            _steamUi?.Apply(config.Cef.NativeQuickAccess);
                            ApplyGlyphConfig(config);
                        }
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

    private async Task<bool> RunUiActionAsync(
        Func<bool> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shutdownRequested)
        {
            return false;
        }
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            _shutdownRequested ? false : action());
    }

    private async Task<bool> CyclePerformanceOverlayLevelAsync(
        CancellationToken cancellationToken)
    {
        if (_shutdownRequested)
        {
            return false;
        }
        PerformanceService? performance = _performance;
        if (performance is null || !performance.Enabled)
        {
            return false;
        }

        PerformanceState state = performance.Current;
        RtssCapabilities? capabilities = state.Probe.Capabilities;
        if (state.Probe.Availability != RtssAvailability.Ready
            || capabilities is null
            || !capabilities.Supports(PerformanceControl.OverlayLevel))
        {
            return false;
        }

        int current = state.Observed.OverlayLevel ?? state.Desired.OverlayLevel ?? int.MinValue;
        int[] levels = [.. capabilities.OverlayLevels.Order()];
        if (levels.Length == 0)
        {
            return false;
        }

        int next = levels.FirstOrDefault(value => value > current, levels[0]);
        PerformanceCommandState result = await performance.SetAsync(
            PerformanceControl.OverlayLevel,
            next,
            PerformancePersistenceTarget.Automatic,
            "oem-action",
            Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
        return result.Phase is PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified;
    }

    private void OnSessionEnding()
    {
        if (_disposed)
        {
            return;
        }
        Log.Info("Interactive session is ending; requesting bounded session cleanup.");
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    /// <summary>Runs bounded device cleanup before the application lifetime ends.</summary>
    public ValueTask DisposeAsync() => ShutdownAsync(
        ApplicationShutdownReason.Normal,
        DateTimeOffset.UtcNow.Add(ApplicationShutdownCoordinator.BudgetFor(
            ApplicationShutdownReason.Normal)));

    /// <summary>Runs session cleanup with the device protocol reason and one outer deadline.</summary>
    internal async ValueTask ShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownRequested = true;
        _shutdownCancellation.Cancel();
        Exception? startupFailure = null;
        if (_startupTask is not null)
        {
            try
            {
                await _startupTask;
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                startupFailure = ex;
                Log.Error("Shell startup failed before shutdown cleanup", ex);
            }
        }
        _bootTakeover?.RequestShutdown();
        _modes?.RequestShutdown();
        Exception? uiCleanupFailure = startupFailure;
        try
        {
            _splash?.Dismiss("application shutdown");
        }
        catch (Exception ex)
        {
            uiCleanupFailure = RetainFirstShutdownFailure(uiCleanupFailure, ex);
            Log.Error("Dismissing the boot splash during application shutdown failed", ex);
        }
        // Close input admission on the UI thread before any safety-critical asynchronous cleanup.
        try
        {
            _overlay?.Dispose();
        }
        catch (Exception ex)
        {
            uiCleanupFailure = RetainFirstShutdownFailure(uiCleanupFailure, ex);
            Log.Error("Closing overlay command admission during application shutdown failed", ex);
        }
        finally
        {
            _overlay = null;
        }
        _tabBootSyncCancellation.Cancel();
        _downloadSortCancellation.Cancel();

        // Device cleanup is the safety-critical part of the outer application budget.
        // Run it before waiting on shell transitions or doing Explorer/CEF/RTSS teardown.
        // If the outer owner reaches its deadline, process exit still closes DeviceHost's job
        // while the shell anchor remains available for owner-loss desktop recovery.
        Exception? deviceCleanupFailure = null;
        // Before the coordinator, deliberately. AutoTDP restores the limit it took over from
        // through that coordinator's capability path, so disposing it afterwards issued the restore
        // into an already-disconnected host and left the handheld on the last automatically
        // selected wattage on every exit, update, uninstall and session end.
        if (_autoTdp is not null)
        {
            _autoTdp.StatusChanged -= OnAutoTdpStatusChanged;
            try
            {
                await _autoTdp.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("AutoTDP restoration was unverified during application shutdown", ex);
            }
            finally
            {
                _autoTdp = null;
                _deviceCoordinator?.AttachAutoTdpStatus(null);
                _deviceCoordinator?.AttachAutoTdpManualOverride(null);
            }
        }

        if (_deviceCoordinator is not null)
        {
            DeviceStopReason deviceReason = reason switch
            {
                ApplicationShutdownReason.Update =>
                    DeviceStopReason.Updating,
                ApplicationShutdownReason.SessionEnd =>
                    DeviceStopReason.SessionEnding,
                ApplicationShutdownReason.Uninstall =>
                    DeviceStopReason.Uninstalling,
                _ => DeviceStopReason.WsgmExiting,
            };
            _deviceCoordinator.PhysicalGlyphProfilesChanged -= OnPhysicalGlyphProfilesChanged;
            try
            {
                await _deviceCoordinator.ShutdownAsync(deviceReason, deadline).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                deviceCleanupFailure = ex;
                Log.Error(
                    "Device cleanup was unverified; remaining shell cleanup continues",
                    ex);
            }
            finally
            {
                _deviceCoordinator = null;
            }
        }

        Exception? retainedShutdownFailure = CombineShutdownFailures(
            uiCleanupFailure,
            deviceCleanupFailure);
        uiCleanupFailure = null;
        await ContinueShutdownWithRetainedFailureAsync(
            retainedShutdownFailure,
            async () =>
            {
                // Shutdown rejects every new transition before reaching this point. Let the one
                // existing transition and the separately-rooted boot worker cross their Explorer/UI
                // boundaries before disposing anything they can still access. The application
                // coordinator owns the only deadline; a nested timeout here could retire the recovery
                // anchor underneath them.
                if (_modes is not null)
                {
                    await _modes.WaitForTransitionAsync().ConfigureAwait(false);
                }
                if (_bootWork is not null)
                {
                    await _bootWork.ConfigureAwait(false);
                    _bootWork = null;
                }

                bool trayRetired = false;
                try
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RetireTrayHostForShutdown);
                    trayRetired = true;
                }
                catch (Exception ex)
                {
                    uiCleanupFailure = RetainFirstShutdownFailure(uiCleanupFailure, ex);
                    Log.Error("Retiring the WSGM taskbar during application shutdown failed", ex);
                }
                try
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(DisposeUiOwnedSessionResources);
                }
                catch (Exception ex)
                {
                    uiCleanupFailure = RetainFirstShutdownFailure(uiCleanupFailure, ex);
                    Log.Error("UI-owned shell cleanup failed during application shutdown", ex);
                }

                bool desktopVerified = trayRetired
                    && await RestoreDesktopBeforeShutdownAsync(reason, deadline).ConfigureAwait(false);
                if (desktopVerified && _desktopHost is not null)
                {
                    await _desktopHost.DisposeAsync().ConfigureAwait(false);
                    _desktopHost = null;
                }

                // AutoTDP is already gone: it is disposed before the device coordinator, above,
                // because its restoration needs that coordinator's write path.
                if (_runningApplicationTargets is not null)
                {
                    await _runningApplicationTargets.DisposeAsync().ConfigureAwait(false);
                    _runningApplicationTargets = null;
                }
                if (_foregroundWindows is not null)
                {
                    _foregroundWindows.ApplicationChanged -= OnForegroundApplicationChanged;
                    _foregroundWindows.Dispose();
                    _foregroundWindows = null;
                }
                if (_runningApplications is not null)
                {
                    await _runningApplications.DisposeAsync().ConfigureAwait(false);
                    _runningApplications = null;
                }
                await _cefMasterGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_steamUi is not null)
                    {
                        await _steamUi.DisposeAsync().ConfigureAwait(false);
                        _steamUi = null;
                    }
                }
                finally
                {
                    _cefMasterGate.Release();
                }
                if (_steamUiTransport is not null)
                {
                    await _steamUiTransport.DisposeAsync().ConfigureAwait(false);
                    _steamUiTransport = null;
                }
                if (_performance is not null)
                {
                    _performance.StateChanged -= OnPerformanceStateForPairing;
                    await _performance.DisposeAsync().ConfigureAwait(false);
                    _performance = null;
                }

                // Before the session ends, not after: the applied rate is transient and would
                // heal on its own eventually, but leaving the desktop at 48 Hz until something
                // else resets it is a change the user never made and would have to hunt for.
                if (_refreshPairing is not null)
                {
                    _ = _refreshPairing.Restore();
                    _refreshPairing = null;
                }

                // After the Steam host and the overlay, both of which hold them.
                if (_audio is not null)
                {
                    _audio.Dispose();
                    _audio = null;
                }

                if (_radios is not null)
                {
                    _radios.Dispose();
                    _radios = null;
                }
                _tabBootSyncCancellation.Dispose();
                _downloadSortCancellation.Dispose();
                _shutdownCancellation.Dispose();

                if (!desktopVerified)
                {
                    throw new InvalidOperationException(
                        "Application shutdown could not verify a usable Explorer desktop; "
                        + "the retained shell anchor will recover after process exit.");
                }
                ThrowIfUiCleanupIncomplete(uiCleanupFailure);
            }).ConfigureAwait(false);
    }

    /// <summary>Keeps the earliest UI/input-admission cleanup failure so later cleanup cannot
    /// accidentally turn an incomplete shutdown into a verified outcome.</summary>
    internal static Exception RetainFirstShutdownFailure(Exception? current, Exception failure) =>
        current ?? failure;

    /// <summary>Combines independently retained shutdown failures without discarding either cause.</summary>
    internal static Exception? CombineShutdownFailures(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }
        if (second is null)
        {
            return first;
        }

        List<Exception> failures = [];
        if (first is AggregateException firstAggregate)
        {
            failures.AddRange(firstAggregate.Flatten().InnerExceptions);
        }
        else
        {
            failures.Add(first);
        }
        if (second is AggregateException secondAggregate)
        {
            failures.AddRange(secondAggregate.Flatten().InnerExceptions);
        }
        else
        {
            failures.Add(second);
        }
        return new AggregateException("Multiple application shutdown steps were unverified.", failures);
    }

    /// <summary>Runs all remaining shell cleanup before reporting an earlier retained failure.</summary>
    internal static async ValueTask ContinueShutdownWithRetainedFailureAsync(
        Exception? retainedFailure,
        Func<Task> remainingCleanupAsync)
    {
        ArgumentNullException.ThrowIfNull(remainingCleanupAsync);
        Exception? remainingFailure = null;
        try
        {
            await remainingCleanupAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            remainingFailure = ex;
        }

        Exception? failure = CombineShutdownFailures(retainedFailure, remainingFailure);
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                failure);
        }
    }

    /// <summary>Reports any retained UI/input-admission cleanup failure to the outer coordinator.</summary>
    internal static void ThrowIfUiCleanupIncomplete(Exception? failure)
    {
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Application shutdown completed desktop recovery but UI-owned cleanup was incomplete.",
                failure);
        }
    }

    private void DisposeUiOwnedSessionResources()
    {
        lock (_configDebounceGate)
        {
            _configDebounce?.Dispose();
            _configDebounce = null;
        }
        _configWatcher?.Dispose();
        _configWatcher = null;
        _splash = null;
        if (_messageWindow is not null)
        {
            _messageWindow.SessionEnding -= OnSessionEnding;
            _messageWindow.SessionLocked -= OnSessionLocked;
            _messageWindow.SessionUnlocked -= OnSessionUnlocked;
            _messageWindow.SystemSuspending -= OnSystemSuspending;
            _messageWindow.SystemResumed -= OnSystemResumed;
        }
        _messageWindow = null;
        _overlay?.Dispose();
        _overlay = null;
        _performanceOverlay?.Dispose();
        _performanceOverlay = null;
        _deviceOverlay?.Dispose();
        _deviceOverlay = null;
        _displayMute?.Dispose();
        _displayMute = null;
        _volumeButtons?.Dispose();
        _volumeButtons = null;
        _cardVolumes?.Dispose();
        _cardVolumes = null;
        _cardAcfWatcher?.Dispose();
        _cardAcfWatcher = null;
        _networkIndicator?.Dispose();
        _networkIndicator = null;
        _startupWatcher?.Dispose();
        _startupWatcher = null;
        if (_keepAwake is not null)
        {
            _keepAwake.DownloadActivityChanged -= OnDownloadActivityChanged;
            _keepAwake.Dispose();
            _keepAwake = null;
        }
        _monitor?.Dispose();
        _monitor = null;
    }

    private void RetireTrayHostForShutdown()
    {
        // Every later cleanup is recoverable through process exit. Explorer restoration is not:
        // it must never run beside WSGM's Shell_TrayWnd and create two taskbar owners.
        _trayHost?.Dispose();
        _trayHost = null;
    }

    private async Task<bool> RestoreDesktopBeforeShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline)
    {
        ExplorerDesktopHost? desktopHost = _desktopHost;
        if (desktopHost is null || reason is ApplicationShutdownReason.SessionEnd)
        {
            return true;
        }

        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        try
        {
            // Reproduce the non-Explorer half of the ordinary desktop transition before the shell
            // appears. Update already asked Steam to exit so its mapped payload can be replaced;
            // never race that exit with a protocol URL that could start the client again.
            if (reason is not ApplicationShutdownReason.Update)
            {
                _modes?.ExitBigPicture();
            }
            DisplayScale.ApplyDesktopMode(_config);
        }
        catch (Exception ex)
        {
            // Explorer recovery is the higher-priority safety boundary. Program's final posture
            // cleanup gets another chance after Avalonia exits.
            Log.Error("Preparing desktop posture during application shutdown failed", ex);
        }

        remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        try
        {
            ExplorerDesktopResult result = await desktopHost.RestoreDesktopAsync(remaining)
                .ConfigureAwait(false);
            return result.Outcome is ExplorerDesktopOutcome.Normal
                or ExplorerDesktopOutcome.Degraded;
        }
        catch (Exception ex)
        {
            Log.Error("Application shutdown Explorer desktop recovery failed", ex);
            return false;
        }
    }

    private void ApplyDeviceConfig(AppConfig config)
    {
        DeviceCoordinator? coordinator = _deviceCoordinator;
        if (coordinator is null)
        {
            return;
        }

        // AutoTDP is applied before the coordinator: turning Device Integration off must stop
        // AutoTDP and restore the previous power limit while the capability is still writable.
        _autoTdp?.Apply(config.DeviceIntegration.Enabled && config.DeviceIntegration.AutoTdpEnabled);
        _ = ObserveDeviceConfigAsync(coordinator, config);
    }

    private static bool GlyphsEnabled(AppConfig config) =>
        config.Cef.Enabled
        && config.DeviceIntegration.Enabled
        && config.DeviceIntegration.GlyphSelection is not DeviceGlyphSelection.NativeSteam;

    private void OnPhysicalGlyphProfilesChanged() => ApplyGlyphConfig(_config);

    private async Task ApplyRunningApplicationTargetAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        _autoTdp?.ApplyRunningApplication(snapshot);
        if (_deviceCoordinator is { } coordinator)
        {
            await coordinator.ApplyRunningApplicationAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        // Authored profiles follow the same identity as everything else per-application, which is
        // the point of resolving them here rather than from a second observer: the fan curve and the
        // controller target can never disagree about which application is running.
        await ApplyDeviceProfilesAsync(snapshot.ApplicationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Applies the authored profile in force for the running application.</summary>
    /// <param name="applicationId">The running application identity, or null for none.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <remarks>
    /// Every failure here is contained. A profile that cannot be applied is a degraded feature, not
    /// a reason to fault the session, and the applier already logs which step refused it.
    /// </remarks>
    private async Task ApplyDeviceProfilesAsync(
        string? applicationId,
        CancellationToken cancellationToken)
    {
        if (_profileApplier is not { } applier)
        {
            return;
        }

        PluginSettingsScope? scope = _config.DeviceIntegration.PluginSettings
            .FirstOrDefault(candidate => candidate.ProfileSelections.Count > 0);
        if (scope is null)
        {
            return;
        }

        foreach (DeviceProfileSelection selection in scope.ProfileSelections)
        {
            try
            {
                await applier.ApplyAsync(
                    scope.ProfileSelections,
                    scope.Profiles,
                    selection.CapabilityId,
                    applicationId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Applying the device profile for '{selection.CapabilityId}' failed: "
                    + ex.Message);
            }
        }
    }

    /// <summary>
    /// The deadline AutoTDP judges frame delivery against.
    /// </summary>
    /// <remarks>
    /// The applied RTSS frame limit when there is one, because that is the rate the user asked for
    /// and delivering it is the whole goal. Without a limit the deadline falls back to 60 Hz rather
    /// than to the panel's maximum: chasing an uncapped refresh rate would push the power limit up
    /// for as long as the game could absorb it, which is the opposite of what AutoTDP is for.
    /// </remarks>
    private double TargetFrametimeMs()
    {
        PerformanceState? state = _performance?.Current;
        int limit = state?.Observed.FrameLimit ?? 0;
        if (limit <= 0)
        {
            limit = state?.Desired.FrameLimit ?? 0;
        }

        return limit > 0 ? 1000d / limit : 1000d / 60d;
    }

    /// <summary>
    /// Applies both halves of physical glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <remarks>
    /// The selector alone changes nothing a user can see. Without the resolved profile the
    /// stylesheet has no rules, and the patch refuses to install an empty one — which is how
    /// physical glyphs were inert.
    /// </remarks>
    private void ApplyGlyphConfig(AppConfig config)
    {
        SteamUiSessionHost? steamUi = _steamUi;
        if (steamUi is null)
        {
            return;
        }

        bool enabled = GlyphsEnabled(config);
        steamUi.ApplyGlyphs(
            enabled,
            enabled ? _deviceCoordinator?.PhysicalGlyphSelectionSnapshot().Profile : null);
    }

    private void ApplyPerformanceConfig(AppConfig config)
    {
        PerformanceService? performance = _performance;
        if (performance is null)
        {
            return;
        }

        _refreshPairing?.SetStrategy(config.Performance.FrameLimitStrategy);
        _ = ObservePerformanceConfigAsync(
            performance,
            BuildPerformancePolicy(config, forceEnabled: _overlayTestOnly));
    }

    /// <remarks>
    /// Runs off the state event rather than inside <see cref="PerformanceService"/>, because that
    /// service owns RTSS profiles and this changes a display mode — two different pieces of hardware
    /// with different failure modes and different restore obligations.
    /// <para>
    /// Only an actual change is acted on. The state event fires on every poll, and re-applying the
    /// same mode repeatedly would put a driver round trip on a two-second timer forever.
    /// </para>
    /// </remarks>
    private void OnPerformanceStateForPairing(PerformanceState state)
    {
        if (_refreshPairing is not { } pairing)
        {
            return;
        }

        int limit = state.Desired.FrameLimit ?? 0;
        if (limit == _pairedFrameLimit)
        {
            return;
        }

        _pairedFrameLimit = limit;

        // Uncapped hands the display back: there is no cadence left to pair against, and holding a
        // reduced refresh rate after the cap is gone would cap frames by the back door.
        if (limit <= 0)
        {
            _ = pairing.Restore();
            return;
        }

        _ = pairing.ApplyForCap(limit);
    }

    private static async Task ObservePerformanceConfigAsync(
        PerformanceService performance,
        PerformancePolicy policy)
    {
        try
        {
            await performance.UpdatePolicyAsync(policy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("RTSS performance config apply failed", ex);
        }
    }

    private static PerformancePolicy BuildPerformancePolicy(
        AppConfig config,
        bool forceEnabled)
    {
        List<PerformanceApplicationPolicy> applications = [];
        foreach (PerformanceApplicationConfig application in config.Performance.Applications)
        {
            applications.Add(new PerformanceApplicationPolicy(
                application.ApplicationId,
                application.RtssProfileName,
                new PerformanceValues(application.FrameLimit, application.OverlayLevel)));
        }

        return new PerformancePolicy(
            new PerformanceValues(
                config.Performance.FrameLimit,
                config.Performance.OverlayLevel),
            applications,
            forceEnabled || config.Performance.Enabled);
    }

    private static Task PersistPerformancePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigStore.Mutate(config =>
        {
            config.Performance.Enabled = policy.Enabled;
            config.Performance.FrameLimit = policy.Global.FrameLimit;
            config.Performance.OverlayLevel = policy.Global.OverlayLevel;
            config.Performance.Applications.Clear();
            foreach (PerformanceApplicationPolicy application in policy.Applications)
            {
                config.Performance.Applications.Add(new PerformanceApplicationConfig
                {
                    ApplicationId = application.ApplicationId,
                    RtssProfileName = application.RtssProfileName,
                    FrameLimit = application.Values.FrameLimit,
                    OverlayLevel = application.Values.OverlayLevel,
                });
            }
        });
        return Task.CompletedTask;
    }

    private static Task PersistSimulatedPerformancePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static async Task ObserveDeviceConfigAsync(
        DeviceCoordinator coordinator,
        AppConfig config)
    {
        try
        {
            await coordinator.ApplyConfigAsync(config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Device cycle config apply failed", ex);
        }
    }

    private async Task LaunchAppsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot the token up front: KickTabBootSync (UI thread) disposes and
        // replaces the source, and reading .Token off the replaced instance later
        // throws ObjectDisposedException — which would abort the rest of this
        // sequence, including the Wi-Fi-indicator start below.
        var tabSyncToken = _tabBootSyncCancellation.Token;
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

    private static async Task TrimAfterBootSettlesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            MemoryTrim.TrimBestEffort("boot settled");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application teardown deliberately suppresses the post-boot trim.
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
    DesktopRestoreRequired,
}
