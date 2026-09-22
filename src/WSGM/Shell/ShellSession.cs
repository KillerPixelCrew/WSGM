using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit.Surfaces;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Settings;

namespace WSGM.Shell;

/// <summary>
///     Shell-mode orchestrator: starts startup apps and the home app, arms the
///     overlay (hotkey + edge swipes + home-exit), stays resident for the session.
/// </summary>
public sealed class ShellSession : IAsyncDisposable
{
    private readonly ApplicationPerformanceReconciler _applicationProfiles;

    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering.
    private readonly SemaphoreSlim _cefMasterGate = new(1, 1);
    private readonly Lock _configDebounceGate = new();
    private readonly DesktopActionAdmission _desktopActionAdmission = new();
    private readonly bool _desktopResident;
    private readonly Lock _devicePowerGate = new();

    private readonly SemaphoreSlim _displayActionGate = new(1, 1);

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
    private readonly PluginHost _pluginHost = new(UiThread.Post);
    private readonly ProfileService _profiles;
    private readonly bool _serviceBoot;

    private readonly CancellationTokenSource _shutdownCancellation = new();

    // The transport's enabled flag is the one choke point every automatic CEF touch
    // passes: the patch host, the running-application probe and the static
    // evaluators. Its open/closed state is decided only by the readiness loop
    // (see SteamUiReadiness.TransportShouldBeOpen) and always under _cefMasterGate,
    // so a mode change or Steam lifecycle edge merely signals a re-check instead of
    // flipping the transport underneath a retract-then-close in flight.
    private readonly SemaphoreSlim _transportGateSignal = new(0);
    private SessionActivation? _activation;

    // Whether WSGM's per-application feature is the reason the device currently holds a power limit,
    // so an application transition knows whether it has a limit of its own to take back. Written and
    // read only from the running-application transition path and the manual funnels, all of which the
    // running-application coordinator serialises. See PerApplicationPowerPolicy / PerApplicationVrrPolicy.
    // The application identity the power limit was last reconciled for. The running-application
    // snapshot bumps on foreground-executable enrichment as well as on a real application change, so
    // this reconciles the power limit only when the identity itself changes — a limit does not move
    // because focus flicked to a launcher and back, and re-writing the EC on every poll would thrash
    // it. The sentinel differs from every real id and from the null-application empty string, so the
    // first transition always reconciles. Touched only on the serialised transition path.
    /// <summary>
    ///     The one audio manager for this session, shared by the taskbar's status cluster and Steam's
    ///     audio namespace.
    /// </summary>
    /// <remarks>
    ///     Session-scoped because the taskbar is not: it comes and goes, and Steam's audio store has to
    ///     answer for the whole session. A second manager would enumerate endpoints twice and could
    ///     disagree with the taskbar about which device is default.
    /// </remarks>
    private AudioManager? _audio;

    /// <summary>Serializes profile and live advanced-format writes against the session audio manager.</summary>
    private AudioProfileService? _audioProfiles;

    private AutoTdpService? _autoTdp;

    // Non-null from the moment the service-boot splash becomes interactive until
    // the worker releases SessionModes' transition gate. The splash's desktop
    // recovery cancels through this owner instead of racing that gate.
    private BootTakeoverCancellation? _bootTakeover;
    private Task? _bootWork;
    private NativeQamBrightnessService? _brightness;
    private CardAcfWatcher? _cardAcfWatcher;
    private CardVolumeMonitor? _cardVolumes;

    private bool _carouselShowUninstalled;

    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;
    private Task _commonPluginStartup = Task.CompletedTask;

    private CommonPluginManager? _commonPlugins;

    // Replaced wholesale on every reload (see Reload) so this stays the same
    // instance the overlay, SessionModes and DisplayScale's saved-scale snapshot
    // live on — the volume OSD's UI-scale callback reads it long after boot.
    private AppConfig _config;
    private Timer? _configDebounce;

    private long _configReloadGeneration;

    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private FileSystemWatcher? _configWatcher;
    private ExplorerDesktopHost? _desktopHost;
    private DesktopTray? _desktopTray;
    private DeviceCoordinator? _deviceCoordinator;
    private IDeviceOverlaySource? _deviceOverlay;
    private long _devicePowerRequestGeneration;
    private Task _devicePowerWork = Task.CompletedTask;
    private bool _deviceSuspended;

    private DisplayChangeWindow? _displayChangeWindow;

    // Field-rooted for the session lifetime: it owns a native power-setting
    // registration and the "did WSGM mute this?" flag.
    private DisplayOffMuteService? _displayMute;

    private bool _disposed;

    // Same for the injected download-queue sort buttons. The session host owns
    // their target generation and retries through the common patch registry.
    private bool _downloadSortEnabled;

    /// <summary>
    ///     The one removable-drive manager for this session, shared by the taskbar's eject tile and
    ///     Steam's revived storage pages.
    /// </summary>
    /// <remarks>
    ///     Session-scoped for the same reason as the audio manager: Steam's storage service is asked
    ///     while the overlay is closed, and the taskbar's manager dies with the taskbar. Two would
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

    // True from just before a transition asks Steam for Big Picture until that transition
    // settles (PrepareSteamUiForBigPictureAsync / ReleaseSteamUiBigPictureHold). The request
    // rebuilds Steam's front-end, so the transport hold must begin before it fires.
    private volatile bool _gameModeCefTransitionPending;
    private bool _gameModeEntryActive;
    private bool _holdingEntrySplash;

    private bool _homeCarouselEnabled;

    // True for the direct game-mode boot; the desktop-resume paths clear it, and
    // DesktopModeStarting/GameModeEntered keep it current afterwards.
    private volatile bool _inGameMode = true;
    private KeepAwakeService? _keepAwake;
    private bool _libraryBadgeEnabled;
    private MessageWindow? _messageWindow;
    private SessionModes? _modes;
    private SteamMonitor? _monitor;
    private OverlayController? _overlay;
    private int _pairedFrameLimit = -1;

    /// <summary>The rendering set that proves which foreground process is the game.</summary>
    private RtssFrametimeReader? _pairingFrametimes;

    private bool? _pendingDeviceSuspended;
    private DisplayLayout? _pendingReturnLayout;
    private PerformanceService? _performance;
    private PerformanceOverlayBridge? _performanceOverlay;
    private CommonPluginOverlaySource? _pluginOverlaySource;
    private ProfileFanOut? _profileFanOut;

    /// <summary>
    ///     The one radio manager for this session, shared by the taskbar's status cluster and Steam's
    ///     network surface.
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
    private bool _screensaverTimeoutsEnabled;
    private SettingsActivation? _settingsActivation;
    private volatile bool _shutdownRequested;
    private BootSplash? _splash;
    private ModernStandbyGuard? _standbyGuard;
    private Task? _startupTask;
    private StartupAppWatcher? _startupWatcher;
    private SteamControllerHandoff? _steamControllerHandoff;
    private SteamControllerOwnershipAdapter? _steamControllerOwnership;

    /// <summary>Steam's revived storage pages over those two managers, or null in overlay-test.</summary>
    private SteamStorageBridge? _steamStorage;

    /// <summary>The artwork browser behind Steam's Change Artwork page, or null in overlay-test.</summary>
    private SteamArtworkBrowserSource? _artwork;

    /// <summary>The Xbox library importer behind the Quick Access tab's page, or null in overlay-test.</summary>
    private SteamLibraryImportSource? _libraryImport;

    private SteamUiSessionHost? _steamUi;

    private PersistentSteamUiTransport? _steamUiTransport;

    // Replaced (not just cancelled) on every game-mode entry: a single cancelled
    // source would permanently kill boot syncing after the first desktop trip.
    private CancellationTokenSource _tabBootSyncCancellation = new();
    private bool _tookOverFromExplorer;
    private Task? _transportGateWork;
    private TrayHost? _trayHost;

    private VolumeButtonService? _volumeButtons;

    // Live Wi-Fi-indicator gate: the applied state, so a reload can tell an
    // on->off transition from a repeat of the same value.
    private bool _wifiIndicatorEnabled;

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">
    ///     Whether the logon service launched this process over a
    ///     live, still-initializing explorer (--boot) — enables the takeover flow.
    /// </param>
    /// <param name="desktopResident">Whether to remain on Desktop even while its logon shell is still starting.</param>
    public ShellSession(
        AppConfig config,
        bool overlayTestOnly = false,
        bool serviceBoot = false,
        bool desktopResident = false)
    {
        _config = config;
        // Overlay-test keeps profile edits in memory: it is a safe UI mode and must never rewrite the
        // user's configuration.
        _profiles = new ProfileService(config.Profiles,
            overlayTestOnly ? MutateSimulatedProfilesAsync() : MutateProfilesAsync);
        _applicationProfiles = new ApplicationPerformanceReconciler(_profiles, () => _deviceCoordinator,
            () => _autoTdp);
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
            _inGameMode = false;
        }
    }

    /// <summary>Runs bounded device cleanup before the application lifetime ends.</summary>
    public ValueTask DisposeAsync()
    {
        return ShutdownAsync(
            ApplicationShutdownReason.Normal,
            DateTimeOffset.UtcNow.Add(ApplicationShutdownCoordinator.BudgetFor(
                ApplicationShutdownReason.Normal)));
    }

    private Task<bool> ShowOnScreenKeyboardAsync(CancellationToken cancellationToken)
    {
        Log.Info($"On-screen keyboard requested: {(_inGameMode ? "Steam" : "Windows")}.");
        return _inGameMode
            ? ToggleSteamSurfaceWithHandoffAsync(SteamNativeSurfaceAction.Keyboard, cancellationToken)
            : RunUiActionAsync(TouchKeyboard.Toggle, cancellationToken);
    }

    private async Task<bool> ToggleSteamSurfaceWithHandoffAsync(SteamNativeSurfaceAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_config.Cef.Enabled || _monitor?.IsAlive != true || _steamUiTransport is not { } transport)
        {
            // Preserve the existing desktop shortcut path when no managed physical handoff is
            // needed. Managed ownership requires authoritative CEF closure before releasing.
            return action != SteamNativeSurfaceAction.Keyboard
                   && _deviceCoordinator?.Controllers.State != ControllerManagementState.Active
                   && await RunUiActionAsync(() => _monitor?.IsAlive == true && Steam.IsBigPictureVisible
                                                                             && Steam.TrySendBigPictureShortcut(
                                                                                 action == SteamNativeSurfaceAction
                                                                                     .QuickAccess
                                                                                     ? BigPictureShortcut.QuickAccess
                                                                                     : BigPictureShortcut.SteamMenu),
                           cancellationToken)
                       .ConfigureAwait(false);
        }

        var snapshot = await SteamSideMenuObserver.ReadAsync(transport, cancellationToken)
            .ConfigureAwait(false);
        var target = SteamControllerHandoff.SelectReplayTarget(snapshot, Steam.IsBigPictureVisible);
        if (target is null)
        {
            return false;
        }

        TaskCompletionSource<bool> dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (action == SteamNativeSurfaceAction.Keyboard && target.KeyboardOpen == true
                                                        && _steamControllerHandoff?.State ==
                                                        SteamControllerOwnership.Steam)
        {
            return true;
        }

        if (_steamControllerHandoff is not { } owner || !owner.TryStart(Replay))
        {
            return false;
        }

        if (action != SteamNativeSurfaceAction.Keyboard)
        {
            return true;
        }

        var finished = await Task.WhenAny(dispatched.Task, owner.Completion).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return finished == dispatched.Task && await dispatched.Task.ConfigureAwait(false);

        async Task<bool> Replay(CancellationToken token)
        {
            try
            {
                var result = await SteamNativeSurfaceCommands.ReplayAsync(
                        transport, action, target.ProcessId, target.AppId, snapshot.Generations, token)
                    .ConfigureAwait(false);
                dispatched.TrySetResult(result);
                return result;
            }
            catch
            {
                dispatched.TrySetResult(false);
                throw;
            }
        }
    }

    /// <summary>
    ///     Opens or closes the Steam UI transport from the master switch, the shell mode and
    ///     the Big Picture window. Callers that can race the master switch hold <c>_cefMasterGate</c>.
    /// </summary>
    /// <remarks>
    ///     Only game mode asks Windows anything: a desktop session opens on the master switch
    ///     alone, so the poll costs nothing there.
    /// </remarks>
    private void ApplySteamUiTransportGate()
    {
        var master = _cefMasterEnabled;
        var inGameMode = _inGameMode;
        var transitionPending = _gameModeCefTransitionPending;
        var bigPictureReady = master && (inGameMode || transitionPending) && SteamUiReadiness.IsReady;
        var open = SteamUiReadiness.TransportShouldBeOpen(
            master, inGameMode, transitionPending, bigPictureReady);
        SteamUiTransportSession.SetEnabled(open);
        string state;
        if (open)
        {
            state = inGameMode || transitionPending
                ? "Steam UI transport open: Big Picture window is up."
                : "Steam UI transport open: desktop mode.";
        }
        else if (master)
        {
            state = inGameMode
                ? "Steam UI transport closed: game mode without a Big Picture window — "
                  + "holding every automatic CEF touch until Steam's UI exists."
                : "Steam UI transport closed: Big Picture was requested — "
                  + "holding every automatic CEF touch until Steam's UI exists.";
        }
        else
        {
            state = "Steam UI transport closed: Steam CEF integration is off.";
        }

        Log.Change("steam-ui-transport-gate", state);
    }

    /// <summary>Asks the gate loop to re-read the shell state now rather than at its next tick.</summary>
    private void RequestSteamUiTransportGateCheck()
    {
        if (_transportGateWork is null || _shutdownRequested)
        {
            return;
        }

        _transportGateSignal.Release();
    }

    /// <summary>
    ///     Retracts every injected Steam UI surface and closes the transport before a
    ///     transition asks Steam for Big Picture.
    /// </summary>
    /// <remarks>
    ///     Steam rebuilds its whole front-end for that request, and the gamepad UI bootstraps against
    ///     whatever <c>SteamClient.System.*</c> then says exists. Namespaces WSGM supplied from
    ///     desktop mode were found there and went unanswered the moment the game-mode gate closed the
    ///     transport two seconds later: the desired Big Picture window stayed recorded native-side
    ///     while no window was ever created (device-diagnosed over CDP, 2026-09-01). Stock Windows
    ///     client state is the one bootstrap Valve ships on this platform, so that is what the rebuild
    ///     must see; everything re-applies through the normal gate once the window exists.
    /// </remarks>
    private async Task PrepareSteamUiForBigPictureAsync()
    {
        if (_steamUiTransport is null)
        {
            return;
        }

        _gameModeCefTransitionPending = true;
        await _cefMasterGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cefMasterEnabled)
            {
                if (_steamUi is not null)
                {
                    try
                    {
                        await _steamUi.DisableAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Retracting the native Steam UI patch for the Big Picture "
                                 + $"request failed: {ex.Message}");
                    }
                }

                try
                {
                    await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("Retracting legacy injected Steam UI for the Big Picture "
                             + $"request failed: {ex.Message}");
                }
            }

            ApplySteamUiTransportGate();
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>
    ///     Ends the Big Picture request hold and re-applies the configured Steam UI state
    ///     for whichever mode the transition settled in. UI thread; safe when no hold is pending.
    /// </summary>
    private void ReleaseSteamUiBigPictureHold()
    {
        if (!_gameModeCefTransitionPending)
        {
            return;
        }

        _gameModeCefTransitionPending = false;
        RequestSteamUiTransportGateCheck();
        Log.Observe(RestoreSteamUiAfterBigPictureAsync(), "Steam UI transition restore");
    }

    /// <summary>Restores the configured surfaces after even a timed-out retraction has finished.</summary>
    private async Task RestoreSteamUiAfterBigPictureAsync()
    {
        await _cefMasterGate.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || !_cefMasterEnabled || _gameModeCefTransitionPending)
                {
                    return;
                }

                _steamUi?.Apply(_config.Cef is { Enabled: true, NativeQuickAccess: true });
                _steamUi?.ApplyPluginSteamUi(_config.Cef.Enabled);
                _steamUi?.ApplySurfaceObservation(_config.Cef.Enabled);
                _steamUi?.ApplyNetworkIndicator(_wifiIndicatorEnabled);
                ApplySteamUiSurfacePreferences();
                // DisableAsync clears the profile. Restore it explicitly instead of depending on
                // a device publication that may have already arrived during the retraction.
                ApplyGlyphConfig(_config);
                KickTabBootSync();
            }, DispatcherPriority.Normal, _shutdownCancellation.Token);
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>
    ///     Owns the transport gate for the session: re-decides it on every signal and at
    ///     <see cref="SteamUiReadiness.TransportGatePollInterval" />, always under the master-switch
    ///     gate so it can never interleave with a retraction.
    /// </summary>
    private async Task RunSteamUiTransportGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ApplySteamUiTransportGate();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Steam UI transport gate check failed: {ex.Message}");
                }
                finally
                {
                    _cefMasterGate.Release();
                }

                await _transportGateSignal
                    .WaitAsync(SteamUiReadiness.TransportGatePollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session shutdown; the owner disposes the transport itself.
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
                // Installed packages only. WSGM bundles none, and the application directory is
                // user-writable, so scanning it would load plugin code from a path the installed
                // root is administrator-protected precisely to avoid.
                _commonPlugins = new CommonPluginManager(_pluginHost, CommonPluginCatalog.InstalledRoot,
                    Path.Combine(Log.Directory, "PluginState"));
                _commonPluginStartup = ApplyCommonPluginConfigAsync(_config);
            }

            coordinator = _overlayTestOnly
                ? null
                : await DeviceCoordinator.TryStartAsync(
                    _config,
                    _pluginHost,
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
        StartDeviceIntegration();
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
        BootManifestWriter.WriteCurrent(_config);

        // Service boot: the service launches WSGM at WTS_SESSION_LOGON — usually
        // BEFORE Winlogon has even started explorer (device-observed 2026-08-07:
        // gating this on IsRunningInSession made the takeover never run, leaving
        // explorer alive behind Big Picture next to our tray host). The takeover
        // owns every explorer state: its readiness poll waits for explorer to
        // appear AND finish logon prep, then shuts it down cleanly; if explorer
        // never shows within the 60 s cap it proceeds like a plain game-mode boot.
        if (_serviceBoot && !_desktopResident)
        {
            StartBootTakeover();
            return;
        }

        if (_desktopResident || ExplorerControl.IsRunningInSession())
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
            _inGameMode = false;
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

        _bootWork = Task.Run(async () =>
        {
            await RunLaunchSequenceAsync();
            _ = TrimAfterBootSettlesAsync(_shutdownCancellation.Token);
        });
    }

    /// <summary>
    ///     Creates the device coordinator, AutoTDP and the Device overlay source, or the simulated source in overlay-test
    ///     mode.
    /// </summary>
    private void StartDeviceIntegration()
    {
        // The resident shell is the sole device-cycle authority. Overlay test deliberately never
        // creates this object, discovers packages, or loads plugin code.
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
            // A separate top-level window: WM_DISPLAYCHANGE is broadcast to top-level windows
            // only, so the message-only window above never hears a monitor appear.
            try
            {
                _displayChangeWindow = DisplayChangeWindow.Create();
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
                TargetFrametimeMs);
            var autoTdp = _autoTdp;
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

            _deviceOverlay = new DeviceOverlayBridge(deviceCoordinator, _autoTdp);
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
            () => _refreshPairing?.FrameLimitRange());
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
            _desktopHost = new ExplorerDesktopHost();
        }

        _modes = _desktopHost is null
            ? new SessionModes(_config, _monitor)
            : new SessionModes(_config, _monitor, _desktopHost);
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
        // Started here rather than by the taskbar, because Steam's audio namespace has to answer
        // while the taskbar is closed. Overlay-test keeps the old behaviour and lets the status
        // cluster own its own, since no Steam surface exists there to serve.
        if (_overlayTestOnly)
        {
            return;
        }

        _audio = new AudioManager();
        _audio.Start();
        _audioProfiles = new AudioProfileService(_audio);

        // Not started here: scanning is expensive and belongs to whichever surface is showing a
        // network list. The manager exists for the whole session so Steam's Internet page can
        // drive it, but it stays idle until something asks.
        _radios = new RadioManager();

        // Started here rather than by the taskbar, for the same reason as audio: Steam's
        // storage pages ask what is ejectable while the overlay is closed, and an unstarted
        // manager would answer "nothing" to someone holding a card.
        _drives = new RemovableDriveManager();

        // Every eject surface reaches the manager, so the policy hangs here rather than at
        // each call site: the overlay's panel and Steam's storage page then mean the same
        // thing by an eject without either knowing about the other.
        _drives.EjectObserver = _libraryPolicy;
        _drives.Start();
        _formats = new SdFormatManager();

        // Over the same two managers the overlay's storage flows use. Steam's revived pages are
        // a second surface on one backend, not a second implementation. The format switch is
        // read through the session's live config, so switching it in Settings takes effect on
        // the next press rather than the next session.
        _steamStorage = new SteamStorageBridge(
            _drives, _formats, () => _config.SteamStorageFormatEnabled, _libraryPolicy);

        // Artwork reads its providers from the session's live config, so a key entered in Settings
        // applies to the next search rather than the next session.
        _artwork = new SteamArtworkBrowserSource(() => _config.Artwork, new ArtworkStateStore());

        // The importer talks to the same running Steam client everything else here does, and reads
        // the machine's installed packages through WinRT. Every seam is injected so the discovery
        // and planning rules stay testable without a live Steam or a real package.
        StoreCatalogClient catalog = new();
        _libraryImport = new SteamLibraryImportSource(
            new XboxLibrarySource(
                XboxPackages.Enumerate,
                XboxPackages.ReadPackageFile,
                (package, token) => catalog.LookUpAsync(package.FamilyName, token)),
            new ImportStateStore(),
            () => new SteamShortcutWriter(
                async token => [.. (await SteamLibraryData.ListGamesAsync(token).ConfigureAwait(false))
                    .Where(game => game.Shortcut)
                    .Select(game => SteamApps.NormalizeAppId(game.AppId))],
                async (name, target, directory, options, token) =>
                    (await SteamApps.AddShortcutAsync(name, target, directory, options, token)
                        .ConfigureAwait(false)).AppId,
                async (appId, target, options, token) =>
                    (await SteamApps.SetShortcutLaunchAsync(appId, target, options, token)
                        .ConfigureAwait(false)).Succeeded,
                async (appId, token) =>
                    (await SteamApps.RemoveShortcutAsync(appId, token).ConfigureAwait(false)).Succeeded),
            async token => [.. await ReadShortcutsAsync(token).ConfigureAwait(false)],
            () => ImportMode.SteamIntegration,
            () => false);
    }

    /// <summary>Reads the non-Steam shortcuts Steam currently has, with what each one runs.</summary>
    /// <remarks>
    ///     The importer needs a shortcut's Target and arguments to tell one it created from one the
    ///     user wrote by hand, and the library listing carries neither, so each is read separately.
    /// </remarks>
    private static async Task<IReadOnlyList<ExistingShortcut>> ReadShortcutsAsync(
        CancellationToken cancellationToken)
    {
        var games = await SteamLibraryData.ListGamesAsync(cancellationToken).ConfigureAwait(false);
        List<ExistingShortcut> shortcuts = [];
        foreach (var game in games.Where(game => game.Shortcut))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appId = SteamApps.NormalizeAppId(game.AppId);
            var details = await SteamApps.ReadDetailsAsync(appId, cancellationToken).ConfigureAwait(false);
            shortcuts.Add(new ExistingShortcut(
                appId,
                details.Details?.ShortcutExe ?? string.Empty,
                details.Details?.ShortcutLaunchOptions ?? string.Empty));
        }

        return shortcuts;
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
            _brightness = new NativeQamBrightnessService(() => !_shutdownRequested, () => { });
        }

        _overlay = new OverlayController(
            _config,
            _monitor,
            _modes,
            _keepAwake,
            _overlayTestOnly,
            new OverlaySources(
                _deviceOverlay,
                _performanceOverlay,
                _pluginOverlaySource = _commonPlugins is null && _deviceCoordinator is null
                    ? null
                    : new CommonPluginOverlaySource(_commonPlugins, _pluginHost, _config.PluginWidgetPins,
                        _deviceCoordinator is not null && _deviceOverlay is not null
                            ? new DeviceWidgetSource(_deviceCoordinator, _deviceOverlay)
                            : null),
                _overlayTestOnly
                    ? null
                    : new DevicePrerequisiteSource(
                        ReadDevicePrerequisiteState, EnableDeviceIntegrationAsync),
                _brightness,
                _deviceCoordinator),
            _audio,
            _audioProfiles,
            _radios,
            _deviceCoordinator?.PowerPresets,
            _deviceCoordinator?.PowerAssignments,
            _drives,
            _formats,
            _displayTimeouts);
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
                ApplicationShutdownRequest.ShutdownLifetime);
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

        // WSGM's own navigation runs on the managed canonical stream when one is delivering, and on
        // SDL otherwise. Subscribed here rather than inside the overlay because this is where both
        // objects exist: the coordinator owns the stream and the controller owns the surfaces.
        // Nothing is unsubscribed on device teardown — the manager simply stops raising, and the
        // router falls back to SDL, which never stopped running.
        if (_deviceCoordinator is not { } canonicalSource || _overlay is not { } overlay)
        {
            return;
        }

        // Queued to the UI thread, never called inline. This event is raised from the plugin
        // runtime's registered ThreadPool wait and runs straight into GamepadNavigation, which
        // reads window visibility and mutates Avalonia focus and controls: UI-thread-owned state
        // that a worker thread must not touch. The rate is bounded by design: the manager raises
        // this only while a WSGM surface has captured input. Losses share the queue, so no
        // sample is delivered ahead of one.
        CanonicalSampleQueue canonicalSamples = new(overlay.SubmitCanonicalSample, overlay.ManagedInputLost);
        canonicalSource.Controllers.UiSampleReceived += canonicalSamples.Enqueue;
        canonicalSource.StateChanged += state =>
        {
            if (state is not DeviceCycleState.Active)
            {
                canonicalSamples.SourceLost();
            }
        };
        // The cycle staying Active is not the same as samples still arriving. Disabling
        // controller management runs make-safe and leaves the cycle Active while the plugin
        // stops publishing, so without this the router waited on a source that had gone quiet
        // and WSGM's own surfaces stopped answering a controller SDL could already see.
        canonicalSource.Controllers.StatusChanged += status =>
        {
            if (status.State is ControllerManagementState.Active)
            {
                return;
            }

            Log.Info(
                $"Managed UI input falls back to SDL: controller management is "
                + $"{status.State} ({status.Detail}).");
            canonicalSamples.SourceLost();
        };
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
                ToggleSteamSurfaceWithHandoffAsync(SteamNativeSurfaceAction.QuickAccess, token),
            ToggleSteamOverlayAsync = token => ToggleSteamSurfaceWithHandoffAsync(SteamNativeSurfaceAction.Home, token),
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
                // Null when no plugin publishes VRR, which is also when the projection omits
                // is_vrr_supported and Valve's row does not render. One fact, one source. The
                // user-facing wrapper persists the state to the per-application layer in force; the
                // bare ApplyVariableRefreshRateAsync stays the profile restore's device write.
                _deviceCoordinator is null ? null : _applicationProfiles.SetVariableRefreshRateFromUserAsync,
                () => _overlay?.ShowBluetoothPanel() == true,
                _brightness,
                _steamStorage,
                _overlayTestOnly ? null : _displayTimeouts,
                _audioProfiles,
                _commonPlugins is null ? null : new CommonPluginSteamUiSource(_commonPlugins, _pluginHost),
                _profiles,
                // Null in overlay-test, which has no Steam client to read artwork for or write it to.
                _artwork,
                _libraryImport);
            _steamUi.Apply(_config.Cef is { Enabled: true, NativeQuickAccess: true });
            _steamUi.ApplyPluginSteamUi(_config.Cef.Enabled);
            _steamUi.ApplySurfaceObservation(_config.Cef.Enabled);
            if (_deviceCoordinator is { } handoffDevice)
            {
                _steamControllerOwnership = new SteamControllerOwnershipAdapter(handoffDevice, () => Steam.IsRunning,
                    (active, token) => RunUiActionAsync(() =>
                    {
                        SdlGamepads.SetSteamOwnership(active);
                        return true;
                    }, token));
                _steamControllerHandoff = new SteamControllerHandoff(
                    _steamControllerOwnership.ReleaseAsync,
                    _steamControllerOwnership.RestoreAsync,
                    token => SteamSideMenuObserver.ReadAsync(_steamUiTransport!, token),
                    () => _monitor?.IsAlive == true,
                    Log.Info,
                    originalSteamExited: () => _steamControllerOwnership.OriginalSteamExited,
                    ownerIsCurrent: _steamControllerOwnership.OwnerIsCurrentAsync);
                _overlay.SteamOwnership = () => _steamControllerHandoff;
            }

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
        // over FindWindow — see TrayHost): gone before explorer starts, back
        // after game mode kills it. Apps re-home their icons on each side's
        // TaskbarCreated broadcast.
        _modes.DesktopModeStarting += () =>
        {
            // Retire the actual shell window before optional plugin/card/UI notifications.
            _trayHost?.Dispose();
            _trayHost = null;
            _overlay?.AttachTrayHost(null);
            _inGameMode = false;
            _desktopTray?.SetDesktop(true);
            _ = NotifyPluginModeAsync(PluginSessionMode.Desktop);
            RequestSteamUiTransportGateCheck();
            _tabBootSyncCancellation.Cancel();
            // Tabs and the badge are game-mode surfaces; the ACF watcher only exists
            // to keep them fresh, so it stands down with them.
            ApplyCardServices(false);
            // The Wi-Fi indicator and download sort stay: Big Picture on the desktop draws the same
            // header and the same download queue (Claw, 2026-09-11).
            _ = SteamLibraryTabs.DisableAsync();
            _volumeButtons?.SetGameModeActive(false);
        };
        _modes.PrepareSteamUiForBigPictureAsync = PrepareSteamUiForBigPictureAsync;
        _modes.SteamUiBigPictureRequestSettled = () =>
            Dispatcher.UIThread.Post(ReleaseSteamUiBigPictureHold);
        _modes.GameModeEntered += () =>
        {
            _inGameMode = true;
            _desktopTray?.SetDesktop(false);
            ReleaseSteamUiBigPictureHold();
            RequestSteamUiTransportGateCheck();
            EnterGameModeSurfaces();
            _steamUi?.ApplyNetworkIndicator(_wifiIndicatorEnabled);
            ApplySteamUiSurfacePreferences();
            // Returning from desktop mode disabled tabs/badge and cancelled the boot
            // sync; re-inject without requiring an overlay open.
            KickTabBootSync();
        };
        // A fresh Steam start while WSGM keeps running (client update, crash restart)
        // wipes the injected tabs and the resident badge with the old CEF session —
        // re-inject once the new UI is up.
        _monitor.SteamStarted += () =>
        {
            RequestSteamUiTransportGateCheck();
            if (!_inGameMode)
            {
                return;
            }

            KickTabBootSync();
            // A restarted client rebuilds its folder list from libraryfolders.vdf,
            // which can bring back a library for a card that is no longer in the
            // reader — and no volume notification will fire to say so.
            _cardVolumes?.Kick("Steam restarted");
        };
        // Steam leaving in game mode closes the transport gate at once, so a restart's
        // fresh, still-headless CEF session cannot be connected before its own Big
        // Picture window exists.
        _monitor.SteamExited += RequestSteamUiTransportGateCheck;
    }

    private async Task NotifyPluginModeAsync(PluginSessionMode mode)
    {
        try
        {
            await _pluginHost.SetModeAsync(mode, DateTimeOffset.UtcNow.AddSeconds(5), _shutdownCancellation.Token)
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
            var watch = Stopwatch.StartNew();
            while (!InputDesktop.IsDefaultInputDesktop() && watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(250, _shutdownCancellation.Token).ConfigureAwait(false);
            }

            // Before the start, so a Steam autostart that reappeared cannot win the race.
            SteamAutostartService.ReapplyAtStart();
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

            _ = TrimAfterBootSettlesAsync(_shutdownCancellation.Token);
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
        // Input-desktop barrier (era-proven): WTS_SESSION_LOGON fires while the
        // Welcome screen still owns the input desktop — proceeding then starts
        // Steam audibly behind LogonUI. WTS_SESSION_DESKTOP_READY never arrives
        // on this hardware; polling for winsta0\Default is the working signal.
        var desktopWatch = Stopwatch.StartNew();
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
        _inGameMode = false;
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
    ///     Cancels any in-flight boot sync and starts a fresh one (waits for
    ///     Steam's UI, then injects tabs and pushes the badge map). Safe to call on
    ///     every trigger — SyncAllAsync's gate serializes overlapping runs (each queued
    ///     caller still runs a full sync; they are not collapsed into one).
    /// </summary>
    private void KickTabBootSync()
    {
        if (_shutdownRequested)
        {
            return;
        }

        var previous = _tabBootSyncCancellation;
        var current = new CancellationTokenSource();
        _tabBootSyncCancellation = current;
        previous.Cancel();
        _ = RunTabBootSyncAsync(current);
    }

    private async Task RunTabBootSyncAsync(CancellationTokenSource owner)
    {
        try
        {
            await LibraryTabManager.SyncOnBootAsync(owner.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            Log.Info("Library tab boot sync superseded or cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fire-and-forget: without this a failed boot sync left the tabs missing with
            // nothing in the log.
            Log.Warn($"Library tab boot sync failed: {ex.Message}");
        }
        finally
        {
            if (!ReferenceEquals(_tabBootSyncCancellation, owner))
            {
                owner.Dispose();
            }
        }
    }

    /// <summary>
    ///     Re-applies the Steam UI surfaces that work without native Quick Access from the
    ///     session's saved preferences, after the host was created or retracted them.
    /// </summary>
    private void ApplySteamUiSurfacePreferences()
    {
        _steamUi?.ApplyPluginSteamUi(_config.Cef.Enabled);
        _steamUi?.ApplyDownloadSort(_downloadSortEnabled);
        _steamUi?.ApplyLibraryBadge(_libraryBadgeEnabled);
        _steamUi?.ApplyHomeCarousel(_homeCarouselEnabled, _carouselShowUninstalled);
        _steamUi?.ApplyScreensaverTimeouts(_screensaverTimeoutsEnabled);
    }

    /// <summary>
    ///     Starts or retracts the injected download-queue sort buttons to match a
    ///     reloaded configuration, so the toggle applies without a re-logon.
    /// </summary>
    /// <param name="enabled">Whether the sort buttons should be injected.</param>
    private void ApplyDownloadSort(bool enabled)
    {
        if (_overlayTestOnly || enabled == _downloadSortEnabled)
        {
            _downloadSortEnabled = enabled;
            return;
        }

        _downloadSortEnabled = enabled;
        // Either mode: Big Picture on the desktop draws the same download queue.
        _steamUi?.ApplyDownloadSort(enabled);
        Log.Info($"Download queue sorting {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>
    ///     Shows or retracts the library badge on Steam's tiles to match a reloaded
    ///     configuration, so the card manager toggle applies without a re-logon.
    /// </summary>
    /// <param name="enabled">Whether the badge should be drawn.</param>
    private void ApplyLibraryBadge(bool enabled)
    {
        if (_overlayTestOnly || enabled == _libraryBadgeEnabled)
        {
            _libraryBadgeEnabled = enabled;
            return;
        }

        _libraryBadgeEnabled = enabled;
        _steamUi?.ApplyLibraryBadge(enabled);
        Log.Info($"Library badge {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>
    ///     Applies the connected-library Home carousel and its uninstalled-games preference
    ///     from a reloaded configuration, so both switches apply without a re-logon.
    /// </summary>
    /// <param name="enabled">Whether Home's carousel lists the attached libraries.</param>
    /// <param name="showUninstalled">Whether it also lists owned games that are not installed.</param>
    private void ApplyHomeCarousel(bool enabled, bool showUninstalled)
    {
        var changed = enabled != _homeCarouselEnabled || showUninstalled != _carouselShowUninstalled;
        _homeCarouselEnabled = enabled;
        _carouselShowUninstalled = showUninstalled;
        if (_overlayTestOnly || !changed)
        {
            return;
        }

        _steamUi?.ApplyHomeCarousel(enabled, showUninstalled);
        Log.Info($"Home carousel {(enabled ? "enabled" : "disabled")}"
                 + $"{(enabled ? $", uninstalled games {(showUninstalled ? "shown" : "hidden")}" : "")}.");
    }

    /// <summary>
    ///     Adds or retracts the display-off rows in Steam's Screensaver settings to match a
    ///     reloaded configuration. They follow the CEF master switch alone.
    /// </summary>
    /// <param name="enabled">Whether the rows should be drawn.</param>
    private void ApplyScreensaverTimeouts(bool enabled)
    {
        if (_overlayTestOnly || enabled == _screensaverTimeoutsEnabled)
        {
            _screensaverTimeoutsEnabled = enabled;
            return;
        }

        _screensaverTimeoutsEnabled = enabled;
        _steamUi?.ApplyScreensaverTimeouts(enabled);
    }

    /// <summary>
    ///     Applies a Steam Input Management change that arrived through a
    ///     config reload.
    /// </summary>
    /// <remarks>
    ///     The park/restore rename touches Steam's directory, so it runs off the UI
    ///     thread. Reconciles are idempotent and serialized inside
    ///     <see cref="SteamInputShim" />, which is what lets the Settings save path and
    ///     this watcher both fire without coordinating.
    /// </remarks>
    private static void ApplySteamInputManagement(bool enabled)
    {
        if (SteamInputShim.Enabled == enabled)
        {
            return;
        }

        SteamInputShim.SetEnabled(enabled);
        _ = Task.Run(() => SteamInputShim.Reconcile("settings-change"));
    }

    /// <summary>
    ///     Mirrors the master CEF switch, retracting anything WSGM already
    ///     injected on the way down. Ordering is load-bearing: the switch fails every
    ///     evaluation closed, including WSGM's own retractions, so flipping it first
    ///     would strand the registered patches, tabs and badge in Steam until the client
    ///     restarted — with the desktop-trip cleanup dead for the same reason. Both
    ///     directions run through <c>_cefMasterGate</c> and re-read the field (the
    ///     wanted state) once they own it, so a flip landing inside a retraction's
    ///     removal sequence cannot leave the choke point closed while the field —
    ///     and the equality guard that would have repaired it — say enabled.
    /// </summary>
    /// <param name="enabled">The reloaded <c>Cef.Enabled</c> value.</param>
    private void ApplyCefMasterSwitch(bool enabled)
    {
        if (_cefMasterEnabled == enabled)
        {
            return;
        }

        _cefMasterEnabled = enabled;
        _runningApplications?.SetSteamEnabled(enabled);
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

                    // Through the readiness gate, not straight to open: a master switch
                    // turned on while Steam is cold-starting in game mode still waits
                    // for its window.
                    ApplySteamUiTransportGate();
                }
                finally
                {
                    _cefMasterGate.Release();
                }

                // Field-mutating and fire-and-forget from the UI thread, like every
                // other caller.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || !_cefMasterEnabled || _gameModeCefTransitionPending)
                    {
                        return;
                    }

                    ApplyCardServices(_inGameMode);
                    KickTabBootSync();
                    ApplySteamUiSurfacePreferences();
                    ApplyGlyphConfig(_config);
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
                    await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
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
                    ApplySteamUiTransportGate();
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

    /// <summary>
    ///     Whether the automatic download wake lock may poll Steam: its CEF
    ///     query is autonomous Steam traffic, so it stays off in overlay-test mode
    ///     alongside the other injections that mode excludes.
    /// </summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool AutoKeepAwakeEnabled(AppConfig config)
    {
        return !_overlayTestOnly && config.Cef is { Enabled: true, DownloadKeepAwake: true };
    }

    /// <summary>
    ///     Whether the shared Steam download poll has at least one consumer.
    ///     The mute feature reuses the same answer even when its automatic wake lock is
    ///     disabled; overlay-test still excludes all autonomous Steam traffic.
    /// </summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool DownloadMonitoringEnabled(AppConfig config)
    {
        return !_overlayTestOnly
               && config.Cef.Enabled
               && (config.Cef.DownloadKeepAwake || config.MuteWhileDisplayOff);
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

    private void OnSessionLocked()
    {
        QueueDevicePowerTransition(true, "session locked");
    }

    private void OnSessionUnlocked()
    {
        QueueDevicePowerTransition(false, "session unlocked");
    }

    private void OnSystemSuspending()
    {
        QueueDevicePowerTransition(true, "system suspending");
    }

    private void OnSystemResumed()
    {
        QueueDevicePowerTransition(false, "system resumed");
        QueueDesktopActions(false);
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

    /// <summary>
    ///     What this install has, of the things a device package needs.
    ///     Read live rather than cached: the protected slot is a directory an administrator can copy
    ///     into while WSGM is running, which is the whole case this exists for.
    /// </summary>
    private DevicePrerequisiteState ReadDevicePrerequisiteState()
    {
        bool package;
        try
        {
            package = DevicePackageStager.InventoryEffectiveInstalledPackage(
                DeviceInstallationPaths.InstalledPackageRoot).PackageRoots.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException or ArgumentException)
        {
            // An unreadable slot is not evidence of a package, and a banner must not guess.
            Log.Warn("Reading the device package slot for the overlay banner failed: " + ex.Message);
            package = false;
        }

        return new DevicePrerequisiteState(
            package,
            _config.DeviceIntegration.Enabled,
            DevicePrerequisiteSource.ControllerLibraryInstalled(AppContext.BaseDirectory),
            DevicePrerequisiteSource.HidHideInstalled());
    }

    private Task EnableDeviceIntegrationAsync()
    {
        return Task.Run(() =>
        {
            ConfigStore.Mutate(fresh => fresh.DeviceIntegration.Enabled = true);
            _config.DeviceIntegration.Enabled = true;
            Log.Info("Device Integration enabled from the overlay's prerequisites banner.");
        });
    }

    private PluginActionSequence ActionSequence()
    {
        return new PluginActionSequence(new PluginHostActionInvoker(_pluginHost), Log.Info);
    }

    /// <summary>
    ///     Every action the running non-device instances declare, for the Settings lists.
    ///     Device instances are excluded: their controls belong to the Device surfaces, and a session
    ///     automation step reaching into hardware policy would be a second owner for it.
    /// </summary>
    private IReadOnlyList<SettingsViewModel.PluginActionOption> ReadPluginActionOptions()
    {
        if (_pluginOverlaySource is not { } source)
        {
            return [];
        }

        PluginInstanceIdentity[] devices =
            [.. source.Device?.Snapshot().Select(instance => instance.Identity) ?? []];
        return
        [
            .. source.Snapshot()
                .Where(instance => !Array.Exists(devices, device => device == instance.Identity)
                                   && instance.Controls is not null)
                .SelectMany(instance => instance.Controls!.Actions.Select(action =>
                    new SettingsViewModel.PluginActionOption(instance.Identity, action,
                        $"{instance.Name} / {instance.Identity.InstanceId}: {action.Label}")))
        ];
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
        if (_shutdownRequested || _overlayTestOnly || !_desktopActionAdmission.TryBegin(
                _inGameMode, _modes?.TransitionInProgress != false, Environment.TickCount64))
        {
            return;
        }

        var acquired = false;
        try
        {
            await _displayActionGate.WaitAsync(_shutdownCancellation.Token);
            acquired = true;
            var config = await Task.Run(ConfigStore.Load, _shutdownCancellation.Token);
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

    /// <summary>Quiesces or revives the device cycle with the session it belongs to.</summary>
    /// <param name="suspend">Whether the cycle should quiesce.</param>
    /// <param name="reason">The notification that asked for it, for the log.</param>
    /// <remarks>
    ///     Edge-triggered and serialized, because the four notifications overlap: a sleep started from
    ///     the lock screen delivers a lock and a suspend, and Windows sends both resume events for one
    ///     wake. Neither coordinator call is idempotent — resume advances the cycle generation — so
    ///     only a real transition is forwarded, and each one waits for the previous to finish.
    /// </remarks>
    private void QueueDevicePowerTransition(bool suspend, string reason)
    {
        if (_shutdownRequested)
        {
            return;
        }

        var coordinator = _deviceCoordinator;
        if (coordinator is null && _commonPlugins is null)
        {
            Log.Info(
                $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): no "
                + "device coordinator is active.");
            return;
        }

        lock (_devicePowerGate)
        {
            var effective = _pendingDeviceSuspended ?? _deviceSuspended;
            if (effective == suspend)
            {
                Log.Info(
                    $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): the "
                    + $"cycle is already {(suspend ? "suspended or suspending" : "running or resuming")}.");
                return;
            }

            _pendingDeviceSuspended = suspend;
            var requestGeneration = ++_devicePowerRequestGeneration;
            _devicePowerWork = ApplyDevicePowerTransitionAsync(
                _devicePowerWork,
                coordinator,
                suspend,
                reason,
                requestGeneration);
        }
    }

    private async Task ApplyDevicePowerTransitionAsync(
        Task previous,
        DeviceCoordinator? coordinator,
        bool suspend,
        string reason,
        long requestGeneration)
    {
        // Never faults: the continuation below reports its own failures and returns normally, so
        // awaiting the previous transition cannot throw here.
        await previous.ConfigureAwait(false);
        try
        {
            var deviceWork = coordinator is null ? Task.CompletedTask
                : suspend ? coordinator.SuspendAsync() : coordinator.ResumeAsync();
            await Task.WhenAll(deviceWork, ApplyCommonPluginPowerAsync(suspend)).ConfigureAwait(false);

            lock (_devicePowerGate)
            {
                _deviceSuspended = suspend;
                if (_devicePowerRequestGeneration == requestGeneration)
                {
                    _pendingDeviceSuspended = null;
                }
            }

            Log.Info($"Device cycle {(suspend ? "suspended" : "resumed")}: {reason}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_devicePowerGate)
            {
                if (_devicePowerRequestGeneration == requestGeneration)
                {
                    _pendingDeviceSuspended = null;
                }
            }

            Log.Error($"Device cycle {(suspend ? "suspend" : "resume")} failed ({reason})", ex);
        }
    }

    /// <summary>Hands a foreground application change to the running-application monitor.</summary>
    /// <param name="executable">Foreground executable file name.</param>
    /// <param name="imagePath">Its full image path, or null when the process could not be opened.</param>
    /// <param name="processId">Its process identifier, or zero when it could not be read.</param>
    /// <remarks>
    ///     Straight through, with no policy of its own: the monitor's projection decides whether this
    ///     identity is used at all, so the precedence between Steam and the foreground stays in the one
    ///     pure function that can be tested.
    /// </remarks>
    private void OnForegroundApplicationChanged(string executable, string? imagePath, uint processId)
    {
        _runningApplications?.ReportForeground(executable, imagePath, processId);
    }

    /// <summary>Applies a refresh rate the user chose by hand.</summary>
    /// <param name="refreshHz">The chosen rate.</param>
    /// <returns>Whether the display is now at that rate.</returns>
    /// <remarks>
    ///     The ownership and validation rules live with
    ///     <see
    ///         cref="RefreshRatePairingService.TryApplyManual" />
    ///     ; this only supplies the cap in force.
    /// </remarks>
    private bool ApplyManualRefreshRate(int refreshHz)
    {
        return _refreshPairing?.TryApplyManual(
            refreshHz,
            _performance?.Current.Desired.FrameLimit ?? 0) ?? false;
    }

    private NativeQamPerfSupport ReadNativeQamPerfSupport()
    {
        var pairing = _refreshPairing;
        var options = pairing?.FrameLimitOptions() ?? [];
        // The same predicate the pairing service decides by, not a second copy of the comparison:
        // under either coupled strategy the pairing policy owns the refresh rate, so Steam's manual
        // refresh row must not be offered at all — a user setting it would watch the next frame-cap
        // change overwrite it.
        var manualRefresh = FrameLimitPairing.RefreshRateIsUserOwned(
            _config.Performance.FrameLimitStrategy);

        var vrr = false;
        var vrrEnabled = false;
        if (_deviceCoordinator is { } coordinator)
        {
            var view = coordinator.Capabilities.Snapshot().FirstOrDefault(candidate =>
                candidate.Descriptor.Role is CapabilityRole.VariableRefreshRate
                && candidate.Projection.State.Available);
            vrr = view is not null;
            // Read from the same capability that reports support, so the toggle cannot show a state
            // the device disagrees with.
            vrrEnabled = view?.Projection.State.ObservedValue?.BooleanValue ?? false;
        }

        // Read through the pairing service's session cache: this runs on every state publication,
        // and enumerating plus CDS_TESTing every mode each time hammers the display driver.
        // Enumerated under every strategy: with the frame limit switched off the unified row
        // becomes a refresh-rate slider, offered whatever the pairing strategy is because there is
        // no cap left for it to fight. RefreshRatesSelectable below still gates Valve's SEPARATE
        // manual row, which must stay hidden while a cap owns the rate.
        var refreshRates = pairing?.AcceptedRates() ?? [];
        return new NativeQamPerfSupport(
            options,
            vrr,
            manualRefresh && refreshRates.Count > 0,
            refreshRates.Count > 0 ? refreshRates.Min() : null,
            refreshRates.Count > 0 ? refreshRates.Max() : null,
            vrrEnabled,
            refreshRates.Count > 0 ? DisplayProfiles.ReadCurrentRefreshRate() : null,
            ReadPairedRefreshRates(pairing, options, manualRefresh),
            refreshRates);
    }

    /// <summary>The refresh rate each offered cap will be presented at.</summary>
    /// <remarks>
    ///     Built here rather than in the injected half so the pairing policy stays one decision in one
    ///     place. Empty under the uncoupled strategy, where a cap changes no display state and the row
    ///     therefore has no rate to name — which is also what makes the label collapse from
    ///     "60 FPS (60 Hz)" to plain "60 FPS" without a second flag saying so.
    /// </remarks>
    private static Dictionary<int, int>? ReadPairedRefreshRates(
        RefreshRatePairingService? pairing,
        IReadOnlyList<int> options,
        bool uncoupled)
    {
        if (uncoupled || pairing is null || options.Count == 0)
        {
            return null;
        }

        Dictionary<int, int> paired = new(options.Count);
        foreach (var cap in options)
        {
            if (cap > 0 && pairing.SelectRefreshHz(cap) is { } hz)
            {
                paired[cap] = hz;
            }
        }

        return paired.Count > 0 ? paired : null;
    }

    /// <summary>Starts or stops the game-mode card services from one shared policy.</summary>
    /// <remarks>
    ///     Initial direct boot and a later desktop-to-game transition are separate entry
    ///     paths: only the latter raises <c>GameModeEntered</c>. Keeping their activation
    ///     here prevents one path from silently losing volume notifications again.
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
                    Dispatcher.UIThread.Post(KickTabBootSync);
                    return Task.CompletedTask;
                },
                _libraryPolicy);
        }
        else
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        }
    }

    /// <summary>
    ///     Starts or stops the Big Picture Wi-Fi indicator to match a reloaded
    ///     configuration. Without this the feed keeps running (and keeps being recreated
    ///     on every game-mode entry) after the user turns the toggle off, because the
    ///     start gates read the boot-time configuration.
    /// </summary>
    /// <param name="enabled">Whether the indicator should be feeding Steam.</param>
    private void ApplyNetworkIndicator(bool enabled)
    {
        if (_overlayTestOnly)
        {
            _wifiIndicatorEnabled = enabled;
            Log.Change(
                "steam.network-indicator",
                "Big Picture Wi-Fi indicator not applied: mode=overlay-test.");
            return;
        }

        if (enabled == _wifiIndicatorEnabled)
        {
            _wifiIndicatorEnabled = enabled;
            return;
        }

        _wifiIndicatorEnabled = enabled;
        if (!enabled)
        {
            _steamUi?.ApplyNetworkIndicator(false);
            Log.Info("Big Picture Wi-Fi indicator turned off.");
            return;
        }

        // Either mode: Big Picture on the desktop draws the same header indicator.
        _steamUi?.ApplyNetworkIndicator(true);
        Log.Info("Big Picture Wi-Fi indicator turned on.");
    }

    private void WatchConfig()
    {
        try
        {
            _configWatcher = new FileSystemWatcher(Log.Directory, "config.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };

            // The LOAD stays off the UI thread: it takes the cross-process config
            // mutex (2 s timeout) that a settings save holds across the write, the
            // splash-asset promotion and the boot manifest — 500 ms of debounce does
            // not reliably outlast that. Only the cheap, UI-affine apply is posted.
            void Reload(object? state)
            {
                _ = Task.Run(() =>
                {
                    var generation = Interlocked.Read(ref _configReloadGeneration);
                    var config = ConfigStore.Load();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || generation != Interlocked.Read(ref _configReloadGeneration))
                        {
                            return;
                        }

                        // One instance for every reader: the volume OSD's UI-scale
                        // callback and DisplayScale's saved-scale snapshot must not
                        // drift onto different AppConfig objects.
                        _config = config;
                        _profiles.ApplyConfig(config.Profiles);
                        ApplyDeviceConfig(config);
                        ApplyPerformanceConfig(config);
                        ApplyCefMasterSwitch(config.Cef.Enabled);
                        _steamUi?.ApplyPluginSteamUi(config.Cef.Enabled);
                        if (config.Cef.Enabled)
                        {
                            _steamUi?.Apply(config.Cef.NativeQuickAccess);
                            _steamUi?.ApplySurfaceObservation(true);
                            ApplyGlyphConfig(config);
                        }

                        ApplySteamInputManagement(config.SteamInputManagementEnabled);
                        ApplyNetworkIndicator(config.Cef is { Enabled: true, WifiIndicator: true });
                        ApplyDownloadSort(config.Cef is { Enabled: true, DownloadQueueSort: true });
                        ApplyLibraryBadge(config.Cef is { Enabled: true, CardManager: true });
                        ApplyHomeCarousel(
                            config.Cef is { Enabled: true, ConnectedLibraryCarousel: true },
                            config.Cef.CarouselShowUninstalled);
                        ApplyScreensaverTimeouts(config.Cef.Enabled);
                        _displayMute?.ApplyConfig(config.MuteWhileDisplayOff);
                        _overlay?.ApplyConfig(config);
                        _startupWatcher?.Apply(config.StartupApps);
                        _keepAwake?.ApplyConfig(
                            AutoKeepAwakeEnabled(config),
                            DownloadMonitoringEnabled(config));
                    });
                });
            }

            // Changed/Renamed fire on threadpool threads — the swap must be locked
            // so two near-simultaneous events can't both dispose the same timer and
            // orphan one that still fires.
            void Debounce()
            {
                lock (_configDebounceGate)
                {
                    Interlocked.Increment(ref _configReloadGeneration);
                    _configDebounce ??= new Timer(
                        Reload, null, Timeout.Infinite, Timeout.Infinite);
                    _configDebounce.Change(500, Timeout.Infinite);
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
                    if (sender is not FileSystemWatcher watcher)
                    {
                        return;
                    }

                    watcher.EnableRaisingEvents = false;
                    watcher.EnableRaisingEvents = true;
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

        return await Dispatcher.UIThread.InvokeAsync(() =>
            !_shutdownRequested && action());
    }

    private async Task<bool> CyclePerformanceOverlayLevelAsync(
        CancellationToken cancellationToken)
    {
        if (_shutdownRequested || _performanceOverlay is null)
        {
            return false;
        }

        return await _performanceOverlay.CycleOverlayLevelAsync("oem-action", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CyclePerformanceProfileAsync(CancellationToken cancellationToken)
    {
        if (_deviceCoordinator is not { } coordinator
            || !await coordinator.PowerAssignments.CycleAsync(cancellationToken).ConfigureAwait(false))
        {
            Log.Info("OEM performance-profile cycle skipped: the device offers no power presets right now.");
            return false;
        }

        return true;
    }

    private void OnSessionEnding()
    {
        if (_disposed)
        {
            return;
        }

        Log.Info("Interactive session is ending; requesting bounded session cleanup.");
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        ApplicationShutdownRequest.ShutdownLifetime();
    }

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
        // ReSharper disable once MethodHasAsyncOverload
        _shutdownCancellation.Cancel();
        _brightness?.Dispose();
        // Every cleanup step still runs after an earlier one fails; the collected
        // failures are reported once at the end so the outer coordinator records the
        // shutdown as unverified without any step having been skipped.
        List<Exception> failures = [];
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
                RecordShutdownFailure(failures, "Shell startup failed before shutdown cleanup", ex);
            }
        }

        _bootTakeover?.RequestShutdown();
        _modes?.RequestShutdown();
        try
        {
            _splash?.Dismiss("application shutdown");
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "Dismissing the boot splash during application shutdown failed", ex);
        }

        // Close input admission on the UI thread before any safety-critical asynchronous cleanup.
        try
        {
            _overlay?.Dispose();
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "Closing overlay command admission during application shutdown failed", ex);
        }
        finally
        {
            _overlay = null;
        }

        // ReSharper disable once MethodHasAsyncOverload
        _tabBootSyncCancellation.Cancel();

        // The fan-out writes to the device, RTSS and power, so it stops before any of them is torn
        // down; a pass left running could otherwise command a coordinator that is being disposed.
        try
        {
            if (_profileFanOut is not null)
            {
                await _profileFanOut.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Stopping the profile fan-out during application shutdown failed", ex);
        }
        finally
        {
            _profileFanOut = null;
        }

        // Device cleanup is the safety-critical part of the outer application budget.
        if (_steamControllerHandoff is not null)
        {
            await _steamControllerHandoff.DisposeAsync().ConfigureAwait(false);
            _steamControllerHandoff = null;
        }

        _steamControllerOwnership?.Dispose();
        _steamControllerOwnership = null;
        await Dispatcher.UIThread.InvokeAsync(() => SdlGamepads.SetSteamOwnership(false));

        // Run it before waiting on shell transitions or doing Explorer/CEF/RTSS teardown.
        // If the outer owner reaches its deadline, process exit still unloads the in-process
        // runtime while the shell anchor remains available for owner-loss desktop recovery.
        // Before the coordinator, deliberately. AutoTDP restores the limit it took over from
        // through that coordinator's capability path, so disposing it afterwards issued the restore
        // into an already-disconnected runtime and left the handheld on the last automatically
        // selected wattage on every exit, update, uninstall and session end.
        DetachOsdPowerStatus();
        if (_autoTdp is not null)
        {
            try
            {
                await _autoTdp.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures, "AutoTDP restoration was unverified during application shutdown", ex);
            }
            finally
            {
                _autoTdp = null;
                _deviceCoordinator?.AttachAutoTdpManualOverride(null);
                _deviceCoordinator?.AttachAutoTdpAvailability(null);
                if (_deviceCoordinator is { } coordinator)
                {
                    coordinator.PowerPresets.AutomaticPowerOwner = null;
                }
            }
        }

        if (_deviceCoordinator is not null)
        {
            var deviceReason = reason switch
            {
                ApplicationShutdownReason.Update =>
                    PluginStopReason.Updating,
                ApplicationShutdownReason.SessionEnd =>
                    PluginStopReason.SessionEnding,
                ApplicationShutdownReason.Uninstall =>
                    PluginStopReason.Uninstalling,
                _ => PluginStopReason.WsgmExiting
            };
            _deviceCoordinator.PhysicalGlyphCatalog.Changed -= OnPhysicalGlyphProfilesChanged;
            try
            {
                await _deviceCoordinator.ShutdownAsync(deviceReason, deadline).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures, "Device cleanup was unverified; remaining shell cleanup continues", ex);
            }
            finally
            {
                _deviceCoordinator = null;
            }
        }

        if (_commonPlugins is { } commonPlugins)
        {
            try
            {
                await commonPlugins.StopAsync(deadline).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordShutdownFailure(failures,
                    "Common plugin cleanup was unconfirmed; remaining shell cleanup continues", ex);
            }
        }

        // From here on every step is independently guarded: a throw from any one of them,
        // Steam UI, AutoTDP-adjacent handoff, performance, display restore, or a manager
        // disposal, must not skip the ones after it. A single shared try around this whole
        // stretch previously let one failure stop everything below it, contradicting the
        // invariant above and, on an Update reason, leaving the audio, radio and drive
        // managers holding endpoints and device notifications while the installer replaces
        // files underneath them.
        try
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
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Waiting for the in-flight mode transition during application shutdown failed", ex);
        }

        try
        {
            if (_bootWork is not null)
            {
                await _bootWork.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Waiting for the boot worker during application shutdown failed", ex);
        }
        finally
        {
            _bootWork = null;
        }

        try
        {
            // Ends on the cancelled session token; awaited so it can never re-decide the
            // transport after the disposal below has begun.
            if (_transportGateWork is not null)
            {
                await _transportGateWork.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Waiting for the transport gate during application shutdown failed", ex);
        }
        finally
        {
            _transportGateWork = null;
        }

        var trayRetired = false;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(RetireTrayHostForShutdown);
            trayRetired = true;
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "Retiring the WSGM taskbar during application shutdown failed", ex);
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(DisposeUiOwnedSessionResources);
        }
        catch (Exception ex)
        {
            RecordShutdownFailure(failures, "UI-owned shell cleanup failed during application shutdown", ex);
        }

        var desktopVerified = false;
        try
        {
            desktopVerified = trayRetired
                              && await RestoreDesktopBeforeShutdownAsync(reason, deadline).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Restoring the desktop during application shutdown failed", ex);
        }

        try
        {
            if (desktopVerified && _desktopHost is not null)
            {
                await _desktopHost.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the desktop host during application shutdown failed", ex);
        }
        finally
        {
            _desktopHost = null;
        }

        // AutoTDP is already gone: it is disposed before the device coordinator, above,
        // because its restoration needs that coordinator's write path.
        try
        {
            if (_runningApplicationTargets is not null)
            {
                await _runningApplicationTargets.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing running-application targets during application shutdown failed",
                ex);
        }
        finally
        {
            _runningApplicationTargets = null;
        }

        try
        {
            if (_foregroundWindows is not null)
            {
                _foregroundWindows.ApplicationChanged -= OnForegroundApplicationChanged;
                _foregroundWindows.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Disposing the foreground window watcher during application shutdown failed", ex);
        }
        finally
        {
            _foregroundWindows = null;
        }

        try
        {
            if (_runningApplications is not null)
            {
                await _runningApplications.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing running applications during application shutdown failed", ex);
        }
        finally
        {
            _runningApplications = null;
        }

        // After the monitor, which is the only thing that reads it.
        _pairingFrametimes?.Dispose();
        _pairingFrametimes = null;
        await _cefMasterGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_steamUi is not null)
            {
                await _steamUi.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the Steam UI session during application shutdown failed", ex);
        }
        finally
        {
            _steamUi = null;
            _cefMasterGate.Release();
        }

        try
        {
            if (_steamUiTransport is not null)
            {
                SteamUiTransportSession.Detach(_steamUiTransport);
                await _steamUiTransport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the Steam UI transport during application shutdown failed", ex);
        }
        finally
        {
            _steamUiTransport = null;
        }

        try
        {
            if (_performance is not null)
            {
                _performance.StateChanged -= OnPerformanceStateForPairing;
                await _performance.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing performance monitoring during application shutdown failed", ex);
        }
        finally
        {
            _performance = null;
        }

        // Before the session ends, not after: the applied rate is transient and would
        // heal on its own eventually, but leaving the desktop at 48 Hz until something
        // else resets it is a change the user never made and would have to hunt for.
        try
        {
            if (_refreshPairing is not null && !_refreshPairing.Restore())
            {
                failures.Add(new InvalidOperationException(
                    "The pre-game display refresh rate could not be restored."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Restoring the pre-game display refresh rate during application shutdown failed", ex);
        }
        finally
        {
            _refreshPairing = null;
        }

        // Same reasoning, and separately owned: a resolution the user picked from the menu
        // is transient too, and leaving the desktop at a game's resolution is the more
        // visible of the two changes to be left with.
        try
        {
            if (_resolutions is not null && !_resolutions.Restore())
            {
                failures.Add(new InvalidOperationException(
                    "The pre-game display resolution could not be restored."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures,
                "Restoring the pre-game display resolution during application shutdown failed", ex);
        }
        finally
        {
            _resolutions = null;
        }

        // After the Steam host and the overlay, both of which hold them.
        try
        {
            if (_audioProfiles is not null)
            {
                await _audioProfiles.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the audio profile service during application shutdown failed",
                ex);
        }
        finally
        {
            _audioProfiles = null;
        }

        try
        {
            _audio?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the audio manager during application shutdown failed", ex);
        }
        finally
        {
            _audio = null;
        }

        try
        {
            _radios?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the radio manager during application shutdown failed", ex);
        }
        finally
        {
            _radios = null;
        }

        // Before the drive manager, whose collection the bridge is subscribed to. The format
        // manager holds no timer or handle to release; its work is a task already cancelled
        // with the session, so only the drive manager is disposed after it.
        try
        {
            _libraryImport?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the library importer during application shutdown failed", ex);
        }
        finally
        {
            _libraryImport = null;
        }

        try
        {
            _artwork?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the artwork browser during application shutdown failed", ex);
        }
        finally
        {
            _artwork = null;
        }

        try
        {
            _steamStorage?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the Steam storage bridge during application shutdown failed",
                ex);
        }
        finally
        {
            _steamStorage = null;
        }

        try
        {
            _drives?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordShutdownFailure(failures, "Disposing the drive manager during application shutdown failed", ex);
        }
        finally
        {
            _drives = null;
        }

        _formats = null;
        _tabBootSyncCancellation.Dispose();
        _shutdownCancellation.Dispose();

        if (!desktopVerified)
        {
            failures.Add(new InvalidOperationException(
                "Application shutdown could not verify a usable Explorer desktop; "
                + "the retained shell anchor will recover after process exit."));
        }

        if (ShutdownFailure(failures) is { } unverified)
        {
            throw unverified;
        }
    }

    /// <summary>Keeps a failed shutdown step for the final report and logs it now.</summary>
    private static void RecordShutdownFailure(List<Exception> failures, string message, Exception ex)
    {
        failures.Add(ex);
        Log.Error(message, ex);
    }

    /// <summary>Reports collected cleanup failures once, or null when every step was verified.</summary>
    /// <remarks>
    ///     The single-failure case keeps that exception as the inner one rather than burying it in a
    ///     one-element aggregate, because the log line a maintainer reads is the inner message. That
    ///     every step still ran is guaranteed by the straight-line shutdown above, which has no early
    ///     return — this only decides how what failed is reported.
    /// </remarks>
    internal static Exception? ShutdownFailure(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0
            ? null
            : new InvalidOperationException(
                "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                failures.Combine("Multiple application shutdown steps were unverified."));
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
        var messageWindow = _messageWindow;
        if (messageWindow is not null)
        {
            messageWindow.SessionEnding -= OnSessionEnding;
            messageWindow.SessionLocked -= OnSessionLocked;
            messageWindow.SessionUnlocked -= OnSessionUnlocked;
            messageWindow.SystemSuspending -= OnSystemSuspending;
            messageWindow.SystemResumed -= OnSystemResumed;
        }

        _overlay?.Dispose();
        _overlay = null;
        _performanceOverlay?.Dispose();
        _performanceOverlay = null;
        _deviceOverlay?.Dispose();
        _deviceOverlay = null;
        _standbyGuard?.Dispose();
        _standbyGuard = null;
        _displayMute?.Dispose();
        _displayMute = null;
        _volumeButtons?.Dispose();
        _volumeButtons = null;
        _cardVolumes?.Dispose();
        _cardVolumes = null;
        _cardAcfWatcher?.Dispose();
        _cardAcfWatcher = null;
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
        // Last: every service above deregisters its own native notification from this window.
        // Destroying the HWND first makes those orderly deregistrations race a dead handle.
        messageWindow?.Dispose();
        _messageWindow = null;
    }

    private void RetireTrayHostForShutdown()
    {
        _settingsActivation?.Dispose();
        _settingsActivation = null;
        _desktopTray?.Dispose();
        _desktopTray = null;
        _activation?.Dispose();
        _activation = null;
        // Every later cleanup is recoverable through process exit. Explorer restoration is not:
        // it must never run beside WSGM's Shell_TrayWnd and create two taskbar owners.
        _trayHost?.Dispose();
        _trayHost = null;
    }

    private async Task<bool> RestoreDesktopBeforeShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline)
    {
        var desktopHost = _desktopHost;
        if (desktopHost is null || reason is ApplicationShutdownReason.SessionEnd)
        {
            return true;
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
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
            if (reason is not ApplicationShutdownReason.Update && _modes is not null)
            {
                SessionModes.ExitBigPicture();
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
            var result = await desktopHost.RestoreDesktopAsync(remaining)
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
        _ = ApplyCommonPluginConfigAsync(config);
        var coordinator = _deviceCoordinator;
        if (coordinator is null)
        {
            return;
        }

        // AutoTDP is applied before the coordinator: turning Device Integration off must stop
        // AutoTDP and restore the previous power limit while the capability is still writable.
        _autoTdp?.Apply(ShouldRunAutoTdp(config.DeviceIntegration));
        Log.Observe(ApplyDeviceConfigAndTargetAsync(coordinator, config), "Device cycle config apply", true);
    }

    private async Task ApplyDeviceConfigAndTargetAsync(DeviceCoordinator coordinator, AppConfig config)
    {
        await coordinator.ApplyConfigAsync(config).ConfigureAwait(false);
        _runningApplicationTargets?.RefreshCurrent();
    }

    private async Task ApplyCommonPluginConfigAsync(AppConfig config)
    {
        try
        {
            if (_commonPlugins is { } manager)
            {
                // Exactly what the user enabled. Nothing is admitted implicitly: the auto-enable pass
                // existed for bundled packages, and WSGM bundles none.
                await manager.ReconcileAsync(config.PluginInstances, _shutdownCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Common plugin configuration failed", ex);
        }
    }

    private async Task ApplyCommonPluginPowerAsync(bool suspend)
    {
        try
        {
            if (_commonPlugins is { } manager)
            {
                await manager.PowerTransitionAsync(suspend, _shutdownCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Common plugin power transition failed", ex);
        }
    }

    /// <summary>Applies the Device Integration master switch to AutoTDP at every entry point.</summary>
    internal static bool ShouldRunAutoTdp(DeviceIntegrationConfig config)
    {
        return config is { Enabled: true, AutoTdpEnabled: true };
    }

    private static bool GlyphsEnabled(AppConfig config)
    {
        return config.Cef.Enabled
               && config.DeviceIntegration.Enabled;
    }

    private void OnPhysicalGlyphProfilesChanged()
    {
        ApplyGlyphConfig(_config);
    }

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
    }

    private void AttachOsdPowerStatus()
    {
        if (_deviceCoordinator is { } coordinator)
        {
            coordinator.Capabilities.Changed += OnOsdPowerCapabilitiesChanged;
            coordinator.ConfigurationChanged += OnOsdPowerConfigurationChanged;
        }

        if (_autoTdp is { } autoTdp)
        {
            autoTdp.StatusChanged += OnOsdAutoTdpStatusChanged;
        }

        UpdateOsdPowerStatus();
    }

    private void DetachOsdPowerStatus()
    {
        _performance?.ApplyOsdPowerStatus(RtssOsdPowerStatus.Empty);
        if (_deviceCoordinator is { } coordinator)
        {
            coordinator.Capabilities.Changed -= OnOsdPowerCapabilitiesChanged;
            coordinator.ConfigurationChanged -= OnOsdPowerConfigurationChanged;
        }

        if (_autoTdp is { } autoTdp)
        {
            autoTdp.StatusChanged -= OnOsdAutoTdpStatusChanged;
        }
    }

    private void OnOsdPowerCapabilitiesChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        UpdateOsdPowerStatus(views);
    }

    private void OnOsdPowerConfigurationChanged()
    {
        UpdateOsdPowerStatus();
    }

    private void OnOsdAutoTdpStatusChanged(AutoTdpStatus status)
    {
        UpdateOsdPowerStatus();
    }

    private void UpdateOsdPowerStatus(IReadOnlyList<DeviceCapabilityView>? views = null)
    {
        var performance = _performance;
        var coordinator = _deviceCoordinator;
        if (performance is null || coordinator is null)
        {
            return;
        }

        var tdp = DeviceCoordinatorNativeQamTdpService
            .Project(views ?? coordinator.Capabilities.Snapshot()).State;
        var autoTdp = _autoTdp?.Status;
        var enabled = _autoTdp?.Enabled ?? false;
        var running = enabled && autoTdp?.State is AutoTdpState.Controlling;
        var reportedTdpWatts = tdp.Available
            ? tdp.ObservedWatts ?? tdp.DesiredWatts
            : null;
        var tdpWatts = enabled && autoTdp?.Watts is { } automaticWatts
            ? automaticWatts
            : reportedTdpWatts;
        performance.ApplyOsdPowerStatus(new RtssOsdPowerStatus(
            tdpWatts,
            enabled,
            running,
            running ? autoTdp?.Watts : null,
            running ? AutoTdpActivity(enabled, autoTdp) : string.Empty));
    }

    internal static string AutoTdpActivity(bool enabled, AutoTdpStatus? status)
    {
        if (!enabled)
        {
            return string.Empty;
        }

        if (status is null || status.State is AutoTdpState.Off)
        {
            return "Starting";
        }

        return status.State switch
        {
            AutoTdpState.Unavailable => "Unavailable",
            AutoTdpState.Idle => "Waiting",
            AutoTdpState.Paused => "Paused",
            AutoTdpState.Controlling when status.Detail is "at-maximum" => "Can't Reach",
            AutoTdpState.Controlling when status.Detail is "settling" or "settling-headroom" =>
                "Settling",
            AutoTdpState.Controlling when status.Detail is "probe-pending" => "Testing",
            AutoTdpState.Controlling => status.Action switch
            {
                AutoTdpAction.Raise => "Raising",
                AutoTdpAction.Probe => "Lowering",
                AutoTdpAction.Restore => "Restoring",
                _ => "Holding"
            },
            _ => "Starting"
        };
    }

    /// <summary>
    ///     The deadline AutoTDP judges frame delivery against.
    /// </summary>
    /// <remarks>
    ///     Only a verified, active RTSS limit supplies a deadline. Zero means no control is permitted;
    ///     a desired value or a default 60 Hz target cannot stand in for an active limiter.
    /// </remarks>
    private double TargetFrametimeMs()
    {
        return AutoTdpService.TargetFrametime(_performance?.Current);
    }

    /// <summary>
    ///     Applies both halves of physical glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <remarks>
    ///     The selector alone changes nothing a user can see. Without the resolved profile the
    ///     stylesheet has no rules, and the patch refuses to install an empty one — which is how
    ///     physical glyphs were inert.
    /// </remarks>
    private void ApplyGlyphConfig(AppConfig config)
    {
        var steamUi = _steamUi;
        if (steamUi is null)
        {
            return;
        }

        var enabled = GlyphsEnabled(config);
        var nativeArtwork = config.DeviceIntegration.GlyphSelection is DeviceGlyphSelection.NativeSteam;
        steamUi.ApplyGlyphs(
            enabled,
            enabled
                ? nativeArtwork
                    ? _deviceCoordinator?.PhysicalControlSelectionSnapshot().Profile
                    : _deviceCoordinator?.PhysicalGlyphSelectionSnapshot().Profile
                : null,
            nativeArtwork);
    }

    private void ApplyPerformanceConfig(AppConfig config)
    {
        var performance = _performance;
        if (performance is null)
        {
            return;
        }

        _refreshPairing?.SetStrategy(config.Performance.FrameLimitStrategy);
        performance.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(config.Performance));
        Log.Observe(
            performance.ApplyProfilesAsync(_profiles.Current, PerformanceEnabled(config)),
            "RTSS performance config apply",
            true);
    }

    /// <remarks>
    ///     Runs off the state event rather than inside <see cref="PerformanceService" />, because that
    ///     service owns RTSS profiles and this changes a display mode — two different pieces of hardware
    ///     with different failure modes and different restore obligations.
    ///     <para>
    ///         Only an actual change is acted on. The state event fires for any performance change, and
    ///         re-applying the same mode on each would put a driver round trip behind every one of them.
    ///     </para>
    /// </remarks>
    private void OnPerformanceStateForPairing(PerformanceState state)
    {
        if (_autoTdp is { } autoTdp)
        {
            autoTdp.RefreshPrerequisites();
            var limiterOff = state.Desired.FrameLimit == 0
                             || (state.FrameLimitQuality is PerformanceReadbackQuality.Verified &&
                                 state.Observed.FrameLimit == 0);
            if (limiterOff && _deviceCoordinator is { AutoTdpEnabled: true } coordinator)
            {
                Log.Observe(coordinator.SetAutoTdpEnabledAsync(false), "AutoTDP limiter disabled");
            }
            else if (autoTdp.Availability.Available && ShouldRunAutoTdp(_config.DeviceIntegration))
            {
                autoTdp.Apply(true);
            }
        }

        if (_refreshPairing is not { } pairing)
        {
            return;
        }

        var limit = state.Desired.FrameLimit ?? 0;
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

    /// <summary>Whether WSGM may change RTSS. Overlay-test always may, against its simulated adapter.</summary>
    private bool PerformanceEnabled(AppConfig config)
    {
        return _overlayTestOnly || config.Performance.Enabled;
    }

    /// <summary>Saves a profile edit under the cross-process configuration lock, on a worker.</summary>
    private static Task<ProfileConfig> MutateProfilesAsync(Func<ProfileConfig, bool> edit,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            ProfileConfig? stored = null;
            ConfigStore.Mutate(config =>
            {
                edit(config.Profiles);
                stored = config.Profiles.Copy();
            });
            return stored!;
        }, cancellationToken);
    }

    /// <summary>An in-memory profile store for overlay-test.</summary>
    private Func<Func<ProfileConfig, bool>, CancellationToken, Task<ProfileConfig>> MutateSimulatedProfilesAsync()
    {
        var store = _config.Profiles.Copy();
        var gate = new Lock();
        return (edit, _) =>
        {
            lock (gate)
            {
                edit(store);
                return Task.FromResult(store.Copy());
            }
        };
    }

    /// <summary>Starts the one queue that carries profile changes to every consumer, in order.</summary>
    /// <remarks>
    ///     RTSS first because it is cheap and the overlay shows it; then the device's desired values,
    ///     fan profile and controller target; then the power limit and refresh preference, which read
    ///     the device's published capabilities.
    /// </remarks>
    private void StartProfileFanOut()
    {
        _profileFanOut = new ProfileFanOut(_profiles,
        [
            new ProfileConsumer("RTSS", (snapshot, token) => _performance is { } performance
                ? performance.ApplyProfilesAsync(snapshot, PerformanceEnabled(_config), token)
                : Task.CompletedTask),
            new ProfileConsumer("device", (snapshot, token) => _deviceCoordinator is { } coordinator
                ? coordinator.ApplyProfilesAsync(snapshot, token)
                : Task.CompletedTask),
            new ProfileConsumer("power and refresh", (snapshot, token) =>
                _applicationProfiles.ReconcileApplicationProfileAsync(snapshot, token))
        ]);
        _profileFanOut.Queue(_profiles.Current, ProfileChangeKind.Application);
    }

    private static async Task ObserveUiCaptureClaimAsync(
        DeviceCoordinator coordinator,
        string surfaceId)
    {
        try
        {
            await coordinator.ClaimUiAsync(surfaceId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Managed controller capture failed for {surfaceId}", ex);
        }
    }

    private async Task LaunchAppsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Before Steam, for the same reason as on the desktop path: a reappeared autostart entry
        // must not be the one that wins the race to start Steam.
        SteamAutostartService.ReapplyAtStart();
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

    /// <summary>
    ///     The session's half of the Game Mode entry transaction. Everything here needs state
    ///     the session owns — the splash, the plugin host, the config lock and the shutdown token — so
    ///     it is a view onto the session rather than a free-standing service.
    /// </summary>
    private sealed class ShellGameModeEntryServices(ShellSession session) : IGameModeEntryServices
    {
        public GameModeLaunchConfiguration ReadLaunch()
        {
            return ConfigStore.Load().GameModeLaunch;
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
                ConfigStore.Mutate(fresh =>
                {
                    fresh.GameModeLaunchRecovery.PendingReturnLayout = layout;
                    fresh.GameModeLaunchRecovery.PendingReturnAudio = audio;
                    fresh.GameModeLaunchRecovery.EnteredAt =
                        layout is null && audio is null ? null : DateTimeOffset.UtcNow;
                });
            });
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
                         ?? ConfigStore.Load().GameModeLaunchRecovery.PendingReturnLayout
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
                        ?? ConfigStore.Load().GameModeLaunchRecovery.PendingReturnAudio;
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
