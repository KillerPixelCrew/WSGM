using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using SteamUiToolkit;
using SteamUiToolkit.Surfaces;
using WSGM.Core;
using WSGM.Install;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Settings;

namespace WSGM.Shell;

/// <summary>
///     Shell-mode orchestrator: starts startup apps and the home app, arms the
///     overlay (hotkey + edge swipes + home-exit), stays resident for the session.
/// </summary>
public sealed partial class ShellSession
{
    private readonly ApplicationPerformanceReconciler _applicationProfiles;
    private readonly bool _desktopResident;

    // The one owner of the display-off timeouts: the overlay's Power page and the rows WSGM adds to
    // Steam's Screensaver settings both edit through it, and it hears Steam's screensaver timeout.
    private readonly DisplayTimeouts _displayTimeouts = new();

    /// <summary>
    ///     The one owner of removable-library registration for this session, shared by the card
    ///     monitor, the overlay's eject panel and Steam's storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-lifetime and unconditional: an eject intent has to outlive the surface that made it
    ///     and the card-monitor configuration toggle, or turning card reconciliation off and on would
    ///     silently re-register a library the user ejected.
    /// </remarks>
    private readonly LibraryPolicy _libraryPolicy = new();

    private readonly bool _overlayTestOnly;
    private readonly PluginHost _pluginHost;
    private readonly ProfileService _profiles;
    private readonly bool _serviceBoot;
    private readonly bool _verboseLogging;
    private readonly CancellationTokenSource _shutdownCancellation = new();

    private SessionActivation? _activation;
    private AnimationService? _animations;

    /// <summary>The artwork browser behind Steam's Change Artwork page, or null in overlay-test.</summary>
    private SteamArtworkBrowserSource? _artwork;

    /// <summary>
    ///     The one audio manager for this session, shared by the quick access sheet's status pills and
    ///     Steam's audio namespace.
    /// </summary>
    /// <remarks>
    ///     Session-scoped because the sheet is not: it comes and goes, and Steam's audio store has to
    ///     answer for the whole session. A second manager would enumerate endpoints twice and could
    ///     disagree with the sheet about which device is default.
    /// </remarks>
    private AudioManager? _audio;

    /// <summary>Serializes profile and live advanced-format writes against the session audio manager.</summary>
    private AudioProfileService? _audioProfiles;

    private AutoTdpService? _autoTdp;
    private NativeQamBrightnessService? _brightness;
    private CardAcfWatcher? _cardAcfWatcher;
    private CardVolumeMonitor? _cardVolumes;
    private SteamGuideChordMirror? _chordMirror;
    private ControllerManager? _controllerStatusSource;
    private Action<ControllerManagerStatus>? _controllerStatusChanged;
    private Task _commonPluginStartup = Task.CompletedTask;
    private CommonPluginManager? _commonPlugins;

    // Replaced wholesale on every reload (see Reload) so this stays the same
    // instance the overlay, SessionModes and DisplayScale's saved-scale snapshot
    // live on — the volume OSD's UI-scale callback reads it long after boot.
    private AppConfig _config;
    private readonly ConfigStore _store;

    private ExplorerDesktopHost? _desktopHost;
    private bool _desktopRecoveryPending;
    private DesktopTray? _desktopTray;
    private DeviceCoordinator? _deviceCoordinator;
    private IDeviceOverlaySource? _deviceOverlay;
    private DisplayChangeWindow? _displayChangeWindow;

    // Field-rooted for the session lifetime: it owns a native power-setting
    // registration and the "did WSGM mute this?" flag.
    private DisplayOffMuteService? _displayMute;

    private bool _disposed;

    /// <summary>
    ///     The one removable-drive manager for this session, shared by the quick access sheet's eject
    ///     pill and Steam's revived storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-scoped for the same reason as the audio manager: Steam's storage service is asked
    ///     while the overlay is closed, when no sheet exists to own a manager. Two would
    ///     enumerate every volume twice and could disagree about what is still ejectable.
    /// </remarks>
    private RemovableDriveManager? _drives;

    private ForegroundWindowWatcher? _foregroundWindows;

    /// <summary>
    ///     The one format manager for this session, shared by the overlay's Format SD Card flow and
    ///     Steam's storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-scoped because a format outlives the surface that started it, which was already true
    ///     of the overlay's own: it kept one for the controller's lifetime so a completion reached with
    ///     the sheet closed still surfaced. Steam's pages make that matter twice over, since the format
    ///     can now be started from either side and both have to see the same run.
    /// </remarks>
    private SdFormatManager? _formats;

    /// <summary>The graphics packages' capability owner, created with the common plugin manager.</summary>
    private GpuCoordinator? _gpu;

    /// <summary>The overlay's Graphics destination source: the coordinator's, or the simulated one in overlay-test.</summary>
    private IGraphicsOverlaySource? _graphicsOverlay;

    // True for the direct game-mode boot; the desktop-resume paths clear it, and
    // DesktopModeStarting/GameModeEntered keep it current afterwards.
    private volatile bool _inGameMode = true;

    private KeepAwakeService? _keepAwake;
    private GameLibraryArtwork? _libraryArtwork;

    /// <summary>The Xbox library importer behind the Quick Access tab's page, or null in overlay-test.</summary>
    private GameLibraryService? _libraryImport;

    private MessageWindow? _messageWindow;
    private SessionModes? _modes;
    private SteamMonitor? _monitor;
    private OverlayController? _overlay;

    /// <summary>The rendering set that proves which foreground process is the game.</summary>
    private RtssFrametimeReader? _pairingFrametimes;

    private PerformanceService? _performance;
    private PerformanceOverlayBridge? _performanceOverlay;
    private CommonPluginOverlaySource? _pluginOverlaySource;
    private CommonPluginSteamUiSource? _pluginSteamUi;
    private ProfileFanOut? _profileFanOut;

    /// <summary>
    ///     The one radio manager for this session, shared by the quick access sheet's status pills and
    ///     Steam's network surface.
    /// </summary>
    /// <remarks>
    ///     Session-scoped for the same reason as the audio manager, and idle by default: scanning costs
    ///     power and only makes sense while a network list is on screen.
    /// </remarks>
    private RadioManager? _radios;

    private RefreshRatePairingService? _refreshPairing;
    private DisplayResolutionService? _resolutions;
    private RunningApplicationCoordinator? _runningApplicationTargets;
    private RunningApplicationMonitor? _runningApplications;
    private SettingsActivation? _settingsActivation;
    private volatile bool _shutdownRequested;
    private SoundPackService? _sounds;
    private BootSplash? _splash;
    private ModernStandbyGuard? _standbyGuard;
    private Task? _startupTask;
    private StartupAppWatcher? _startupWatcher;
    private bool _steamDeckTargetActive;

    /// <summary>The Graphics page in Steam, with its own projection over the coordinator.</summary>
    private SteamGraphicsService? _steamGraphics;

    // Steam's Switch to Desktop, which follows the mode. Null in overlay-test and before the Steam UI
    // host exists.
    private SteamPowerMenuBackend? _steamPowerMenu;

    /// <summary>Steam's revived storage pages over those two managers, or null in overlay-test.</summary>
    private SteamStorageBridge? _steamStorage;
    private Task _managerStartup = Task.CompletedTask;

    private SteamUiSessionHost? _steamUi;
    private PersistentSteamUiTransport? _steamUiTransport;
    private ThemeService? _themes;
    private TrayHost? _trayHost;
    private UpdateMonitor? _updates;
    private VolumeButtonService? _volumeButtons;
    private WsgmSteamSettingsService? _wsgmSettings;

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="store">The process-owned persistence and data roots.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">
    ///     Whether the logon service launched this process over a
    ///     live, still-initializing explorer (--boot) — enables the takeover flow.
    /// </param>
    /// <param name="desktopResident">Whether to remain on Desktop even while its logon shell is still starting.</param>
    /// <param name="verboseLogging">Whether the command line forces verbose logging for this run.</param>
    public ShellSession(
        AppConfig config,
        ConfigStore store,
        bool overlayTestOnly = false,
        bool serviceBoot = false,
        bool desktopResident = false,
        bool verboseLogging = false)
    {
        _config = config;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _verboseLogging = verboseLogging;
        _pluginHost = new PluginHost(UiThread.Post, new ApplicationPluginConfigurationStore(_store));
        // Overlay-test keeps profile edits in memory: it is a safe UI mode and must never rewrite the
        // user's configuration.
        _profiles = new ProfileService(config.Profiles,
            overlayTestOnly ? MutateSimulatedProfilesAsync() : MutateProfilesAsync);
        // Overlay-test must not write Windows power policy, so it gets no processor boost.
        _applicationProfiles = new ApplicationPerformanceReconciler(_profiles, () => _deviceCoordinator,
            () => _autoTdp, overlayTestOnly ? null : CpuBoost.Windows, () => _gpu);
        _cefMasterEnabled = config.Cef.Enabled;
        _wifiIndicatorEnabled = config.Cef is { Enabled: true, WifiIndicator: true };
        _downloadSortEnabled = config.Cef is { Enabled: true, DownloadQueueSort: true };
        _libraryBadgeEnabled = config.Cef is { Enabled: true, CardManager: true };
        _homeCarouselEnabled = config.Cef is { Enabled: true, ConnectedLibraryCarousel: true };
        _carouselShowUninstalled = config.Cef.CarouselShowUninstalled;
        _screensaverTimeoutsEnabled = config.Cef.Enabled;
        // The real shell opens the transport only through the readiness gate, once it is
        // running and knows whether Steam is cold-starting under it. Overlay-test never
        // attaches a transport and keeps the plain master flag so its static callers
        // report the configured state.
        SteamUiTransportSession.SetEnabled(overlayTestOnly && config.Cef.Enabled);
        SteamInputShim.SetEnabled(config.SteamInputManagementEnabled);
        _overlayTestOnly = overlayTestOnly;
        _serviceBoot = serviceBoot;
        _desktopResident = desktopResident;
        if (desktopResident)
        {
            SetInGameMode(false);
        }
    }

    /// <summary>Starts plugin admission off-thread, then creates shell and overlay services on the UI thread.</summary>
    /// <returns>The complete asynchronous session-start operation.</returns>
    public Task StartAsync()
    {
        _startupTask ??= StartUnderDeviceAdmissionAsync();
        return _startupTask;
    }

    private async Task StartUnderDeviceAdmissionAsync()
    {
        DeviceCoordinator? coordinator = null;
        var coordinatorAdopted = false;
        try
        {
            // Overlay test deliberately never discovers packages or loads plugin code.
            if (!_overlayTestOnly)
            {
                using var recoveryBudget = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
                recoveryBudget.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var fingerprint = GameModeReturnRecovery.PendingFingerprint(_store);
                    _desktopRecoveryPending = !await GameModeReturnRecovery.RestorePendingAsync(_store, recoveryBudget.Token)
                        .ConfigureAwait(false);
                    if (!_desktopRecoveryPending && ExplorerControl.IsDesktopShellRunning())
                    {
                        GameModeReturnRecovery.ClearRestored(_store, fingerprint);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _desktopRecoveryPending = true;
                    Log.Warn($"Recorded desktop recovery remains pending: {ex.Message}");
                }

                if (_desktopRecoveryPending && !ExplorerControl.IsDesktopShellRunning())
                {
                    try
                    {
                        _desktopHost ??= new ExplorerDesktopHost(_store.Context);
                        var restored = await _desktopHost.RestoreDesktopAsync(TimeSpan.FromSeconds(15))
                            .ConfigureAwait(false);
                        if (restored.Outcome is ExplorerDesktopOutcome.Failed)
                        {
                            Log.Warn("Explorer startup recovery remains unconfirmed.");
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Log.Warn($"Explorer recovery could not be verified: {ex.Message}");
                    }
                }

                // Installed packages only. WSGM bundles none, and the application directory is
                // user-writable, so scanning it would load plugin code from a path the installed
                // root is administrator-protected precisely to avoid.
                // Graphics packages publish capabilities through their own owner, created first so the
                // channel of every graphics package the manager starts has a router waiting for it.
                _gpu = new GpuCoordinator(UiThread.Post, _profiles, _pluginHost);
                // Variable refresh set on a graphics package's control is saved as the device's is.
                _gpu.AttachManualVariableRefreshOverride(_applicationProfiles.PersistManualVariableRefresh);
                _commonPlugins = new CommonPluginManager(_pluginHost, InstallLayout.Plugins,
                    Path.Combine(_store.Context.Root, "PluginState"), capabilityChannels: _gpu);
                _commonPluginStartup = ApplyCommonPluginConfigAsync(_config);
            }

            coordinator = _overlayTestOnly
                ? null
                : await DeviceCoordinator.TryStartAsync(
                    _config, _store,
                    _profiles,
                    _shutdownCancellation.Token).ConfigureAwait(false);
            if (_commonPluginStartup is not null)
            {
                await _commonPluginStartup.ConfigureAwait(false);
            }

            if (_shutdownRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
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
        // The session owns the process's one message window: it is built first, handed to every
        // consumer and disposed last on this thread (ShutdownAsync).
        _messageWindow = new MessageWindow();
        StartDeviceIntegration(_messageWindow);
        StartPerformance();
        StartSessionServices();
        StartOverlay();
        WireSessionEvents();

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
            () => DisplayScale.GetUiScalePercent(_config) / 100.0,
            _audio!);
        // Reads the flag on every look rather than capturing it, so a runtime config reload takes
        // effect without the guard being rebuilt; _config is replaced wholesale on reload.
        _standbyGuard = new ModernStandbyGuard(_messageWindow!, () => _config.ResuspendUnexplainedWakes);
        // Reads the flag on every wakeup, like the standby guard, so a config reload needs no rebuild.
        _updates = new UpdateMonitor(_store.Context, () => _config.CheckForUpdates);
        _displayMute = new DisplayOffMuteService(_messageWindow!);
        _displayMute.ApplyConfig(_config.MuteWhileDisplayOff);
        _displayMute.SetDownloadActive(_keepAwake.DownloadActive);
        if (_config is { MuteWhileDisplayOff: true, Cef.Enabled: false })
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
        BootManifestWriter.WriteCurrent(_store.Read(), _store.Context);

        // Once the user let Full mode turn the other handheld managers off, keep them off: Handheld
        // Companion's uninstaller re-enables the maker's services, and the Armoury Crate helper then
        // answers the Armoury Crate button with an install dialog. Off the boot path: it reads the task
        // scheduler and waits for windows to close.
        var managerStartupCancellation = _shutdownCancellation.Token;
        _managerStartup = Task.Run(() => OtherManagers.ReapplyAtStart(_store, managerStartupCancellation));
        Log.Observe(_managerStartup, "Other-manager startup reconciliation", true);
        if (UpdateFailure.Read() is { } updateFailure)
        {
            Log.Warn("Last in-app update: " + updateFailure);
        }

        // Service boot: the service launches WSGM at WTS_SESSION_LOGON — usually
        // BEFORE Winlogon has even started explorer (device-observed 2026-08-07:
        // gating this on a running Explorer made the takeover never run, leaving
        // explorer alive behind Big Picture next to our tray host). The takeover
        // owns every explorer state: its readiness poll waits for explorer to
        // appear AND finish logon prep, then shuts it down cleanly; if explorer
        // never shows within the 60 s cap it proceeds like a plain game-mode boot.
        if (_serviceBoot && !_desktopResident && !_desktopRecoveryPending)
        {
            StartBootTakeover();
            return;
        }

        if (_desktopRecoveryPending || _desktopResident || ExplorerControl.IsDesktopShellRunning())
        {
            // A live desktop at --shell start is either the sign-in start of a Desktop session,
            // the update restart (updates only run in desktop mode), or a manual start next to a
            // desktop. Resume in desktop mode — no splash, no startup apps, no game posture/scale
            // — with the overlay armed and Steam supplied; EnterGameMode brings the rest back.
            Log.Info("Shell started with a live desktop — resuming in desktop mode (overlay armed).");
            _desktopTray?.SetDesktop(true);
            // No DesktopModeStarting fires for a session that never entered game
            // mode, so clear the flag here: the game-mode-only CEF injections must
            // not start next to a live explorer (and nothing would retract them).
            SetInGameMode(false);
            _ = NotifyPluginModeAsync(PluginSessionMode.Desktop);
            // The third entry path the card services have to be started from. Game-mode boot
            // and the desktop-to-game transition both call this; a session that starts next to a
            // live desktop did not, and since DesktopModeStarting never fires for it either, the
            // volume monitor was simply absent: a card pulled from the reader left its library in
            // Steam's list, with Steam's storage page showing a drive that was not there (Claw,
            // 2026-09-11). The policy decides what runs on the desktop; this only asks it.
            ApplyCardServices(false);
            RequestSteamUiTransportGateCheck();
            // Desktop mode watches Steam like game mode does; only a transition or an explicit
            // Close Steam pauses it. That is what lets the session keep the client running.
            _monitor.Paused = false;
            WatchStartupAppsAndConfig();
            QueueDesktopActions(true);
            _bootWork = Task.Run(StartDesktopSteamAsync);
            return;
        }

        // Boot recomputes the posture value, so game mode re-applies it each start.
        // Posture first: it changes the display scale, and the splash sizes itself
        // to the final screen metrics.
        _modes.ApplyGameModePosture();
        EnterGameModeSurfaces();
        ShowBootSplashIfEnabled();
        WatchStartupAppsAndConfig();

        _bootWork = Task.Run(async () => { await RunLaunchSequenceAsync(); });
    }

    /// <summary>
    ///     Creates the device coordinator, AutoTDP and the Device overlay source, or the simulated source in overlay-test
    ///     mode.
    /// </summary>
    private void StartDeviceIntegration(MessageWindow messageWindow)
    {
        // The resident shell is the sole device-cycle authority. Overlay test deliberately never
        // creates this object, discovers packages, or loads plugin code.
        if (!_overlayTestOnly)
        {
            messageWindow.SessionEnding += OnSessionEnding;
            // The device cycle follows the session it belongs to. Without these the Claw's
            // controller, motion, OEM and suppressor services stayed live across a lock and a
            // system sleep, and the fresh cycle generation the resume contract requires was never
            // established afterwards.
            messageWindow.SessionLocked += OnSessionLocked;
            messageWindow.SessionUnlocked += OnSessionUnlocked;
            messageWindow.SystemSuspending += OnSystemSuspending;
            messageWindow.SystemResumed += OnSystemResumed;
            // Power preset assignments and per-source capability availability follow AC and battery.
            messageWindow.PowerSourceChanged += OnPowerSourceChanged;
            // A separate top-level window: WM_DISPLAYCHANGE is broadcast to top-level windows
            // only, so the message-only window above never hears a monitor appear.
            try
            {
                _displayChangeWindow = new DisplayChangeWindow();
            }
            catch (InvalidOperationException ex)
            {
                // The arrival wait falls back to polling, which is correct, just slower.
                Log.Warn("Display-change window unavailable: " + ex.Message);
            }

            if (_deviceCoordinator is not { } deviceCoordinator)
            {
                return;
            }

            _autoTdp = new AutoTdpService(
                new RtssFrametimeReader(),
                deviceCoordinator.Capabilities.Snapshot,
                (power, value, pair, token) =>
                    deviceCoordinator.ExecuteCapabilityAsync(
                        power.Descriptor.CapabilityId,
                        power.Descriptor.InstanceId,
                        value,
                        TimeSpan.FromSeconds(5),
                        CapabilityCommandOrigin.AutomaticControl,
                        power.Projection.State.CycleGeneration,
                        power.Projection.State.DescriptorGeneration, pair, token),
                TargetFrametimeMs,
                new AutoTdpTraceRecorder(
                    AutoTdpTraceRecorder.DefaultDirectory(_store.Context),
                    () => deviceCoordinator.InstalledPackage?.Manifest is { } manifest
                        ? (manifest.Id, manifest.Version)
                        : (null, null),
                    new AutoTdpTraceSystemContext()),
                () => _performance?.SampleSensors() ?? RtssOsdMetrics.Empty);
            var autoTdp = _autoTdp;
            autoTdp.SetTraceEnabled(_config.AutoTdpTraceEnabled);
            deviceCoordinator.AttachAutoTdpAvailability(() => autoTdp.Availability);
            deviceCoordinator.PowerPresets.AutomaticPowerOwner = () => autoTdp.OwnsPower;
            // A power limit the user set by hand pauses control permanently and is persisted to
            // whichever profile layer is in force, so it is restored on the next launch instead
            // of leaking onto the desktop. The hook is rooted here because this is where both
            // objects exist; every surface's power write already goes through the coordinator,
            // so this is the one place that sees all of them. Restore writes use the
            // ProfileRestore origin and never reach this funnel.
            deviceCoordinator.AttachAutoTdpManualOverride(watts =>
            {
                _autoTdp?.NoteManualChange(watts);
                _applicationProfiles.PersistManualPowerLimit(watts);
            }, watts => _autoTdp?.NoteManualChange(watts));

            // Variable refresh is stored the same way and for the same reason. Rooted on the
            // coordinator rather than on the one control that used to save it, because the
            // overlay's Device row reaches the capability directly and would otherwise apply a
            // state the profile never learned about.
            deviceCoordinator.AttachManualVariableRefreshOverride(_applicationProfiles.PersistManualVariableRefresh);

            _deviceOverlay = new DeviceOverlayBridge(deviceCoordinator, _autoTdp, _gpu);
        }
        else
        {
            _deviceOverlay = new SimulatedDeviceOverlaySource();
        }
    }

    /// <summary>
    ///     Creates the RTSS performance service, its overlay projection, refresh pairing and the running-application
    ///     target.
    /// </summary>
    [MemberNotNull(nameof(_performance))]
    private void StartPerformance()
    {
        _performance = new PerformanceService(
            _overlayTestOnly ? new SimulatedRtssAdapter() : new RtssNativeAdapter(),
            (field, value, token) => _profiles.SetAsync(field, value, token),
            _profiles.Current,
            PerformanceEnabled(_config));
        // Read through the field rather than captured, because the pairing service is created a
        // few lines below this and replaced on shutdown; the overlay slider then bookends exactly
        // where the Quick Access row does instead of running over RTSS's own 0-1000.
        _performanceOverlay = new PerformanceOverlayBridge(
            _performance,
            _profiles,
            () => _refreshPairing?.FrameLimitRange(),
            _applicationProfiles)
        {
            LivePublishers = LiveCapabilityPublishers
        };
        StartProfileFanOut();
        _performance.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(_config.Performance));
        AttachOsdPowerStatus();
        if (_overlayTestOnly)
        {
            // The per-application workflow must be inspectable in the safe UI mode: pretend one
            // Steam game is running and focused, so Device -> Profiles shows the application layer
            // instead of a permanently unavailable row. The real shell gets this target from
            // RunningApplicationCoordinator, which overlay-test deliberately never creates.
            _profiles.SetRunningApplication(new PerformanceApplicationTarget(
                "steam:480",
                480,
                "PreviewGame.exe"));
            return;
        }

        // Overlay-test runs without a real display to move, and pairing is the one performance
        // concern that changes hardware state rather than an RTSS profile.
        _refreshPairing = new RefreshRatePairingService();
        _resolutions = new DisplayResolutionService();
        _refreshPairing.SetStrategy(_config.Performance.FrameLimitStrategy);
        _performance.StateChanged += OnPerformanceStateForPairing;
        _autoTdp?.Apply(ShouldRunAutoTdp(_config.DeviceIntegration));

        _steamUiTransport = new PersistentSteamUiTransport(true);
        // Decide the gate BEFORE attaching: Attach copies the session flag into the
        // transport, and an open transport with a subscriber starts discovering
        // Steam's port at once.
        ApplySteamUiTransportGate();
        SteamUiTransportSession.Attach(_steamUiTransport);
        _transportGateWork = Task.Run(() =>
            RunSteamUiTransportGateAsync(_shutdownCancellation.Token));
        // Its own reader rather than AutoTDP's: RtssFrametimeReader is not thread-safe, and
        // these two poll on different threads at different cadences. The mapping is read-only,
        // so a second view of it costs a handle and nothing else.
        _pairingFrametimes = new RtssFrametimeReader();
        _runningApplications = new RunningApplicationMonitor(
            new SteamRunningAppsProbe(_steamUiTransport),
            _config.Cef.Enabled,
            _pairingFrametimes.ReadLive);
        _runningApplications.Start();

        // The second identity source. It feeds the same monitor rather than driving policy on
        // its own, so per-application settings also work on the desktop and for titles Steam
        // never launched — which is the only way the overlay's per-game rows mean anything
        // outside a Steam game.
        _foregroundWindows = new ForegroundWindowWatcher();
        _foregroundWindows.ApplicationChanged += OnForegroundApplicationChanged;
        _runningApplicationTargets = new RunningApplicationCoordinator(
            _runningApplications,
            (target, _) =>
            {
                // The profile store decides what runs and which layer is in force; its change drives
                // RTSS, the device, power and refresh through the fan-out.
                _profiles.SetRunningApplication(target);
                return Task.CompletedTask;
            },
            _deviceCoordinator is null
                ? null
                : ApplyRunningApplicationTargetAsync);
    }

    /// <summary>
    ///     Creates the Steam monitor, session modes, keep-awake and the audio, radio and storage services Steam's
    ///     surfaces use.
    /// </summary>
    [MemberNotNull(nameof(_keepAwake), nameof(_modes), nameof(_monitor))]
    private void StartSessionServices()
    {
        _monitor = new SteamMonitor();
        if (!_overlayTestOnly)
        {
            _desktopHost ??= new ExplorerDesktopHost(_store.Context);
        }

        _modes = _desktopHost is null
            ? new SessionModes(_config, _monitor)
            : new SessionModes(_config, _monitor, _desktopHost, _store);
        if (!_overlayTestOnly)
        {
            _modes.GameModeEntryServices = new ShellGameModeEntryServices(this);
            _modes.DesktopReady = () => _splash?.Dismiss("desktop restored");
            _modes.IsGameMode = () => _inGameMode;
            // Settings opened from the tray or the overlay runs in this process, so its action
            // lists can offer what is actually running. A standalone --settings sees nothing here
            // and shows saved steps read-only, which is the truthful rendering.
            SettingsPluginActions.Publish(ReadPluginActionOptions);
            _modes.GameModeEntrySettled = () => Dispatcher.UIThread.Post(() =>
            {
                _holdingEntrySplash = false;
                _gameModeEntryActive = false;
                if (!_inGameMode)
                {
                    _splash?.Dismiss("desktop entry settled");
                }
            });
        }

        _modes.SteamStartFailed += _ => Dispatcher.UIThread.Post(() =>
            _splash?.Dismiss("session transition warning"));
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
        // Started here rather than by the sheet, because Steam's audio namespace has to answer
        // while the sheet is closed. Overlay-test lets the sheet's status pills own their own
        // managers, since no Steam surface exists there to serve.
        if (_overlayTestOnly)
        {
            return;
        }

        _audio = new AudioManager();
        _audio.Start();
        _audioProfiles = new AudioProfileService(_audio);
        // Follows the managed controller target: it only has work while a Steam Deck type target
        // exists, and it puts Valve's file back when that target goes.
        _chordMirror = SteamGuideChordMirror.ForInstalledSteam();

        // Not started here: scanning is expensive and belongs to whichever surface is showing a
        // network list. The manager exists for the whole session so Steam's Internet page can
        // drive it, but it stays idle until something asks.
        _radios = new RadioManager();

        // Started here rather than by the sheet, for the same reason as audio: Steam's
        // storage pages ask what is ejectable while the overlay is closed, and an unstarted
        // manager would answer "nothing" to someone holding a card.
        _drives = new RemovableDriveManager(_messageWindow);

        // Every eject surface reaches the manager, so the policy hangs here rather than at
        // each call site: the overlay's panel and Steam's storage page then mean the same
        // thing by an eject without either knowing about the other.
        _drives.EjectObserver = _libraryPolicy;
        _drives.CardWatcher = _cardAcfWatcher;
        _drives.Start();
        _formats = new SdFormatManager(_store, _shutdownCancellation.Token) { CardWatcher = _cardAcfWatcher };

        // Over the same two managers the overlay's storage flows use. Steam's revived pages are
        // a second surface on one backend, not a second implementation. The format switch is
        // read through the session's live config, so switching it in Settings takes effect on
        // the next press rather than the next session.
        _steamStorage = new SteamStorageBridge(
            _drives, _formats, () => _config.SteamStorageFormatEnabled, _libraryPolicy);

        // Artwork reads its providers from the session's live config, so a key entered in Settings
        // applies to the next search rather than the next session.
        _artwork = new SteamArtworkBrowserSource(() => _config.Artwork, new ArtworkStateStore(_store.Context.Root));

        // The Steam themes: CSSLoader-compatible themes from DeckThemes, kept in WSGM's own folder
        // and published into every Big Picture window through the toolkit. Reads the session's live
        // config and writes its own fields one at a time through the store.
        _themes = new ThemeService(
            new ThemeLoader(ThemePaths.DefaultRoot(_store.Context)),
            new ThemeStoreClient(),
            () => _config.Themes,
            change => CommitWsgmSetting(config => change(config.Themes), false),
            () => Steam.InstallDirectory);
        _themes.Start();

        _sounds = new SoundPackService(new SoundPackLibrary(SoundPackLibrary.DefaultRoot(_store.Context)),
            () => _config.Sounds.Selected,
            id => CommitWsgmSetting(config => config.Sounds.Selected = id, false),
            () => Steam.InstallDirectory);
        _ = _sounds.RefreshAsync(CancellationToken.None);
        if (!_config.Cef.Enabled)
        {
            _sounds.SetIntegrationStatus("Steam integration is off. The sound-pack selection is saved.");
        }

        // The boot movie: SteamDeckRepo's boot movies and the user's own in WSGM's library, the
        // chosen one copied to the file Steam's client asks for. Started before Steam so a shuffle on start
        // is what Steam reads.
        _animations = new AnimationService(
            new AnimationLibrary(AnimationLibrary.DefaultRoot(_store.Context)),
            new AnimationRepoClient(),
            () => _config.Animations,
            change => CommitWsgmSetting(config => change(config.Animations), false),
            () => Steam.InstallDirectory,
            steamRunning: () => _monitor?.IsAlive ?? true,
            steamChoice: new SteamStartupMovieAccess(
                token => SteamStartupMovie.SetAsideAsync(cancellationToken: token),
                (choice, token) => SteamStartupMovie.RestoreAsync(choice, cancellationToken: token),
                (operation, attempt, token) => _config.Cef.Enabled
                    ? SteamUiReadiness.RunWhenReadyAsync(operation, attempt, token)
                    : Task.FromResult(false)));
        _animations.Start();

        // WSGM's own settings, from its row in Steam's main menu. Reads the session's live config and
        // writes one field at a time through the store; the config reload then applies it. Plugins
        // are read through the same source the Quick Access tab uses, created with the Steam host.
        _wsgmSettings = new WsgmSteamSettingsService(
            () => _config,
            CommitWsgmSetting,
            config => SteamInputManagement.Apply(config, "steam-settings"),
            () => SteamInputShim.LastStatus,
            () =>
            [
                .. (_commonPlugins?.Catalog.Common ?? []).Select(package =>
                    new InstalledCommonPlugin(package.Manifest.Id, package.Manifest.Name,
                        _commonPlugins?.EnabledByDefault(package.Manifest) == true))
            ],
            () => _pluginSteamUi?.ReadSettings() ?? [],
            (id, key, value, revision, token) => _pluginSteamUi is { } source
                ? source.ConfigureAsync(id, key, value, revision, token)
                : Task.FromResult(new SteamUiCommandResult(false, "Plugins are not available.")),
            PluginPackageCatalog.InstalledDevicePluginId);

        ReleaseAbandonedPackageExemptions();

        // The Game Library talks to the same running Steam client everything else here does, and reads
        // each launcher's own files. Every seam is injected so the discovery and planning rules stay
        // testable without a live Steam or a real launcher.
        StoreCatalogClient catalog = new();
        GameLibraryArtwork libraryArtwork = new(new ArtworkSearchProviders(() => _config.Artwork));
        _libraryImport = new GameLibraryService(
            [
                new XboxLibrarySource(
                    XboxPackages.Enumerate,
                    XboxPackages.ReadPackageFile,
                    (package, token) => catalog.LookUpAsync(package.FamilyName, token)),
                new EpicLibrarySource(),
                new GogLibrarySource(),
                new UbisoftLibrarySource(),
                new BattleNetLibrarySource(),
                new ItchLibrarySource(),
                new AmazonLibrarySource(),
                new PrismLauncherSource(),
                new AtLauncherSource()
            ],
            UninstallEntries.Read,
            new ImportStateStore(_store.Context),
            () => new SteamShortcutWriter(
                AddShortcutAsync,
                async (appId, fields, token) =>
                    (await SteamApps.SetShortcutLaunchAsync(
                            appId, fields.Target, fields.StartDirectory, fields.LaunchOptions, token)
                        .ConfigureAwait(false)).Succeeded,
                async (appId, token) =>
                    (await SteamApps.RemoveShortcutAsync(appId, token).ConfigureAwait(false)).Succeeded),
            ReadShortcutsAsync,
            ReadShortcutAsync,
            _config.GameLibrary,
            (appId, images, token) => SteamArtwork.ApplyManyFromUrlsAsync(appId, images, _config.Artwork, token),
            (id, name, target, removeEmptyProfile, token) => _profiles is null
                ? Task.FromResult(false)
                : _profiles.SetApplicationControllerTargetAsync(id, name, target, removeEmptyProfile, token),
            openArtwork: _artwork.OpenAsync,
            controllerManaged: () => _config.DeviceIntegration is { Enabled: true, ControllerManagementEnabled: true },
            updateSettings: change => CommitWsgmSetting(config => change(config.GameLibrary), false).GameLibrary,
            folderSource: folder => new ShortcutFolderSource(folder),
            artwork: libraryArtwork,
            syncCollection: (id, name, add, remove, token) =>
                SteamCollections.SyncAsync(id, name, add, remove, true, cancellationToken: token));
        _libraryArtwork = libraryArtwork;
        _libraryImport.Start();
    }

    /// <summary>Creates the overlay controller with its sources and routes managed controller input to WSGM's own surfaces.</summary>
    [MemberNotNull(nameof(_overlay))]
    private void StartOverlay()
    {
        // StartSessionServices runs first and sets these.
        // ReSharper disable once InvocationIsSkipped
        Debug.Assert(_modes is not null);
        if (!_overlayTestOnly)
        {
            _brightness = new NativeQamBrightnessService(() => !_shutdownRequested);
        }

        // Graphics follows the graphics packages, not the device integration switch. Overlay-test loads no
        // plugin, so it gets an in-memory publication to try the destination with.
        _graphicsOverlay = _gpu is { } gpu
            ? new GraphicsOverlayBridge(gpu)
            : _overlayTestOnly
                ? new SimulatedGraphicsOverlaySource()
                : null;

        _overlay = new OverlayController(
            _config,
            _store,
            _monitor,
            _modes,
            _keepAwake,
            _overlayTestOnly,
            new OverlaySources(
                _deviceOverlay,
                _performanceOverlay,
                _pluginOverlaySource = _commonPlugins is null && _deviceCoordinator is null
                    ? null
                    : new CommonPluginOverlaySource(_store, _commonPlugins, _pluginHost, _config.PluginWidgetPins,
                        _deviceCoordinator is not null && _deviceOverlay is not null
                            ? new DeviceWidgetSource(_deviceCoordinator, _deviceOverlay)
                            : null),
                _overlayTestOnly
                    ? null
                    : new DevicePrerequisiteSource(
                        ReadDevicePrerequisiteState, EnableDeviceIntegrationAsync),
                _brightness,
                _deviceCoordinator,
                _libraryImport,
                _themes,
                _animations,
                _graphicsOverlay,
                _sounds,
                _artwork),
            _audio,
            _audioProfiles,
            _radios,
            _deviceCoordinator?.PowerPresets,
            _deviceCoordinator?.PowerAssignments,
            _drives,
            _formats,
            _displayTimeouts,
            _messageWindow);
        _overlay.ShowOnScreenKeyboard = ShowOnScreenKeyboardAsync;
        if (!_overlayTestOnly)
        {
            _overlay.GameReturn = new GameWindowReturn(async (processId, token) =>
                    _config.Cef.Enabled && _steamUiTransport is { } transport
                                        && await SteamGameWindowActivation.RaiseAsync(transport, processId, token),
                _shutdownCancellation.Token);
        }

        if (!_overlayTestOnly)
        {
            _desktopTray = new DesktopTray(
                _store,
                () =>
                {
                    if (!_shutdownRequested)
                    {
                        _overlay.ShowOverlay();
                    }
                },
                () =>
                {
                    if (!_shutdownRequested)
                    {
                        _modes.EnterGameMode();
                    }
                },
                () =>
                {
                    ApplicationShutdownRequest.Request(ApplicationShutdownReason.Normal);
                    _ = ((App)Application.Current!).Runtime.RequestExit();
                });
            _activation = new SessionActivation(() =>
            {
                if (!_shutdownRequested)
                {
                    _overlay.ShowOverlay();
                }
            });
            _settingsActivation = new SettingsActivation(() =>
            {
                if (!_shutdownRequested)
                {
                    _desktopTray?.OpenSettings();
                }
            });
        }

        // The sheet is recreated per open, so its one-time cost — compiled-XAML populate JIT for
        // the process's largest window — lands on the user's first swipe (~1.5 s on the Claw).
        // Pay it at idle instead; every later open constructs against warm code.
        Dispatcher.UIThread.Post(
            _overlay.WarmUp,
            DispatcherPriority.ApplicationIdle);

        if (_deviceCoordinator is { } controllerCapture && _overlay is { } captureSurface)
        {
            captureSurface.UiSurfaceOpened += surfaceId =>
                _ = ObserveUiCaptureClaimAsync(controllerCapture, surfaceId);
            captureSurface.UiSurfaceClosed += controllerCapture.ReleaseUi;
        }

        if (_deviceCoordinator is not { } controllers || _overlay is not { } overlay)
        {
            return;
        }

        // WSGM's own navigation reads the managed controller while management is active and SDL
        // otherwise; the overlay's gamepad poll switches by itself.
        overlay.UseManagedPad(controllers.Controllers.UiPad);
        if (_controllerStatusSource is not null && _controllerStatusChanged is not null)
        {
            _controllerStatusSource.StatusChanged -= _controllerStatusChanged;
        }

        _controllerStatusSource = controllers.Controllers;
        _controllerStatusChanged = status =>
        {
            if (_disposed)
            {
                return;
            }

            // The guide chord mirror follows the target: Steam only reloads its chord template
            // for a Steam Deck type controller, and the mirror restores Valve's file otherwise.
            _steamDeckTargetActive = status is
            {
                State: ControllerManagementState.Active,
                Target: ManagedControllerTarget.SteamDeckComposite
            };
            _chordMirror?.Apply(_config.DeviceIntegration.KeepGuideChordEdits, _steamDeckTargetActive);
        };
        _controllerStatusSource.StatusChanged += _controllerStatusChanged;
    }

    /// <summary>Connects OEM actions, the card badge and the Steam monitor's lifecycle events to the session.</summary>
    private void WireSessionEvents()
    {
        // The earlier setup steps set these before the session events are wired.
        // ReSharper disable once InvocationIsSkipped
        Debug.Assert(_monitor is not null && _modes is not null && _performance is not null && _overlay is not null);
        _deviceCoordinator?.ConfigureOemActions(new DeviceOemActionServices
        {
            ToggleOverlayAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOverlay();
                return _overlay is not null;
            }, cancellationToken),
            ToggleSteamQuickAccessAsync = token =>
                ToggleSteamSurfaceAsync(SteamNativeSurfaceAction.QuickAccess, token),
            ToggleSteamOverlayAsync = token => ToggleSteamSurfaceAsync(SteamNativeSurfaceAction.Home, token),
            ToggleDevicePageAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ShowDevicePage();
                return _overlay is not null;
            }, cancellationToken),
            ToggleOpenAppsAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOpenApps();
                return _overlay is not null;
            }, cancellationToken),
            ToggleDesktopGameModeAsync = cancellationToken => RunUiActionAsync(() =>
            {
                if (_modes is null)
                {
                    return false;
                }

                if (ExplorerControl.IsDesktopShellRunning())
                {
                    _modes.EnterGameMode();
                }
                else
                {
                    _modes.EnterDesktopMode();
                }

                return true;
            }, cancellationToken),
            ToggleOnScreenKeyboardAsync = ShowOnScreenKeyboardAsync,
            CyclePerformanceProfileAsync = CyclePerformanceProfileAsync,
            CyclePerformanceOverlayLevelAsync = CyclePerformanceOverlayLevelAsync,
            SetRearButtonAsync = (button, cancellationToken) =>
                _deviceCoordinator?.PulseRearButtonAsync(button, cancellationToken)
                ?? Task.FromResult(false)
        });
        if (!_overlayTestOnly)
        {
            // The badge's first reading, before any library-tab sync: the host publishes whatever
            // reading exists when the bridge comes up, and a null one publishes nothing, which
            // named every card game internal until a sync happened to run (Claw, 2026-09-11).
            try
            {
                LibraryBadges.Update(_config, LibraryTabManager.PresentCardContentIds());
            }
            catch (Exception ex)
            {
                Log.Warn($"Library badge: initial reading failed: {ex.Message}");
            }

            _steamUi = new SteamUiSessionHost(
                _store,
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
                _radios,
                // Null in overlay-test, where there is no real display to move. The patch is then
                // never registered, so the row cannot appear offering a control with nothing behind
                // it.
                _overlayTestOnly ? null : _resolutions,
                _autoTdp,
                ReadNativeQamPerfSupport,
                ApplyManualRefreshRate,
                // Null when nothing can publish VRR, which is also when the projection omits
                // is_vrr_supported and Valve's row does not render. One fact, one source. The
                // user-facing wrapper persists the state to the per-application layer in force; the
                // bare ApplyVariableRefreshRateAsync stays the profile restore's write. A graphics
                // package publishes it whether or not device integration runs.
                _deviceCoordinator is null && _gpu is null
                    ? null
                    : _applicationProfiles.SetVariableRefreshRateFromUserAsync,
                () => _overlay?.ShowBluetoothPanel() == true,
                _brightness,
                _steamStorage,
                _overlayTestOnly ? null : _displayTimeouts,
                _audioProfiles,
                _pluginSteamUi = _commonPlugins is null
                    ? null
                    : new CommonPluginSteamUiSource(_commonPlugins, _pluginHost),
                _profiles,
                // Null in overlay-test, which has no Steam client to read artwork for or write it to.
                _artwork,
                _libraryImport,
                _wsgmSettings,
                _applicationProfiles.CpuBoostAvailable
                    ? new NativeQamCpuBoostService(_applicationProfiles, _profiles)
                    : null,
                _chordMirror,
                // Null in overlay-test, which has no mode switch to run.
                _steamPowerMenu = _overlayTestOnly
                    ? null
                    : new SteamPowerMenuBackend(_inGameMode, SwitchToDesktopFromSteamAsync),
                _themes,
                _animations,
                _steamGraphics = _gpu is { } gpu ? new SteamGraphicsService(new GraphicsOverlayBridge(gpu)) : null,
                _sounds);
            if (_pluginSteamUi is not null && _wsgmSettings is { } wsgmSettings)
            {
                // A plugin starting, stopping or taking a setting changes the Plugins page.
                _pluginSteamUi.Changed += wsgmSettings.Refresh;
            }

            _steamUi.Apply(_config.Cef is { Enabled: true, NativeQuickAccess: true });
            _steamUi.ApplyHostSteamUi(_config.Cef.Enabled);
            _steamUi.ApplySurfaceObservation(_config.Cef.Enabled);
            _steamUi.ApplyNetworkIndicator(_wifiIndicatorEnabled);
            ApplySteamUiSurfacePreferences();
            ApplyGlyphConfig(_config);
            if (_deviceCoordinator is not null)
            {
                // Two sources change the active profile: the package publishing its profiles, and
                // the user changing the selection mode. Both land on the same apply.
                _deviceCoordinator.PhysicalGlyphCatalog.Changed += OnPhysicalGlyphProfilesChanged;
            }
        }

        // The tray host must never coexist with explorer's taskbar (Z-order war
        // over FindWindow, see TrayHost): gone before explorer starts, back
        // once game mode has closed it. Apps re-home their icons on each side's
        // TaskbarCreated broadcast.
        _modes.DesktopModeStarting += () =>
        {
            // Retire the actual shell window before optional plugin/card/UI notifications.
            if (_trayHost is not null && !_trayHost.Retire())
            {
                throw new InvalidOperationException("The tray host's native window is still active.");
            }

            _trayHost = null;
            _overlay?.AttachTrayHost(null);
            SetInGameMode(false);
            _desktopTray?.SetDesktop(true);
            _ = NotifyPluginModeAsync(PluginSessionMode.Desktop);
            RequestSteamUiTransportGateCheck();
            _tabBootSyncCancellation.Cancel();
            // Tabs and the badge are game-mode surfaces; the ACF watcher only exists
            // to keep them fresh, so it stands down with them.
            ApplyCardServices(false);
            // The tabs themselves were retracted before Big Picture was asked to close
            // (PrepareSteamUiForDesktopAsync), and the transport stays held until the return
            // settles. The Wi-Fi indicator and download sort stay: Big Picture on the desktop draws
            // the same header and the same download queue (Claw, 2026-09-11).
            _volumeButtons?.SetGameModeActive(false);
        };
        _modes.PrepareSteamUiForBigPictureAsync = PrepareSteamUiForBigPictureAsync;
        _modes.PrepareSteamUiForDesktopAsync = PrepareSteamUiForDesktopAsync;
        _modes.SteamUiBigPictureRequestSettled = () =>
            Dispatcher.UIThread.Post(ReleaseSteamUiBigPictureHold);
        _modes.GameModeEntered += () =>
        {
            SetInGameMode(true);
            _desktopTray?.SetDesktop(false);
            ReleaseSteamUiBigPictureHold();
            RequestSteamUiTransportGateCheck();
            EnterGameModeSurfaces();
            _steamUi?.ApplyNetworkIndicator(_wifiIndicatorEnabled);
            ApplySteamUiSurfacePreferences();
            // Returning from desktop mode disabled tabs/badge and cancelled the boot
            // sync; re-inject them.
            KickTabBootSync();
        };
        // A fresh Steam start while WSGM keeps running (client update, crash restart)
        // wipes the injected tabs and the resident badge with the old CEF session —
        // re-inject once the new UI is up.
        _monitor.SteamStarted += () =>
        {
            RequestSteamUiTransportGateCheck();
            // Steam rebuilds its registrations on restart in both desktop and game mode.
            _cardVolumes?.Kick("Steam started");
            if (!_inGameMode)
            {
                return;
            }

            KickTabBootSync();
        };
        // Steam leaving in game mode closes the transport gate at once, so a restart's
        // fresh, still-headless CEF session cannot be connected before its own Big
        // Picture window exists.
        _monitor.SteamExited += RequestSteamUiTransportGateCheck;
        _monitor.ClientStarted += () => _animations?.SteamStarted();
    }

    /// <summary>
    ///     Marshals the shared poller's download transition onto the UI thread,
    ///     where the display mute service and its timers are owned.
    /// </summary>
    /// <param name="active">Whether Steam reports an active download.</param>
    private void OnDownloadActivityChanged(bool active)
    {
        Dispatcher.UIThread.Post(() => _displayMute?.SetDownloadActive(active));
    }
}
