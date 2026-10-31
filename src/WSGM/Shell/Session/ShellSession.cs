using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using SteamUiToolkit;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Install;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Settings;
using WSGM.Themes;

namespace WSGM.Shell;

/// <summary>
///     Shell-mode orchestrator: starts startup apps and the home app, arms the
///     overlay (hotkey + edge swipes + home-exit), stays resident for the session.
/// </summary>
/// <remarks>
///     The composition root. It builds its parts in the constructor without touching Windows, starts
///     them in one fixed order (the plugin and device set off the UI thread, then everything else on
///     it), wires the events that cross parts, applies config reloads in one fixed order, and runs the
///     one ordered shutdown. Each part owns its own services, their construction and their slice of
///     that shutdown.
/// </remarks>
public sealed class ShellSession
{
    private readonly ApplicationPerformanceReconciler _applicationProfiles;
    private readonly SessionBoot _boot;
    private readonly SessionCommands _commands;
    private readonly CpuBoostService _cpuBoost;
    private readonly DesktopLifecycleActions _desktopActions;
    private readonly bool _desktopResident;
    private readonly DevicePowerQueue _devicePower;

    // The one owner of the display-off timeouts: the overlay's Power page and the rows WSGM adds to
    // Steam's Screensaver settings both edit through it, and it hears Steam's screensaver timeout.
    private readonly DisplayTimeouts _displayTimeouts;

    private readonly GameModeEntryBackend _entryBackend;
    private readonly IntegrationServices _integrations;
    private readonly LibraryServices _library;
    private readonly PerformanceServices _performance;

    /// <summary>
    ///     The session's Windows power policy owners. They share one scheme owner, whose lock serializes
    ///     every machine-wide power change the overlay, Steam's Quick Access and the device presets make.
    /// </summary>
    private readonly WindowsPowerPolicy _power = WindowsPowerPolicy.OverWindows();

    private readonly SessionPowerEvents _powerEvents;
    private readonly NativeQamPowerProfileService _powerProfiles;
    private readonly ProfileService _profiles;
    private readonly bool _serviceBoot;
    private readonly SessionState _state;

    /// <summary>The process's Steam Input lease owner, shared with every surface this session opens.</summary>
    private readonly SteamInputBlocker _steamInput;

    private readonly SteamUiServices _steamUi;
    private readonly StorageServices _storage;
    private readonly GameModeSurfaces _surfaces;
    private readonly SystemServices _system;
    private readonly OverlayTools _tools;
    private readonly bool _verboseLogging;

    private SessionActivation? _activation;
    private ConfigReloadWatcher? _configWatcher;
    private ExplorerDesktopHost? _desktopHost;
    private bool _desktopRecoveryPending;
    private DisplayChangeWindow? _displayChangeWindow;
    private Task _managerStartup = Task.CompletedTask;
    private MessageWindow? _messageWindow;
    private SessionModes? _modes;
    private SteamMonitor? _monitor;
    private OverlayController? _overlay;
    private SettingsActivation? _settingsActivation;
    private SettingsSurface? _settingsSurface;

    // The runtime that owns the one outer deadline, and its deadline token read while the attempt runs.
    // Both are set by the first ShutdownAsync call; before it the deadline is unset and never cancels.
    private CancellationToken _shutdownDeadlineToken;
    private ApplicationRuntime? _shutdownRuntime;

    private Task? _shutdownTask;
    private Task? _startupTask;

    /// <summary>
    ///     The shim reconcile a config reload started, which shutdown waits for; completed while
    ///     none runs.
    /// </summary>
    private Task _steamInputReconcile = Task.CompletedTask;

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="store">The process-owned persistence and data roots.</param>
    /// <param name="steamInput">The process's Steam Input lease owner and, through it, its shim.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">
    ///     Whether the logon service requested takeover (--boot), subject to the current Desktop preference.
    /// </param>
    /// <param name="desktopResident">Whether to remain on Desktop even while its logon shell is still starting.</param>
    /// <param name="verboseLogging">Whether the command line forces verbose logging for this run.</param>
    internal ShellSession(
        AppConfig config,
        ConfigStore store,
        SteamInputBlocker steamInput,
        bool overlayTestOnly = false,
        bool serviceBoot = false,
        bool desktopResident = false,
        bool verboseLogging = false)
    {
        _state = new SessionState(config, store, overlayTestOnly);
        _steamInput = steamInput ?? throw new ArgumentNullException(nameof(steamInput));
        _displayTimeouts = new DisplayTimeouts(_power.Timeouts);
        _powerProfiles = new NativeQamPowerProfileService(_power.Schemes);
        _verboseLogging = verboseLogging;
        _integrations = new IntegrationServices(_state);
        // Overlay-test keeps profile edits in memory: it is a safe UI mode and must never rewrite the
        // user's configuration.
        _profiles = new ProfileService(config.Profiles,
            overlayTestOnly ? new InMemoryProfileStore(config.Profiles).MutateAsync : MutateProfilesAsync);
        // Overlay-test must not write Windows power policy, so it gets no processor boost.
        _cpuBoost = new CpuBoostService(_profiles, overlayTestOnly ? null : _power.CpuBoost);
        _applicationProfiles = new ApplicationPerformanceReconciler(_profiles, () => _integrations.Device,
            _cpuBoost, () => _integrations.Gpu);
        _performance = new PerformanceServices(_state, _profiles, _cpuBoost, _applicationProfiles, _integrations);
        _steamUi = new SteamUiServices(_state, _integrations, _performance, () => _modes,
            () => _library!.SteamUiReady(), () => _storage!.ApplyCardServices(_state.InGameMode),
            () => _library!.LibraryManager);
        _storage = new StorageServices(_state, _steamUi, () => _library!.LibraryManager);
        _library = new LibraryServices(_state, _steamUi);
        _tools = new OverlayTools(_state, _steamUi, _library, _performance);
        _system = new SystemServices(_state);
        _devicePower = new DevicePowerQueue(_integrations, _state);
        _desktopActions = new DesktopLifecycleActions(_state, _integrations, _devicePower, () => _modes);
        _powerEvents = new SessionPowerEvents(_state, _devicePower, _steamUi, _performance, _integrations,
            () => _desktopActions.Queue(false));
        _surfaces = new GameModeSurfaces(_state, _integrations, _steamUi, _storage, _system, () => _overlay,
            () => _messageWindow);
        _boot = new SessionBoot(_state, _steamUi, _surfaces, _desktopActions, () => _modes, () => _monitor,
            () => _overlay, () => _desktopHost);
        _entryBackend = new GameModeEntryBackend(_state, _boot, _integrations, _system, () => _modes,
            () => _desktopHost, () => _monitor, () => _displayChangeWindow);
        _commands = new SessionCommands(_state, _integrations, _performance, _steamUi, () => _overlay,
            () => _modes, () => _monitor);
        _serviceBoot = serviceBoot;
        // boot.json and its launch arguments are a projection from an earlier config read.
        // The current Desktop preference must win even when that projection still says --boot.
        _desktopResident = overlayTestOnly
            ? desktopResident
            : SessionBoot.StartsOnDesktop(config.StartMode, desktopResident);
        if (_desktopResident)
        {
            _surfaces.SetInGameMode(false);
        }
    }

    private DateTimeOffset ShutdownDeadline => _shutdownRuntime?.Deadline ?? DateTimeOffset.MinValue;

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
            if (!_state.OverlayTestOnly)
            {
                using var recoveryBudget = CancellationTokenSource.CreateLinkedTokenSource(_state.ShutdownToken);
                recoveryBudget.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var fingerprint = GameModeReturnRecovery.PendingFingerprint(_state.Store);
                    _desktopRecoveryPending = !await _entryBackend.RestorePendingDesktopAsync(recoveryBudget.Token)
                        .ConfigureAwait(false);
                    if (!_desktopRecoveryPending && ExplorerControl.IsDesktopShellRunning())
                    {
                        GameModeReturnRecovery.ClearRestored(_state.Store, fingerprint);
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
                        _desktopHost ??= new ExplorerDesktopHost(_state.Store.Context);
                        var restored = await _desktopHost
                            .RestoreDesktopAsync(TimeSpan.FromSeconds(15), recoveryBudget.Token)
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

                _state.ShutdownToken.ThrowIfCancellationRequested();
                // A sign-in takeover covers the booting desktop now, while everything below is built on
                // workers; waiting for the takeover itself let Explorer's desktop and taskbar show first.
                if (_serviceBoot && !_desktopResident && !_desktopRecoveryPending)
                {
                    await Dispatcher.UIThread.InvokeAsync(_boot.ShowBootSplashIfEnabled);
                }

                _integrations.StartPlugins(_profiles, _applicationProfiles.PersistManualVariableRefresh);
            }

            coordinator = _state.OverlayTestOnly
                ? null
                : await _integrations.StartDeviceAsync(_profiles, _performance,
                    _applicationProfiles.PersistManualVariableRefresh, _power.Modes).ConfigureAwait(false);
            await _integrations.CommonPluginStartup.ConfigureAwait(false);
            if (_state.ShutdownRequested)
            {
                return;
            }

            if (!_state.OverlayTestOnly)
            {
                await _library.StartEmulatorsAsync().ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_state.ShutdownRequested)
                {
                    return;
                }

                _integrations.AdoptDevice(coordinator);
                coordinatorAdopted = true;
                StartOnUiThread();
            });
        }
        catch (OperationCanceledException) when (_state.ShutdownToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!coordinatorAdopted && coordinator is not null)
            {
                if (_state.ShutdownRequested)
                {
                    await coordinator.ShutdownAsync(
                        IntegrationServices.DeviceShutdownReason(ApplicationShutdownRequest.Current),
                        Deadline.At(ShutdownDeadline)).ConfigureAwait(false);
                }
                else
                {
                    await coordinator.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    ///     Starts one optional owner. A failure is logged and the session carries on without it.
    ///     Essential owners, the message window and the desktop host among them, are started
    ///     directly, so their failure still fails startup and runs the full cleanup.
    /// </summary>
    /// <param name="name">The owner, for the log.</param>
    /// <param name="start">Creates or starts it.</param>
    internal static void TryStart(string name, Action start)
    {
        try
        {
            start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Session startup: {name} is unavailable; the session continues without it", ex);
        }
    }

    private void StartOnUiThread()
    {
        // The session owns the process's one message window: it is built first, handed to every
        // consumer and disposed last on this thread (ShutdownAsync).
        MessageWindow messageWindow = new();
        _messageWindow = messageWindow;
        StartDeviceIntegration(messageWindow);
        TryStart("performance services", StartPerformance);
        var (modes, monitor) = StartSessionServices();
        var overlay = StartOverlay(modes, monitor);
        WireSessionEvents(modes, monitor);

        if (_state.OverlayTestOnly)
        {
            // Paused so a Steam exit can never trigger auto-relaunch/overlay-pop
            // reactions on a dev machine ("no apps started" contract); IsAlive
            // still updates for the HomeAppAlive display.
            monitor.Paused = true;
            Log.Info("Overlay test mode (no apps started).");
            overlay.ShowOverlay();
            return;
        }

        _system.StartShellServices(messageWindow, _performance, _integrations.Device);

        // Once the user let Full mode turn the other handheld managers off, keep them off: Handheld
        // Companion's uninstaller re-enables the maker's services, and the Armoury Crate helper then
        // answers the Armoury Crate button with an install dialog. Off the boot path: it reads the task
        // scheduler and waits for windows to close. The same owned task rechecks every five minutes
        // so replacement Acer packages do not regain activation while the session is running.
        // The boot manifest refresh and the update-failure note run first on the same worker, because
        // the config read takes the cross-process mutex a Settings save can hold.
        var managerStartupCancellation = _state.ShutdownToken;
        _managerStartup = Task.Run(async () =>
        {
            // Refresh boot.json every session start so a stale Elevate/ExePath heals
            // itself before the next sign-in.
            TryStart("boot manifest refresh",
                () => BootManifestWriter.WriteCurrent(_state.Store.Read(), _state.Store.Context));
            TryStart("update failure report", () =>
            {
                if (UpdateFailure.Read() is { } updateFailure)
                {
                    Log.Warn("Last in-app update: " + updateFailure);
                }
            });
            await OtherManagers.MaintainAsync(_state.Store, managerStartupCancellation).ConfigureAwait(false);
        });
        Log.Observe(_managerStartup, "Other-manager startup reconciliation", true);

        // Service boot: the service launches WSGM at WTS_SESSION_LOGON — usually
        // BEFORE Winlogon has even started explorer (device-observed 2026-08-07:
        // gating this on a running Explorer made the takeover never run, leaving
        // explorer alive behind Big Picture next to our tray host). The takeover
        // owns every explorer state when current preferences still select Game: its readiness
        // poll waits for explorer to appear AND finish logon prep, then shuts it down cleanly; if explorer
        // never shows within the 60 s cap it proceeds like a plain game-mode boot.
        if (_serviceBoot && !_desktopResident && !_desktopRecoveryPending)
        {
            _boot.CoverForTakeover();
            WatchStartupAppsAndConfig();
            _boot.RunTakeover();
            return;
        }

        if (_desktopRecoveryPending || _desktopResident || ExplorerControl.IsDesktopShellRunning())
        {
            // A live desktop at --shell start is either the sign-in start of a Desktop session,
            // the update restart (updates only run in desktop mode), or a manual start next to a
            // desktop. Resume in desktop mode — no splash, no startup apps, no game posture/scale
            // — with the overlay armed and Steam supplied; EnterGameMode brings the rest back.
            Log.Info("Shell starting in desktop mode (overlay armed); verifying Explorer before Steam.");
            _surfaces.StartOnDesktop();
            // A desktop-resident launch can arrive before Winlogon creates Explorer. Hold the
            // same gate as ordinary mode switches until the desktop is verified, so a Game Mode
            // request cannot race a late desktop Steam start.
            monitor.Paused = true;
            modes.BeginTransition();
            WatchStartupAppsAndConfig();
            _boot.StartDesktopSteam();
            return;
        }

        // Boot recomputes the posture value, so game mode re-applies it each start.
        // Posture first: it changes the display scale, and the splash sizes itself
        // to the final screen metrics.
        modes.ApplyGameModePosture();
        try
        {
            _surfaces.Enter();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No Game Mode without its tray: bring the desktop back and stay resident on it,
            // rather than failing startup with no shell at all.
            Log.Error("Game Mode boot could not create its surfaces; restoring the desktop", ex);
            WatchStartupAppsAndConfig();
            _boot.BeginDesktopModeAfterBootFailure();
            return;
        }

        _boot.ShowBootSplashIfEnabled();
        WatchStartupAppsAndConfig();
        _boot.StartLaunchSequence();
    }

    /// <summary>
    ///     Follows the message window's session and power notifications and the display topology, then
    ///     creates the Device overlay source, or the simulated one in overlay-test mode.
    /// </summary>
    /// <param name="messageWindow">The process's message window.</param>
    private void StartDeviceIntegration(MessageWindow messageWindow)
    {
        // The resident shell is the sole device-cycle authority. Overlay test deliberately never
        // creates this object, discovers packages, or loads plugin code.
        if (!_state.OverlayTestOnly)
        {
            messageWindow.SessionEnding += OnSessionEnding;
            _powerEvents.Attach(messageWindow);
            // A separate top-level window: WM_DISPLAYCHANGE is broadcast to top-level windows
            // only, so the message-only window above never hears a monitor appear.
            try
            {
                _displayChangeWindow = new DisplayChangeWindow();
                _displayChangeWindow.DisplaysChanged += OnDisplayTopologyChanged;
            }
            catch (InvalidOperationException ex)
            {
                // The arrival wait falls back to polling, which is correct, just slower.
                Log.Warn("Display-change window unavailable: " + ex.Message);
            }
        }

        _integrations.StartDeviceOverlay();
    }

    /// <summary>
    ///     Creates RTSS with its overlay projection and, in the real shell, the refresh pairing, the Steam
    ///     UI transport gate and the running-application target, in that order.
    /// </summary>
    private void StartPerformance()
    {
        _performance.StartRtss();
        if (_state.OverlayTestOnly)
        {
            return;
        }

        _performance.StartDisplayPairing();
        // The transport was created closed with the session; the gate decides when it opens.
        _steamUi.StartTransportGate();
        _performance.StartRunningApplications(
            _steamUi.Client ?? throw new InvalidOperationException("The Steam client was not created."));
    }

    /// <summary>
    ///     Creates the Steam monitor, session modes, keep-awake and, in the real shell, the audio, radio,
    ///     storage and library services Steam's surfaces use.
    /// </summary>
    /// <returns>The mode coordinator and the Steam monitor it watches.</returns>
    private (SessionModes Modes, SteamMonitor Monitor) StartSessionServices()
    {
        var monitor = _monitor = new SteamMonitor();
        if (!_state.OverlayTestOnly)
        {
            _desktopHost ??= new ExplorerDesktopHost(_state.Store.Context);
        }

        var modes = _modes = _desktopHost is null
            ? new SessionModes(_state.Config, monitor)
            : new SessionModes(_state.Config, monitor, _desktopHost, _state.Store, _steamInput.Shim,
                new SessionModeHooks(
                    _entryBackend,
                    _steamUi.PrepareForBigPictureAsync,
                    _steamUi.PrepareForDesktopAsync,
                    () => Dispatcher.UIThread.Post(_steamUi.ReleaseBigPictureHold),
                    () => Dispatcher.UIThread.Post(_boot.SettleEntrySplash),
                    () => _boot.DismissSplash("desktop restored"),
                    () => _state.InGameMode,
                    _state.ShutdownToken));

        modes.SteamStartFailed += _ => Dispatcher.UIThread.Post(() =>
            _boot.DismissSplash("session transition warning"));
        _system.StartKeepAwake(monitor, _steamUi);
        // --overlay-test shares the Settings preview's exposure: it has no boot takeover
        // and no watchdog behind it, so the mode row must not offer a real transition.
        // The services below are started here rather than by the sheet, because Steam's surfaces
        // have to answer while the sheet is closed. Overlay-test lets the sheet's status pills own
        // their own managers, since no Steam surface exists there to serve.
        if (_state.OverlayTestOnly)
        {
            return (modes, monitor);
        }

        _system.StartAudioAndRadios();
        TryStart("storage services", () => _storage.Start(_messageWindow));
        _library.StartPersonalization(() => _monitor?.IsAlive ?? true);
        // WSGM's own settings, from its row in Steam's main menu.
        TryStart("Steam settings page", () => _steamUi.StartSettingsPage(_steamInput));
        _library.StartGameLibrary(_profiles, _storage, _messageWindow);
        return (modes, monitor);
    }

    private static Task<DisplayModeSnapshot?> ReadOverlayDisplayModeAsync(string? sourceName)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }

            var path = DisplayTopology.CaptureActive().Paths.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
            if (path is null)
            {
                return null;
            }

            var snapshot = DisplayModes.Read(path.Target);
            return snapshot?.Path == path ? snapshot : null;
        });
    }

    /// <summary>Creates the overlay controller with its sources and routes managed controller input to WSGM's own surfaces.</summary>
    /// <param name="modes">The session's mode coordinator.</param>
    /// <param name="monitor">The session's Steam monitor.</param>
    /// <returns>The overlay controller.</returns>
    private OverlayController StartOverlay(SessionModes modes, SteamMonitor monitor)
    {
        if (!_state.OverlayTestOnly)
        {
            _system.StartBrightness();
        }

        _integrations.StartGraphicsOverlay();
        _tools.Start();
        var overlay = _overlay = new OverlayController(
            _state.Config,
            _state.Store,
            _steamInput,
            monitor,
            modes,
            _system.KeepAwake,
            _state.OverlayTestOnly,
            new OverlaySources(
                _integrations.DeviceOverlay,
                _performance.OverlayBridge,
                _integrations.StartPluginOverlay(),
                _state.OverlayTestOnly
                    ? null
                    : new DevicePrerequisiteSource(_integrations.ReadDevicePrerequisiteState),
                _system.Brightness,
                _integrations.Device,
                _library.GameLibrary,
                _library.Themes,
                _library.Animations,
                _integrations.GraphicsOverlay,
                _library.Sounds,
                _library.Artwork,
                _state.OverlayTestOnly
                    ? DisplayModeAccess.Unavailable
                    : new DisplayModeAccess(ReadOverlayDisplayModeAsync,
                        (snapshot, mode) => Task.Run(() => DisplayModes.Apply(snapshot, mode, persist: true))),
                _powerProfiles,
                _library.EmulatorTool,
                _tools.Programs,
                _library.LaunchActions,
                _library.LibraryManager,
                Notes: _tools.Notes,
                Screensavers: _tools.Screensavers,
                Browser: _tools.Browser),
            _system.Audio,
            _system.AudioProfiles,
            _system.Radios,
            _integrations.Device?.Power.PowerPresets,
            _integrations.Device?.Power.PowerAssignments,
            _storage.Drives,
            _storage.Formats,
            _displayTimeouts,
            _messageWindow,
            _power);
        overlay.ShowOnScreenKeyboard = _commands.ShowOnScreenKeyboardAsync;
        overlay.SteamClient = _steamUi.Client;
        if (!_state.OverlayTestOnly)
        {
            overlay.RequestPowerAction = PowerActions.Request;
            overlay.GameReturn = new GameWindowReturn(async (processId, token) =>
                    _state.Config.Cef.Enabled && _steamUi.Transport is { } transport
                                              && await SteamGameWindowActivation.RaiseAsync(transport, processId,
                                                  token),
                _state.ShutdownToken);
        }

        // The one Settings window of this process, for the tray, the sheet and a desktop Settings launch
        // alike; the overlay test keeps its sheet's Settings row too. It runs in this process, so its action
        // lists offer what is actually running.
        var settings = _settingsSurface = new SettingsSurface(
            read => SettingsViewModel.FromLoadedConfig(read, _state.Store, _steamInput.Shim,
                _integrations.ReadPluginActionOptions,
                readDisplayGpu: () => _integrations.Gpu?.DisplayCapabilities() ?? [],
                scaledResolutions: _integrations.ScaledResolutions),
            _state.Store,
            _steamInput,
            () => _state.InGameMode,
            () => _integrations.Device?.Controllers.Forwarding.UiPad);
        overlay.OpenSettings = settings.OpenAsync;
        if (!_state.OverlayTestOnly)
        {
            // The tray menu and the activation receivers are conveniences: a failure leaves the
            // session running without them.
            _surfaces.StartDesktopTray(
                () =>
                {
                    if (!_state.ShutdownRequested)
                    {
                        overlay.ShowOverlay();
                    }
                },
                () =>
                {
                    if (!_state.ShutdownRequested)
                    {
                        modes.EnterGameMode();
                    }
                },
                () =>
                {
                    if (!_state.ShutdownRequested)
                    {
                        Log.Observe(settings.OpenAsync(), "Settings open from the tray", true);
                    }
                },
                () =>
                {
                    ApplicationShutdownRequest.Request(ApplicationShutdownReason.Normal);
                    _ = ((App)Application.Current!).Runtime.RequestExit();
                });
            TryStart("overlay activation", () => _activation = new SessionActivation(() =>
            {
                if (!_state.ShutdownRequested)
                {
                    overlay.ShowOverlay();
                }
            }));
            TryStart("Settings activation", () => _settingsActivation = new SettingsActivation(() =>
            {
                if (!_state.ShutdownRequested)
                {
                    Log.Observe(settings.OpenAsync(), "Settings open from a desktop launch", true);
                }
            }));
        }

        // The sheet is recreated per open, so its one-time cost — compiled-XAML populate JIT for
        // the process's largest window — lands on the user's first swipe (~1.5 s on the Claw).
        // Pay it at idle instead; every later open constructs against warm code.
        Dispatcher.UIThread.Post(
            overlay.WarmUp,
            DispatcherPriority.ApplicationIdle);

        if (_integrations.Device is not { } controllers)
        {
            return overlay;
        }

        overlay.UiSurfaceOpened += surfaceId =>
            _ = ObserveUiCaptureClaimAsync(controllers, surfaceId);
        overlay.UiSurfaceClosed += controllers.ReleaseUi;

        // WSGM's own navigation reads the managed controller while management is active and SDL
        // otherwise; the overlay's gamepad poll switches by itself.
        overlay.UseManagedPad(controllers.Controllers.Forwarding.UiPad);
        _system.FollowControllers(controllers.Controllers);
        return overlay;
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

    /// <summary>Connects OEM actions, the card badge, Steam's surfaces and the mode and Steam lifecycle events.</summary>
    /// <param name="modes">The session's mode coordinator.</param>
    /// <param name="monitor">The session's Steam monitor.</param>
    private void WireSessionEvents(SessionModes modes, SteamMonitor monitor)
    {
        _integrations.Device?.ConfigureOemActions(_commands.OemActions());
        if (!_state.OverlayTestOnly)
        {
            _storage.StartBadge();
            TryStart("Steam UI services", StartSteamUiHost);
        }

        // The tray host must never coexist with explorer's taskbar (Z-order war
        // over FindWindow, see TrayHost): gone before explorer starts, back
        // once game mode has closed it.
        modes.DesktopModeStarting += _surfaces.RetireForDesktop;
        modes.GameModeEntered += _surfaces.EnterFromDesktop;
        // A fresh Steam start while WSGM keeps running (client update, crash restart)
        // wipes the injected tabs and the resident badge with the old CEF session —
        // re-inject once the new UI is up.
        monitor.SteamStarted += () =>
        {
            if (_state.ShutdownRequested)
            {
                return;
            }

            _steamUi.RequestGateCheck();
            // Steam rebuilds its registrations on restart in both desktop and game mode.
            _storage.KickCardVolumes("Steam started");
            if (!_state.InGameMode)
            {
                return;
            }

            _steamUi.TabBootSync.Kick();
        };
        // Steam leaving in game mode closes the transport gate at once, so a restart's
        // fresh, still-headless CEF session cannot be connected before its own Big
        // Picture window exists.
        monitor.SteamExited += _steamUi.RequestGateCheck;
        monitor.ClientStarted += () =>
        {
            if (!_state.ShutdownRequested)
            {
                _library.Animations?.SteamStarted();
            }
        };
    }

    /// <summary>
    ///     Builds every backend behind Steam's surfaces and starts the host over them. The session's
    ///     parts own the backends and dispose them after the host, which only reads them.
    /// </summary>
    private void StartSteamUiHost()
    {
        _steamUi.StartProjections(_commands.SwitchToDesktopFromSteamAsync);
        SteamUiBackends backends = new()
        {
            Performance = _performance.Service!,
            Profiles = _profiles,
            Brightness = _system.Brightness
                         ?? throw new InvalidOperationException("Brightness was not created."),
            Folds = new QuickAccessFolds(_state.Store.Context),
            PowerProfiles = _powerProfiles,
            HybridCores = new NativeQamHybridCoreService(_power.HybridCores),
            DeviceCoordinator = _integrations.Device,
            PerfSupport = _performance.ReadNativeQamPerfSupport,
            ApplyRefreshRate = _performance.ApplyManualRefreshRate,
            // Null when nothing can publish VRR, which is also when the projection omits
            // is_vrr_supported and Valve's row does not render. One fact, one source. The
            // user-facing wrapper persists the state to the per-application layer in force; the
            // bare ApplyVariableRefreshRateAsync stays the profile restore's write. A graphics
            // package publishes it whether or not device integration runs.
            ApplyVariableRefreshRate = _integrations.Device is null && _integrations.Gpu is null
                ? null
                : _applicationProfiles.SetVariableRefreshRateFromUserAsync,
            Audio = _system.Audio,
            AudioProfiles = _system.AudioProfiles,
            Radios = _system.Radios,
            ShowBluetoothPanel = () => _overlay?.ShowBluetoothPanel() == true,
            Resolution = _performance.Resolutions,
            Storage = _storage.SteamStorage,
            DisplayTimeouts = _displayTimeouts,
            PluginSteamUi = _steamUi.PluginSource,
            Artwork = _library.ArtworkPage,
            LibraryImport = _library.GameLibrary,
            Emulators = _library.EmulatorTool,
            LibraryManager = _library.LibraryManager,
            Notes = _tools.Notes,
            Screensavers = _tools.Screensavers,
            LaunchActions = _library.LaunchActions,
            ScaledResolutions = _integrations.ScaledResolutions,
            WsgmSettings = _steamUi.SettingsPage,
            CpuBoost = _cpuBoost.Available
                ? new NativeQamCpuBoostService(_cpuBoost, _profiles)
                : null,
            ChordMirror = _system.ChordMirror,
            PowerMenu = _steamUi.PowerMenu,
            Themes = _library.Themes,
            Animations = _library.Animations,
            Graphics = _steamUi.Graphics,
            Sounds = _library.Sounds
        };
        _steamUi.StartHost(_commands.ToggleOverlayAsync, backends);
    }

    private void OnDisplayTopologyChanged()
    {
        if (_state.ShutdownRequested)
        {
            return;
        }

        _performance.Resolutions?.InvalidateOptions();
        _integrations.ScaledResolutions?.Invalidate();
        _performance.RefreshPairing?.Invalidate();
        _steamUi.Host?.QueueStatePublication();
        // Steam can rebuild its window for a new display; ask the page whether WSGM is still in it.
        _steamUi.RequestHealthCheck();
        _integrations.RefreshGraphicsTopology();
    }

    private void WatchStartupAppsAndConfig()
    {
        if (_state.ShutdownRequested)
        {
            return;
        }

        _boot.WatchStartupApps();
        WatchConfig();
    }

    private void WatchConfig()
    {
        if (_state.ShutdownRequested || _configWatcher is not null)
        {
            return;
        }

        try
        {
            _configWatcher = new ConfigReloadWatcher(_state.Store, ApplyReloadedConfig);
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher not available: {ex.Message}");
        }
    }

    private void CloseConfigWatcher()
    {
        var watcher = _configWatcher;
        _configWatcher = null;
        watcher?.Dispose();
    }

    /// <summary>
    ///     Applies a configuration the watcher read successfully, on the UI thread, in a fixed order.
    ///     Each step runs on its own, so one that throws is logged and the rest still apply.
    /// </summary>
    /// <param name="config">The new configuration, which becomes the session's live instance.</param>
    private void ApplyReloadedConfig(AppConfig config)
    {
        // One instance for every reader: the overlay, SessionModes and DisplayScale's saved-scale
        // snapshot must not drift onto different AppConfig objects.
        var steamInputManagementWas = _state.Config.SteamInputManagementEnabled;
        _state.Config = config;
        TryApply("log verbosity", () =>
            Log.SetVerbosity(_verboseLogging ? LogVerbosity.Verbose : config.LogVerbosity));
        TryApply("profiles", () =>
            Log.Observe(_profiles.ReloadAsync(_state.ShutdownToken), "Profile config reload", true));
        TryApply("device integration",
            () => _integrations.ApplyConfig(config, _performance.RefreshApplicationTarget));
        TryApply("performance", () => _performance.ApplyConfig(config));
        TryApply("Steam integration", () => _steamUi.ApplyMasterSwitch(config.Cef.Enabled));
        // Every surface switch in one apply. With the master switch off that is all off at once, so
        // Quick Access refuses commands while the retraction runs.
        TryApply("Steam surfaces", _steamUi.ApplySurfaces);
        TryApply("Steam Input management", () =>
            ApplySteamInputManagement(steamInputManagementWas, config.SteamInputManagementEnabled));
        // The artwork settings are in this file too. The browser reads them live, but a page already
        // open still shows the old tabs, and a response the old key earned is still cached against the
        // new one.
        TryApply("artwork", () => _library.Artwork?.ConfigurationChanged());
        TryApply("game library", () => _library.GameLibrary?.ConfigurationChanged(config.GameLibrary));
        TryApply("themes", () => _library.Themes?.ConfigurationChanged());
        TryApply("boot movies", () => _library.Animations?.ConfigurationChanged());
        TryApply("sounds", () => _library.Sounds?.ConfigurationChanged());
        TryApply("WSGM settings page", () => _steamUi.SettingsPage?.ConfigurationChanged());
        TryApply("screen-off mute", () => _system.ApplyMuteConfig(config));
        TryApply("guide chord edits", () => _system.ApplyChordConfig(config));
        TryApply("accent colour", () =>
            AccentPalette.Apply(Application.Current!, AccentPalette.Parse(config.AccentColor)));
        TryApply("session modes", () => _modes?.ApplyConfig(config));
        TryApply("overlay", () => _overlay?.ApplyConfig(config));
        TryApply("programs", () => _tools.Programs?.ConfigurationChanged(config));
        TryApply("browser", () => _tools.Browser?.ApplyOptions(config.Browser));
        TryApply("screensavers", () => _tools.Screensavers?.ConfigurationChanged());
        TryApply("emulator shortcuts", () => _library.EmulatorTool?.RequestShortcutSync());
        TryApply("library manager", () => _library.LibraryManager?.ConfigurationChanged());
        Log.Debug($"Config reloaded at {Log.MinimumLevel} minimum log level.");
        TryApply("startup apps", () => _boot.ApplyStartupApps(config));
        TryApply("keep awake", () => _system.ApplyKeepAwakeConfig(config));
    }

    /// <summary>Runs one reload step; a failure is logged with its name and the later steps still run.</summary>
    /// <param name="step">The step, for the log.</param>
    /// <param name="apply">The step.</param>
    private static void TryApply(string step, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Config reload: applying {step} failed", ex);
        }
    }

    /// <summary>
    ///     Applies a Steam Input Management change that arrived through a
    ///     config reload.
    /// </summary>
    /// <remarks>
    ///     The park/restore rename touches Steam's directory, so it runs off the UI
    ///     thread, and shutdown waits for it. Reconciles are idempotent and serialized inside
    ///     <see cref="SteamInputShim" />, which is what lets the Settings save path and
    ///     this watcher both fire without coordinating. There is no elevation fallback here on
    ///     purpose: the surface that saved the change already ran it through
    ///     <see cref="SteamInputManagement.Apply" />, and a second prompt after a declined one
    ///     would be a new workflow.
    /// </remarks>
    /// <param name="previous">Whether the setting was on in the configuration being replaced.</param>
    /// <param name="enabled">Whether the setting is on in the reloaded configuration.</param>
    private void ApplySteamInputManagement(bool previous, bool enabled)
    {
        if (previous == enabled)
        {
            return;
        }

        var shim = _steamInput.Shim;
        var earlier = _steamInputReconcile;
        _steamInputReconcile = Task.Run(async () =>
        {
            await earlier.ConfigureAwait(false);
            shim.Reconcile(enabled, "settings-change");
        });
    }

    /// <summary>Saves a profile edit under the cross-process configuration lock, on a worker.</summary>
    private Task<ProfileConfig> MutateProfilesAsync(Func<ProfileConfig, bool> edit,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            ProfileConfig? stored = null;
            _state.Store.Update(config =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var changed = edit(config.Profiles);
                stored = ConfigJson.Clone(config.Profiles, ConfigJsonContext.Tolerant.ProfileConfig);
                return changed;
            });
            return stored!;
        }, cancellationToken);
    }

    private void OnSessionEnding()
    {
        var alreadyEnding = ApplicationShutdownRequest.SessionEnding;
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        if (!alreadyEnding)
        {
            Log.Info("Interactive session is ending; requesting bounded session cleanup.");
        }

        // Starts the exit, or tightens the deadline of the one already running.
        _ = ((App)Application.Current!).Runtime.RequestExit();
    }

    /// <summary>Returns the session's single cleanup task; called by the runtime on the UI thread.</summary>
    /// <param name="reason">Shutdown reason; the first call starts cleanup with this reason.</param>
    /// <param name="deadline">
    ///     Outer deadline, kept for the runtime's call contract: the session reads the runtime's own
    ///     deadline, which a later urgent request can tighten.
    /// </param>
    /// <returns>The shared cleanup task, which may fault with accumulated failures or unverified teardown.</returns>
    internal ValueTask ShutdownAsync(ApplicationShutdownReason reason, DateTimeOffset deadline)
    {
        if (_shutdownTask is null)
        {
            var runtime = ((App)Application.Current!).Runtime;
            _shutdownRuntime = runtime;
            _shutdownDeadlineToken = runtime.DeadlineToken;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
            _ = CompleteShutdownAsync(completion, reason);
        }

        return new ValueTask(_shutdownTask);
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion,
        ApplicationShutdownReason reason)
    {
        try
        {
            await RunShutdownAsync(reason).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    /// <summary>Runs session cleanup with the device protocol reason and one outer deadline.</summary>
    /// <remarks>
    ///     One ordered routine: safety first, every step attempted whatever failed before it, and every
    ///     wait bounded by the same deadline. A SessionEnd that arrives meanwhile only tightens the
    ///     runtime's deadline and sets the sticky flag that keeps Explorer from being started.
    /// </remarks>
    private async Task RunShutdownAsync(ApplicationShutdownReason reason)
    {
        // Failures are collected and reported once at the end, so the outer coordinator records the
        // shutdown as unverified without any step having been skipped.
        var steps = new ShutdownSteps(_shutdownDeadlineToken, () => ShutdownDeadline);

        // 0. Stop new work. This runs on the UI thread, where startup builds the owners, so a startup
        //    that has not reached the UI thread yet sees shutdown requested and builds nothing.
        _state.RequestShutdown();
        _library.CloseAdmission(steps);
        // Steam's Quick Access stops reaching the device, AutoTDP and GPU owners before they stop
        // below. No CEF round trip: the patches stay until the host is disposed after device cleanup.
        steps.Step("Closing Steam UI admission failed", _steamUi.CloseHostAdmission);
        _integrations.CloseAdmission(steps);
        steps.Step("Cancelling library tab boot sync failed", _steamUi.TabBootSync.Cancel);
        _system.DetachControllers(steps);
        steps.Step("Closing profile admission failed", _profiles.Close);
        steps.Step("Closing profile fan-out admission failed", () => _performance.FanOut?.Close());
        steps.Step("Cancelling boot takeover failed", _boot.RequestShutdown);
        steps.Step("Closing mode transition admission failed", () => _modes?.RequestShutdown());
        steps.Step("Cancelling session work failed", _state.CancelWork);
        steps.Step("Closing config watcher failed", CloseConfigWatcher);
        _boot.DisposeStartupWatcher(steps);
        steps.Step("Dismissing the boot splash during application shutdown failed",
            () => _boot.DismissSplash("application shutdown"));
        steps.Step("Closing overlay command admission during application shutdown failed", () =>
        {
            _overlay?.Dispose();
            _overlay = null;
        });
        _system.DisposeBrightness(steps);
        steps.Step("Closing the browser during application shutdown failed", _tools.CloseBrowser);

        // 1. Join the startup task and the device power queue, so neither still commands the device when
        //    it stops. An unadopted coordinator is disposed by startup's own finally, and a power
        //    transition that has not started yet never runs: the device stops below either way.
        var powerWork = _devicePower.CloseForShutdown();
        await steps.JoinAsync("Shell startup and device power transition",
            Task.WhenAll(_startupTask ?? Task.CompletedTask, powerWork)).ConfigureAwait(false);
        _library.CloseLateAdmission(steps);

        // 2 and 3. The coordinator's shutdown restores AutoTDP's original limit first, through its still
        //    open capability path, then releases the controller, shows the pad again and stops the device.
        //    If the deadline passes, process exit unloads the in-process runtime while the shell anchor
        //    stays available for owner-loss desktop recovery.
        steps.Step("Detaching AutoTDP status failed", _performance.DetachOsdPowerStatus);
        steps.Step("Detaching physical glyph profiles failed", _steamUi.DetachDevice);
        await _integrations.ShutdownDeviceAsync(steps, reason).ConfigureAwait(false);

        // Work that cannot hold up controller safety, joined after it: profile passes (a hung pass is
        // retained rather than freed), the other-manager reconciliation, and the Steam Input shim and
        // management applies, which rename files in Steam's folder.
        await steps.JoinAsync("Profile work",
                Task.WhenAll(_profiles.Completion, _performance.FanOut?.Completion ?? Task.CompletedTask))
            .ConfigureAwait(false);
        await steps.JoinAsync("Other-manager startup work", _managerStartup).ConfigureAwait(false);
        await steps.JoinAsync("Browser cleanup", _tools.BrowserShutdownCompletion).ConfigureAwait(false);
        await steps.JoinAsync("Steam Input shim reconcile", _steamInputReconcile).ConfigureAwait(false);
        await steps.JoinAsync("Steam Input management apply",
            _steamUi.SettingsPage?.SteamInputApplyCompletion ?? Task.CompletedTask).ConfigureAwait(false);

        // 4. Common plugins, then the graphics router, so no graphics channel is open when it goes.
        await _integrations.StopPluginsAsync(steps).ConfigureAwait(false);

        // 5. Shutdown already refuses new transitions. Let the running one, the boot worker and the
        //    transport gate loop cross their Explorer and UI boundaries before anything they use goes.
        //    One still running at the deadline is left behind; Explorer is then not restored below, and
        //    the retained anchor recovers the desktop after process exit.
        await steps.JoinAsync("The in-flight mode transition",
            _modes?.WaitForTransitionAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await steps.JoinAsync("The boot worker", _boot.Work).ConfigureAwait(false);
        await steps.JoinAsync("The transport gate", _steamUi.TransportGateWork).ConfigureAwait(false);

        // 6. Retire the tray, then the session's event sources. Explorer may only come back once WSGM's
        //    taskbar is verifiably gone.
        var trayRetired = false;
        await steps.StepAsync("Retiring the tray host during application shutdown failed", async () =>
                trayRetired = await Dispatcher.UIThread.InvokeAsync(() => RetireTrayHostForShutdown(steps)))
            .ConfigureAwait(false);

        // 7. Explorer, only when the reason allows it and the tray is gone. Otherwise the retained shell
        //    anchor recovers the desktop after process exit.
        var desktopVerified = false;
        await steps.StepAsync("Restoring the desktop during application shutdown failed", async () =>
            desktopVerified = trayRetired
                              && await RestoreDesktopBeforeShutdownAsync(reason, steps)
                                  .ConfigureAwait(false)).ConfigureAwait(false);
        await steps.StepAsync("Disposing the desktop host during application shutdown failed", async () =>
        {
            var desktopHost = _desktopHost;
            _desktopHost = null;
            if (desktopVerified && !ApplicationShutdownRequest.SessionEnding && desktopHost is not null)
            {
                await desktopHost.DisposeAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        // Owners the Steam host no longer reaches; AutoTDP already stopped with the device.
        await _performance.DisposeRunningApplicationsAsync(steps).ConfigureAwait(false);

        // 8. Steam. The master-switch applies, the Big Picture restore and the tab boot sync started
        //    before shutdown and none starts again; join what is left. The library manager's tab syncs,
        //    order writes and pushes are joined with the rest of its work.
        await steps.JoinAsync("Steam UI and library tab work",
            Task.WhenAll(_steamUi.Work, _steamUi.TabBootSync.Completion)).ConfigureAwait(false);
        await _library.FinishSteamWorkAsync(steps, reason is ApplicationShutdownReason.Uninstall)
            .ConfigureAwait(false);
        await steps.StepAsync("Disposing the Steam UI session during application shutdown failed",
            () => _steamUi.RetractForShutdownAsync(steps)).ConfigureAwait(false);
        _library.DisposeLibraryManager(steps);
        await steps.UiStepAsync("Disposing the Steam graphics and plugin projections during shutdown failed",
            _steamUi.DisposeProjections).ConfigureAwait(false);
        await steps.StepAsync("Disposing the Steam UI transport during application shutdown failed",
            _steamUi.DisposeTransportAsync).ConfigureAwait(false);

        // 9. Feature owners. Synchronous disposals run on the UI thread that built them; stops and joins
        //    are awaited within the deadline.
        await steps.UiStepAsync("UI-owned shell cleanup failed during application shutdown",
            () => DisposeUiOwnedSessionResources(steps)).ConfigureAwait(false);
        await _performance.StopAsync(steps).ConfigureAwait(false);
        await _system.StopAudioAndRadiosAsync(steps).ConfigureAwait(false);
        await _storage.StopFormatsAsync(steps).ConfigureAwait(false);
        await _library.StopImportAsync(steps).ConfigureAwait(false);
        await _tools.StopAsync(steps).ConfigureAwait(false);
        await _library.DisposePersonalizationAsync(steps).ConfigureAwait(false);

        // 10. Native providers: the Steam storage bridge, then the drive manager with its message-window
        //     registration.
        await _storage.DisposeDrivesAsync(steps).ConfigureAwait(false);

        // 11. The message window last: until here a session end still sets the sticky SessionEnd flag.
        await steps.UiStepAsync("Disposing the message window during shutdown failed", DisposeMessageWindow)
            .ConfigureAwait(false);

        // The cancellation source stays undisposed: work left behind at the deadline may still read its
        // token, and process exit follows.
        if (!desktopVerified && reason is not ApplicationShutdownReason.SessionEnd
                             && !ApplicationShutdownRequest.SessionEnding)
        {
            steps.Record(new InvalidOperationException(
                "Application shutdown could not verify a usable Explorer desktop; "
                + "the retained shell anchor will recover after process exit."));
        }

        steps.ThrowIfUnverified();
    }

    private void DisposeUiOwnedSessionResources(ShutdownSteps steps)
    {
        _boot.ReleaseSplash();
        _performance.DisposeOverlayBridge(steps);
        _integrations.DisposeOverlaySources(steps);
        _system.DisposeShellServices(steps);
        _storage.DisposeCardServices(steps);
        steps.Step("Disposing _displayChangeWindow failed", () =>
        {
            if (_displayChangeWindow is { } displayWindow)
            {
                displayWindow.DisplaysChanged -= OnDisplayTopologyChanged;
            }

            _displayChangeWindow?.Dispose();
            _displayChangeWindow = null;
        });
        _system.DisposeKeepAwake(steps);
        steps.DisposeOwner("Steam monitor", ref _monitor);
    }

    /// <summary>
    ///     Disposes the process's one message window, last. Its power notifications are refused since
    ///     shutdown began, and its session-end notification keeps setting the sticky SessionEnd flag until
    ///     here.
    /// </summary>
    private void DisposeMessageWindow()
    {
        if (_messageWindow is not { } messageWindow)
        {
            return;
        }

        _messageWindow = null;
        messageWindow.SessionEnding -= OnSessionEnding;
        _powerEvents.Detach(messageWindow);
        messageWindow.Dispose();
    }

    private bool RetireTrayHostForShutdown(ShutdownSteps steps)
    {
        steps.DisposeOwner("Settings activation", ref _settingsActivation);
        _surfaces.DisposeDesktopTray(steps);
        // A save still running finishes before the window closes itself; the session's own exit
        // (application shutdown) is never held up by it.
        steps.Step("Disposing Settings window failed", () =>
        {
            _settingsSurface?.Close();
            _settingsSurface = null;
        });
        steps.DisposeOwner("session activation", ref _activation);
        return _surfaces.RetireTrayHost(steps);
    }

    private async Task<bool> RestoreDesktopBeforeShutdownAsync(
        ApplicationShutdownReason reason,
        ShutdownSteps steps)
    {
        var desktopHost = _desktopHost;
        if (ApplicationShutdownRequest.SessionEnding || reason is ApplicationShutdownReason.SessionEnd)
        {
            return false;
        }

        if (desktopHost is null)
        {
            return true;
        }

        var remaining = ShutdownDeadline - DateTimeOffset.UtcNow;
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
            if (!ApplicationShutdownRequest.SessionEnding
                && ApplicationShutdownRequest.Current is not ApplicationShutdownReason.Update
                && _modes is not null)
            {
                SteamLifecycle.ExitBigPicture();
            }

            DisplayScale.ApplyDesktopMode(_state.Store, _state.Store.Read().RequireConfig());
        }
        catch (Exception ex)
        {
            // Explorer recovery is the higher-priority safety boundary. Program's final posture
            // cleanup gets another chance after Avalonia exits.
            Log.Error("Preparing desktop posture during application shutdown failed", ex);
        }

        remaining = ShutdownDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        var stateRestored = false;
        string? pending = null;
        try
        {
            pending = GameModeReturnRecovery.PendingFingerprint(_state.Store);
            stateRestored = await _entryBackend.RestorePendingDesktopAsync(steps.Token).ConfigureAwait(false);
            if (!stateRestored)
            {
                steps.Record(
                    new InvalidOperationException("The recorded desktop display or audio state remains pending."));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            steps.RecordFailure("Restoring recorded desktop display and audio state failed", ex);
        }

        try
        {
            if (ApplicationShutdownRequest.SessionEnding)
            {
                return false;
            }

            remaining = ShutdownDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var result = await desktopHost
                .RestoreDesktopAsync(remaining, steps.Token)
                .ConfigureAwait(false);
            if (stateRestored && pending is not null
                              && result.Outcome is ExplorerDesktopOutcome.Normal or ExplorerDesktopOutcome.Degraded)
            {
                GameModeReturnRecovery.ClearRestored(_state.Store, pending);
            }

            return result.Outcome is ExplorerDesktopOutcome.Normal
                or ExplorerDesktopOutcome.Degraded;
        }
        catch (Exception ex)
        {
            Log.Error("Application shutdown Explorer desktop recovery failed", ex);
            return false;
        }
    }
}
