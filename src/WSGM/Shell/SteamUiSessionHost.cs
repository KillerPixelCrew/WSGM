using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Owns the narrow bridge and registered patches over the injected process-long Steam UI transport.
/// </summary>
/// <remarks>
///     Session lifetime only: which patches are applied when, generation changes, synchronization, and
///     publication gating. The surfaces themselves — what each gate installs, which rows mount, how a
///     payload is read — are the toolkit's; this host feeds them WSGM's data through the backend
///     services and decides which are on.
/// </remarks>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    private const string ShellPatchId = "wsgm.native-qam.shell";
    private readonly AnimationService? _animations;

    /// <summary>The artwork browser behind Steam's Change Artwork page, or null.</summary>
    private readonly SteamArtworkBrowserSource? _artwork;

    /// <summary>
    ///     Null when no audio manager exists for this session.
    /// </summary>
    /// <remarks>
    ///     Unlike the semantic services above there is no "unavailable" stand-in, because audio is
    ///     supplied as a namespace rather than drawn as a row: with nothing to supply, the right
    ///     behaviour is to leave the namespace absent so Steam's own store stays unavailable, not to
    ///     install one that answers with nothing.
    /// </remarks>
    private readonly AudioManagerNativeQamAudioService? _audio;

    private readonly NativeQamAudioFormatService? _audioFormat;

    private readonly DeviceCoordinatorNativeQamAutoTdpService _autoTdp;

    /// <summary>The Bluetooth surface, riding the same radio-manager condition.</summary>
    private readonly NativeQamBluetoothService? _bluetooth;

    private readonly SteamUiBridgeHost _bridge;
    private readonly NativeQamBrightnessService _brightness;
    private readonly SteamGuideChordMirror? _chordMirror;
    private readonly DeviceCoordinatorNativeQamControllerTargetService _controllerTarget;

    /// <summary>The device platform the device rows project, or null when integration is off.</summary>
    private readonly DeviceCoordinator? _coordinator;

    /// <summary>The processor boost row's backend, or null when this session cannot write Windows power policy.</summary>
    private readonly NativeQamCpuBoostService? _cpuBoost;

    private readonly DeviceCoordinatorNativeQamDeviceControlsService _deviceControls;

    /// <summary>The session's display-off timeouts, shared with the overlay, or null without one.</summary>
    private readonly DisplayTimeouts? _displayTimeouts;

    // Serializes the start of disposal; every caller gets the one disposal task.
    private readonly Lock _disposeGate = new();

    /// <summary>The Quick Access plugin tab, which carries WSGM's own tools as well.</summary>
    private readonly SteamExtensionsTabBackend _extensionsTab;

    private readonly SteamGameContextMenuBackend _gameContextMenu;

    private readonly SteamInputGlyphDeliveryState _glyphDeliveryState = new();
    private readonly SteamGraphicsService? _graphics;

    /// <summary>Hears what Big Picture Home's carousel holds.</summary>
    private readonly HomeCarouselBackend _homeCarousel = new();

    // The modules this host declares itself; the ready plugins' modules are composed beside them.
    private readonly IReadOnlyList<ISteamUiModule> _hostModules;

    private readonly NativeQamHybridCoreService _hybridCores;

    /// <summary>Hears the library badge's Home layout report.</summary>
    private readonly LibraryBadgeBackend _libraryBadge = new();

    /// <summary>The library importer behind the Quick Access tab's page, or null.</summary>
    private readonly GameLibraryService? _libraryImport;

    private readonly SteamNativeSettingsService _nativeSettings;
    private readonly Timer _nativeStateRefresh;

    /// <summary>The Wi-Fi surface, or null when this session has no radio manager.</summary>
    private readonly NativeQamNetworkService? _network;

    private readonly SteamOverlayActivationPatch _overlayActivation = new();

    /// <summary>Which Quick Access sections the user opened.</summary>
    private readonly SteamPanelFoldsBackend _panelFolds;

    private readonly SteamUiPatchManager _patches;
    private readonly PerformanceServiceNativeQamAdapter _performance;

    private readonly PerformanceService _performanceService;

    // Serializes plugin module replacement; _composedPluginModules is the list the runtime holds.
    private readonly SemaphoreSlim _pluginModulesChange = new(1, 1);
    private readonly CommonPluginSteamUiSource? _pluginSteamUi;
    private readonly SteamPowerMenuBackend? _powerMenu;
    private readonly NativeQamPowerPresetService _powerPresets;

    private readonly NativeQamPowerProfileService _powerProfiles;

    private readonly ProfileService _profiles;

    /// <summary>
    ///     The display-resolution row's backend, or null when this session must not move the display.
    /// </summary>
    /// <remarks>
    ///     The patch is not registered at all then, so the row cannot appear and offer a control with
    ///     nothing behind it.
    /// </remarks>
    private readonly NativeQamResolutionService? _resolution;

    private readonly SteamUiModuleRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SoundPackService? _sounds;

    /// <summary>
    ///     Steam's revived storage pages over WSGM's own eject, format and library registration, or
    ///     null when this session has no storage managers to answer with.
    /// </summary>
    private readonly SteamStorageBridge? _storage;

    // Every switch write takes this, so the stored switches, the derivation and the patch switches
    // move as one step and the last writer always leaves the patches matching its switches. Held
    // only around synchronous switch setters, never across an await.
    private readonly Lock _switchGate = new();

    private readonly DeviceCoordinatorNativeQamTdpService _tdp;
    private readonly ThemeService? _themes;
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly ISteamUiTransport _transport;
    private readonly WsgmSteamSettingsService? _wsgmSettings;
    private IReadOnlyList<ISteamUiModule> _composedPluginModules = [];
    private Task? _disposal;

    // Set by CloseAdmission at shutdown start: nothing is applied, queued or answered after it.
    // Retraction still runs, until _retired marks the patch registry as gone.
    private volatile bool _disposed;

    // Derived from the switches and the active glyph profile: whether the glyph stylesheet is on.
    private volatile bool _glyphDeliveryEnabled;

    // The registered plugin patches' ids. Replaced whole under _switchGate.
    private volatile HashSet<string> _pluginPatchIds = [];
    private volatile bool _retired;

    // The switches the session last applied. Replaced whole under _switchGate; readers take one
    // reference and read a consistent set.
    private volatile SteamUiSurfaceSwitches _switches = SteamUiSurfaceSwitches.Off;

    // Whether WSGM's settings page can be drawn: its route and its renderer both verified. The menu
    // row is published only while it can, so a Steam update that breaks the page takes the row with
    // it rather than leaving one that opens onto nothing.
    private volatile bool _wsgmSettingsReady;

    /// <summary>Creates the host and its surface services.</summary>
    /// <param name="transport">The one process-long Steam UI transport.</param>
    /// <param name="toggleQuickAccess">Opens or closes WSGM's overlay.</param>
    /// <param name="backends">
    ///     The session-owned services behind the surfaces. The session builds and disposes them; this
    ///     host only reads them, and a surface whose backend is null is not declared.
    /// </param>
    /// <remarks>
    ///     The throwable steps (the asset, the module set, the patch registry) run before anything is
    ///     subscribed, so a host that fails to build leaves no handler behind on the session's services.
    /// </remarks>
    internal SteamUiSessionHost(
        ISteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        SteamUiBackends backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _toggleQuickAccess = toggleQuickAccess ?? throw new ArgumentNullException(nameof(toggleQuickAccess));
        // WSGM's composed asset, loaded first: it is the step most likely to throw.
        var asset = SteamUiAssetCatalog.LoadNativeQamBootstrap();
        var deviceCoordinator = backends.DeviceCoordinator;
        _coordinator = deviceCoordinator;
        _storage = backends.Storage;
        _powerProfiles = backends.PowerProfiles;
        _hybridCores = backends.HybridCores;
        _themes = backends.Themes;
        _animations = backends.Animations;
        _sounds = backends.Sounds;
        _cpuBoost = backends.CpuBoost;
        _displayTimeouts = backends.DisplayTimeouts;
        _pluginSteamUi = backends.PluginSteamUi;
        // The menu exists for WSGM's own Change Artwork entry, so it is not conditional on a plugin
        // source the way it was while artwork was a package.
        _artwork = backends.Artwork;
        _libraryImport = backends.LibraryImport;
        _wsgmSettings = backends.WsgmSettings;
        _graphics = backends.Graphics;
        _chordMirror = backends.ChordMirror;
        _powerMenu = backends.PowerMenu;
        _gameContextMenu = new SteamGameContextMenuBackend(
            _pluginSteamUi,
            _artwork is null ? null : _artwork.OpenAsync,
            _artwork is null ? null : SteamArtworkBrowserSurface.RouteFor);
        var libraryImport = _libraryImport;
        _extensionsTab = new SteamExtensionsTabBackend(
            _pluginSteamUi,
            libraryImport is null ? null : () => SteamLibraryImportSurface.Route,
            libraryImport is null
                ? null
                : () => string.Join(", ", libraryImport.ReadState().Reading),
            [.. new IExtensionsTabSection?[] { _themes, _animations }.OfType<IExtensionsTabSection>()]);
        // One fold store for every Quick Access tab: the Extensions tab's sections and the
        // Performance and Quick Settings groups.
        _panelFolds = new SteamPanelFoldsBackend(backends.Folds);
        _resolution = backends.Resolution is { } resolution ? new NativeQamResolutionService(resolution) : null;
        _tdp = new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _powerPresets =
            new NativeQamPowerPresetService(deviceCoordinator?.PowerPresets, deviceCoordinator?.PowerAssignments);
        _deviceControls = new DeviceCoordinatorNativeQamDeviceControlsService(deviceCoordinator);
        _performanceService = backends.Performance;
        _profiles = backends.Profiles;
        _performance = new PerformanceServiceNativeQamAdapter(_performanceService, _profiles)
        {
            PerfSupport = backends.PerfSupport,
            ApplyRefreshRate = backends.ApplyRefreshRate,
            ApplyVariableRefreshRate = backends.ApplyVariableRefreshRate
        };
        _autoTdp = new DeviceCoordinatorNativeQamAutoTdpService(deviceCoordinator, backends.AutoTdp);
        _controllerTarget = new DeviceCoordinatorNativeQamControllerTargetService(deviceCoordinator);
        _audio = backends.Audio is { } audio ? new AudioManagerNativeQamAudioService(audio) : null;
        _audioFormat = backends.Audio is { } formatAudio && backends.AudioProfiles is { } audioProfiles
            ? new NativeQamAudioFormatService(formatAudio, audioProfiles)
            : null;
        _brightness = backends.Brightness;
        _nativeSettings = new SteamNativeSettingsService(_brightness, _resolution, _performance, _tdp,
            _autoTdp, _powerProfiles, _powerPresets, _cpuBoost, _hybridCores, _audioFormat, _graphics,
            _coordinator, _profiles, _displayTimeouts);
        _network = backends.Radios is { } radios
            ? new NativeQamNetworkService(
                radios,
                () => !_disposed && _switches.NetworkIndicator,
                QueueStatePublication)
            : null;
        _bluetooth = backends.Radios is { } bluetoothRadios
            ? new NativeQamBluetoothService(bluetoothRadios, backends.ShowBluetoothPanel)
            : null;
        try
        {
            _hostModules = CreateModules();
            // Plugins that became ready later are added by OnPluginModulesChanged.
            var pluginModules = _pluginSteamUi?.ReadModules() ?? [];
            var composed = ComposeModules(pluginModules);
            var modules = composed.Modules;
            _pluginPatchIds = composed.PluginPatchIds;
            _composedPluginModules = pluginModules;
            // The module-derived vocabulary, named here rather than reached for from inside the bridge.
            _bridge = new SteamUiBridgeHost(_transport, asset, modules.AllowedCommands);
            _patches = new SteamUiPatchManager(_transport);
            _patches.Register(new SteamUiBridgePatch(_bridge));
            _patches.Register(_overlayActivation);
            modules.RegisterPatches(_patches);
            lock (_switchGate)
            {
                ApplySwitchStates();
            }

            _patches.SetGlobalEnabled(false);
            // Traffic in both directions is the runtime's; which patches are applied when stays here,
            // because that is this application's policy and not a general rule.
            // The library badge can be the only thing on: it reports the Home layout back and needs
            // its libraries published, so both directions stay open for it without native Quick Access.
            // The download sort can be too, and it reports the queue positions Steam refused.
            _runtime = new SteamUiModuleRuntime(
                _bridge,
                modules,
                _patches,
                () => PublishWanted(_switches),
                () => BootstrapWanted(_switches));
        }
        catch
        {
            // The network service polls from construction; nothing else here holds a handler yet.
            if (_network is { } network)
            {
                Log.Observe(network.DisposeAsync().AsTask(), "Steam UI network service disposal");
            }

            throw;
        }

        Subscribe();
        // Audio formats and Windows power policy can change without a manager event. Refresh the
        // existing shared projections, including QAM, while native Settings integration is enabled.
        _nativeStateRefresh = new Timer(static state =>
        {
            var host = (SteamUiSessionHost)state!;
            if (!host._disposed && host._switches.HostSurfaces)
            {
                host.QueueStatePublication();
            }
        }, this, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <inheritdoc />
    /// <remarks>Concurrent and repeated calls share one disposal.</remarks>
    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    /// <summary>Hears every change source that moves a published state. The constructor's last step.</summary>
    private void Subscribe()
    {
        _runtime.ModuleFailed += OnModuleFailed;
        _patches.Synchronized += OnPatchesSynchronized;
        _transport.GenerationChanged += OnGenerationChanged;
        _brightness.Changed += QueueStatePublication;
        _panelFolds.Changed += QueueStatePublication;
        LibraryBadges.Changed += OnSemanticStateChanged;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed += OnSemanticStateChanged;
        }

        // One subscription for every device row: the power sliders, the device controls and AutoTDP
        // all project the same capability views and coordinator configuration.
        if (_coordinator is not null)
        {
            _coordinator.Capabilities.Changed += OnCapabilitiesChanged;
            _coordinator.ConfigurationChanged += OnSemanticStateChanged;
        }

        _autoTdp.StateChanged += OnSemanticStateChanged;
        _performanceService.StateChanged += OnPerformanceStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
        // A profile change moves the game-override markers even when no device value changed.
        _profiles.Changed += OnProfilesChanged;
        if (_pluginSteamUi is not null)
        {
            _pluginSteamUi.Changed += QueueStatePublication;
            _pluginSteamUi.ModulesChanged += OnPluginModulesChanged;
            // A plugin that turned ready between the constructor's read and this subscription.
            if (!ReferenceEquals(_pluginSteamUi.ReadModules(), _composedPluginModules))
            {
                OnPluginModulesChanged();
            }
        }

        // Both host pages answer a command immediately and finish the work in the background, so
        // without these the page only ever sees the first loading snapshot: the scan or the search
        // completes, raises this, and nobody is listening. The page spins forever.
        if (_artwork is not null)
        {
            _artwork.Changed += QueueStatePublication;
        }

        if (_libraryImport is not null)
        {
            _libraryImport.Changed += QueueStatePublication;
        }

        if (_chordMirror is not null)
        {
            _chordMirror.Changed += QueueStatePublication;
        }

        if (_wsgmSettings is not null)
        {
            _wsgmSettings.Changed += QueueStatePublication;
        }

        if (_graphics is not null)
        {
            _graphics.Changed += QueueStatePublication;
        }

        if (_themes is not null)
        {
            _themes.Changed += OnThemesChanged;
        }

        if (_animations is not null)
        {
            _animations.Changed += QueueStatePublication;
        }

        if (_sounds is not null)
        {
            _sounds.Changed += QueueStatePublication;
        }

        if (_powerMenu is not null)
        {
            _powerMenu.Changed += QueueStatePublication;
        }

        if (_audio is not null)
        {
            _audio.StateChanged += OnSemanticStateChanged;
        }

        if (_audioFormat is not null)
        {
            _audioFormat.StateChanged += OnSemanticStateChanged;
        }
    }

    /// <summary>Detaches everything <see cref="Subscribe" /> attached.</summary>
    private void Unsubscribe()
    {
        _runtime.ModuleFailed -= OnModuleFailed;
        _patches.Synchronized -= OnPatchesSynchronized;
        _transport.GenerationChanged -= OnGenerationChanged;
        _brightness.Changed -= QueueStatePublication;
        _panelFolds.Changed -= QueueStatePublication;
        LibraryBadges.Changed -= OnSemanticStateChanged;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed -= OnSemanticStateChanged;
        }

        if (_coordinator is not null)
        {
            _coordinator.Capabilities.Changed -= OnCapabilitiesChanged;
            _coordinator.ConfigurationChanged -= OnSemanticStateChanged;
        }

        _autoTdp.StateChanged -= OnSemanticStateChanged;
        _performanceService.StateChanged -= OnPerformanceStateChanged;
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
        _profiles.Changed -= OnProfilesChanged;
        if (_pluginSteamUi is not null)
        {
            _pluginSteamUi.Changed -= QueueStatePublication;
            _pluginSteamUi.ModulesChanged -= OnPluginModulesChanged;
        }

        if (_artwork is not null)
        {
            _artwork.Changed -= QueueStatePublication;
        }

        if (_libraryImport is not null)
        {
            _libraryImport.Changed -= QueueStatePublication;
        }

        if (_chordMirror is not null)
        {
            _chordMirror.Changed -= QueueStatePublication;
        }

        if (_wsgmSettings is not null)
        {
            _wsgmSettings.Changed -= QueueStatePublication;
        }

        if (_graphics is not null)
        {
            _graphics.Changed -= QueueStatePublication;
        }

        if (_themes is not null)
        {
            _themes.Changed -= OnThemesChanged;
        }

        if (_animations is not null)
        {
            _animations.Changed -= QueueStatePublication;
        }

        if (_sounds is not null)
        {
            _sounds.Changed -= QueueStatePublication;
        }

        if (_powerMenu is not null)
        {
            _powerMenu.Changed -= QueueStatePublication;
        }

        if (_audio is not null)
        {
            _audio.StateChanged -= OnSemanticStateChanged;
        }

        if (_audioFormat is not null)
        {
            _audioFormat.StateChanged -= OnSemanticStateChanged;
        }
    }

    /// <summary>Retracts and disposes the host, no longer than the shutdown deadline allows.</summary>
    /// <param name="deadline">
    ///     The session's shutdown deadline. The toolkit's runtime and patch manager stop waiting when it
    ///     fires and name what they left; the patches not yet removed are reported as not removed.
    /// </param>
    /// <returns>The one disposal every caller shares.</returns>
    internal ValueTask StopAsync(CancellationToken deadline)
    {
        lock (_disposeGate)
        {
            _disposal ??= DisposeCoreAsync(deadline);
            return new ValueTask(_disposal);
        }
    }

    /// <summary>
    ///     Stops every Steam command, publication and surface write without a CEF round trip, so Steam's
    ///     Quick Access can no longer call into owners the shutdown is about to stop.
    /// </summary>
    /// <remarks>
    ///     The session calls this at the start of shutdown, before device cleanup. It is the no-CEF prefix
    ///     of <see cref="DisableAsync" />: the runtime's publish and request gates read the switches cleared
    ///     here, so every later command is refused. The patches stay in Steam until disposal retracts them
    ///     after device cleanup. Idempotent.
    /// </remarks>
    internal void CloseAdmission()
    {
        _disposed = true;
        Log.Observe(_nativeSettings.StopRumblePreviewAsync(), "Stopping Steam rumble calibration preview");
        _nativeStateRefresh.Dispose();
        ClearSwitches();
    }

    private async Task DisposeCoreAsync(CancellationToken deadline)
    {
        CloseAdmission();
        Exception? retraction = null;
        try
        {
            await RetractAsync(deadline).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Disposal still releases everything below; the session records the failure.
            retraction = ex;
        }

        _retired = true;
        // The session's services outlive this host: only the handlers attached to them go here.
        Unsubscribe();
        if (_bluetooth is not null)
        {
            await _bluetooth.StopDiscoveryAsync().ConfigureAwait(false);
        }

        // A session that ends while Steam's network page is open would otherwise leave the radio
        // sweeping and this host subscribed to a collection it no longer publishes.
        if (_network is not null)
        {
            await _network.DisposeAsync().ConfigureAwait(false);
        }

        _audioFormat?.Dispose();
        // The runtime first: it stops answering, cancels what is in flight and drains its own
        // request tasks, so nothing is still writing to the bridge when that is disposed below.
        await _runtime.ShutdownAsync(deadline).ConfigureAwait(false);
        // ReSharper disable once MethodHasAsyncOverload
        _shutdown.Cancel();
        // Removes whatever the retraction left, the bridge last, within the same deadline.
        await _patches.ShutdownAsync(deadline).ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _autoTdp.Dispose();
        _audio?.Dispose();
        _controllerTarget.Dispose();
        _shutdown.Dispose();
        if (retraction is not null)
        {
            ExceptionDispatchInfo.Throw(retraction);
        }
    }

    /// <summary>Applies every Steam surface switch the session derived from its configuration.</summary>
    /// <param name="next">The switches to hold from now on.</param>
    /// <remarks>
    ///     The one entry point for every switch. Under <c>_switchGate</c> it stores the switches, runs
    ///     the synchronous edge effects and derives every patch switch from them, so a later call always
    ///     wins over an earlier one and over a retraction that started before it. The edges:
    ///     <list type="bullet">
    ///         <item>native Quick Access going off cancels its in-flight requests at once;</item>
    ///         <item>the screensaver rows going off forget Steam's screensaver timeouts;</item>
    ///         <item>the Wi-Fi indicator going off while Quick Access is off stops the network sweep;</item>
    ///         <item>the host surfaces moving reset the sound integration line;</item>
    ///         <item>the glyph inputs moving rebuild the glyph presentation.</item>
    ///     </list>
    ///     The host surfaces are deliberately not conditional on a plugin source existing: the pages,
    ///     the game menu and the plugin tab carry WSGM's own entries. The carousel's uninstalled-games
    ///     preference alone changing is only a publication, so the carousel re-orders without being
    ///     retracted. The screensaver rows need a session timeout owner and stay off without one.
    /// </remarks>
    internal void Apply(SteamUiSurfaceSwitches next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (_displayTimeouts is null)
        {
            next = next with { ScreensaverRows = false };
        }

        SteamUiSurfaceSwitches previous;
        lock (_switchGate)
        {
            previous = _switches;
            if (_disposed || next == previous)
            {
                return;
            }

            _switches = next;
            if (previous.Glyphs != next.Glyphs
                || !Equals(previous.GlyphProfile, next.GlyphProfile)
                || previous.NativeGlyphArtwork != next.NativeGlyphArtwork)
            {
                _glyphDeliveryState.Update(next.Glyphs ? next.GlyphProfile : null, next.NativeGlyphArtwork);
            }

            ApplySwitchStates();
        }

        // The edges run once the new switches are in force, so a request arriving now is refused.
        if (previous.NativeQuickAccess && !next.NativeQuickAccess)
        {
            CancelAllInflightRequests();
        }

        if (previous.ScreensaverRows && !next.ScreensaverRows)
        {
            _displayTimeouts?.ForgetSteam();
        }

        if (previous.NetworkIndicator && !next.NetworkIndicator && !next.NativeQuickAccess)
        {
            _network?.PostStopScanning();
        }

        if (previous.HostSurfaces != next.HostSurfaces)
        {
            _sounds?.SetHostState(next.HostSurfaces, null);
        }

        QueueSynchronization();
        // Several published states read the switches: the network header, the carousel's
        // preference and the capability hook's mask among them.
        QueueStatePublication();
    }

    /// <summary>Sets every patch switch and the global switch from the stored switches.</summary>
    /// <remarks>
    ///     The caller holds <c>_switchGate</c>. Only turns the global switch on; turning it off is the
    ///     retraction's and the synchronization handler's. The patch manager removes the gates before
    ///     the bridge they live in, so the bridge's switch needs no ordering here.
    /// </remarks>
    private void ApplySwitchStates()
    {
        var switches = _switches;
        SetPatchStates(switches, BootstrapWanted(switches));
        SetGlyphDeliveryPatchStates(switches);
        _patches.SetPatchEnabled(_overlayActivation.Id, switches.SurfaceObservation);
        if (AnyPatchWanted(switches))
        {
            _patches.SetGlobalEnabled(true);
        }
    }

    /// <summary>Whether any surface that runs without native Quick Access is on.</summary>
    /// <remarks>
    ///     Download sort counts: it registers its transform on the toolkit's shared JSX-runtime claim,
    ///     which the bridge serves.
    /// </remarks>
    private static bool IndependentSurfacesEnabled(SteamUiSurfaceSwitches switches)
    {
        return switches.NetworkIndicator
               || switches.HostSurfaces
               || switches.LibraryBadge
               || switches.HomeCarousel
               || switches.ScreensaverRows
               || switches.DownloadSort;
    }

    /// <summary>Whether the bridge bootstrap is needed: native QAM, or a surface that works without it.</summary>
    private static bool BootstrapWanted(SteamUiSurfaceSwitches switches)
    {
        return switches.NativeQuickAccess || IndependentSurfacesEnabled(switches);
    }

    /// <summary>Whether states are published and requests answered at all.</summary>
    /// <remarks>
    ///     The library badge and the download sort can be the only thing on, and both report back.
    /// </remarks>
    private static bool PublishWanted(SteamUiSurfaceSwitches switches)
    {
        return switches.NativeQuickAccess || switches.HostSurfaces || switches.LibraryBadge
               || switches.HomeCarousel || switches.ScreensaverRows || switches.DownloadSort;
    }

    /// <summary>Whether any patch at all should stay applied, so the global switch stays on.</summary>
    private bool AnyPatchWanted(SteamUiSurfaceSwitches switches)
    {
        return BootstrapWanted(switches) || _glyphDeliveryEnabled || switches.SurfaceObservation;
    }

    /// <summary>Returns the immutable patch-registry view used by diagnostics and isolated tests.</summary>
    /// <returns>The patch manager's independent snapshot list with current generations and diagnostics.</returns>
    internal IReadOnlyList<SteamUiPatchSnapshot> GetPatchSnapshots()
    {
        return _patches.GetSnapshots();
    }

    /// <summary>Retracts every surface from Steam and turns the patch registry off.</summary>
    /// <remarks>
    ///     Still runs after <see cref="CloseAdmission" />, so a master-switch or Big Picture retraction
    ///     already queued at shutdown start removes what it was meant to; only a retired host skips it.
    /// </remarks>
    /// <returns>A task completing after the retraction pass, or immediately for a retired host; session shutdown cancellation can interrupt the pass.</returns>
    internal async Task DisableAsync()
    {
        if (_retired)
        {
            return;
        }

        ClearSwitches();
        await RetractAsync(_shutdown.Token).ConfigureAwait(false);
    }

    /// <summary>Turns every feature switch off and cancels in-flight requests, without touching CEF.</summary>
    private void ClearSwitches()
    {
        Log.Observe(_nativeSettings.StopRumblePreviewAsync(), "Stopping rumble preview when Steam integration stops");
        lock (_switchGate)
        {
            _switches = SteamUiSurfaceSwitches.Off;
            // ReSharper disable once MethodHasAsyncOverload
            _patches.SetPatchEnabled(_overlayActivation.Id, false);
        }

        CancelAllInflightRequests();
    }

    /// <summary>Takes the patches out of Steam in one pass.</summary>
    /// <param name="cancellationToken">Ends the wait for the pass.</param>
    /// <remarks>
    ///     The patch manager removes the gates before the bridge they live in on every pass. The patch
    ///     switches are derived from the stored switches under <c>_switchGate</c>, so an
    ///     <see cref="Apply" /> that lands during the retraction wins rather than being overwritten with
    ///     off.
    /// </remarks>
    private async Task RetractAsync(CancellationToken cancellationToken)
    {
        if (_network is not null)
        {
            await _network.StopScanningAsync().ConfigureAwait(false);
        }

        lock (_switchGate)
        {
            ApplySwitchStates();
            if (!AnyPatchWanted(_switches))
            {
                _glyphDeliveryState.Update(null);
                // ReSharper disable once MethodHasAsyncOverload
                _patches.SetGlobalEnabled(false);
            }
        }

        await _patches.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            Log.Observe(_nativeSettings.StopRumblePreviewAsync(), "Stopping rumble preview after Steam reload");
            if (_sounds is { } sounds)
            {
                sounds.SetHostState(_switches.HostSurfaces, null);
                _ = sounds.RefreshAsync(CancellationToken.None);
            }

            // A semantic operation is authorized against one execution-context/document pair.
            // Letting it continue after either generation moved could apply a result for a page
            // that can no longer receive its response, so replacement is cancellation just like
            // an explicit bridge cancel.
            CancelAllInflightRequests();
            // A new document is a new client until its screensaver surface reports again.
            _displayTimeouts?.ForgetSteam();
        }

        // The patch manager marks patches for every changed target role, so every role change must
        // queue synchronization.
        // Every surface switch belongs here: a Steam restart replaces the SharedJSContext
        // generation, and a surface that is the only thing on would otherwise never be reapplied.
        var switches = _switches;
        if (BootstrapWanted(switches)
            || switches.Glyphs
            || _glyphDeliveryEnabled
            || switches.SurfaceObservation)
        {
            QueueSynchronization();
        }
    }

    /// <summary>Asks the patch manager for a pass; it coalesces requests into one queued pass.</summary>
    private void QueueSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        _patches.QueueSynchronization();
    }

    /// <summary>Reconciles what reads patch states after each of the manager's queued passes.</summary>
    /// <remarks>
    ///     Raised by the manager on a thread of its own, after the pass released its scheduler. Turning
    ///     the global switch off queues one more pass only when the switch moves, so this settles.
    /// </remarks>
    private void OnPatchesSynchronized(object? sender, EventArgs e)
    {
        if (_retired)
        {
            return;
        }

        _pluginSteamUi?.CheckPatches(_patches.GetSnapshots());

        if (_sounds is { } sounds)
        {
            var soundPatch = _patches.GetSnapshots()
                .FirstOrDefault(patch => patch.Id == SteamSoundOverrideSurface.PatchId);
            sounds.SetHostState(_switches.HostSurfaces, soundPatch);
        }

        ReconcileScreensaverReport();
        ReconcileWsgmSettingsMenu();
        // Every surface that runs without native Quick Access keeps the bootstrap up. Only
        // the network indicator used to count here, so with Quick Access off the library
        // badge, the Home carousel and the screensaver rows were retracted after each pass.
        if (BootstrapWanted(_switches))
        {
            QueueStatePublication();
            return;
        }

        // Re-derived under the gate, so an Apply that landed after this pass read the
        // switches wins rather than being overwritten with off.
        lock (_switchGate)
        {
            ApplySwitchStates();
            if (!AnyPatchWanted(_switches))
            {
                // ReSharper disable once MethodHasAsyncOverload
                _patches.SetGlobalEnabled(false);
            }
        }
    }

    /// <summary>Forgets Steam's screensaver timeouts whenever the surface that reports them does not hold.</summary>
    /// <remarks>
    ///     The bound they set belongs to the client that reported it. Switching to a Steam build without
    ///     the screensaver, or turning the rows off, otherwise leaves the overlay refusing short display
    ///     timeouts for a screensaver that no longer exists. A holding surface reports again on install.
    /// </remarks>
    private void ReconcileScreensaverReport()
    {
        if (_displayTimeouts is null)
        {
            return;
        }

        var surface = _patches.GetSnapshots().FirstOrDefault(patch => patch.Id == SteamScreensaverSurface.PatchId);

        if (!ScreensaverReportHolds(surface))
        {
            _displayTimeouts.ForgetSteam();
        }
    }

    /// <summary>Publishes WSGM's menu row, or takes it down, as its page becomes drawable or stops being.</summary>
    private void ReconcileWsgmSettingsMenu()
    {
        if (_wsgmSettings is null)
        {
            return;
        }

        var snapshots = _patches.GetSnapshots();
        var ready = WsgmSettingsPageReady(snapshots);
        if (ready != _wsgmSettingsReady)
        {
            _wsgmSettingsReady = ready;
            QueueStatePublication();
        }
    }

    /// <summary>Whether WSGM's settings page can be drawn: its route and its renderer both verified.</summary>
    /// <param name="snapshots">Every patch's state.</param>
    /// <returns>True only when both patches are enabled and verified.</returns>
    internal static bool WsgmSettingsPageReady(IReadOnlyList<SteamUiPatchSnapshot> snapshots)
    {
        return PageReady(snapshots, SteamWsgmSettingsSurface.PatchId);
    }

    /// <summary>Whether one of WSGM's pages can be drawn: the route host and its renderer both verified.</summary>
    /// <param name="snapshots">Every patch's state.</param>
    /// <param name="patchId">The page's own patch.</param>
    /// <returns>True only when both patches are enabled and verified.</returns>
    internal static bool PageReady(IReadOnlyList<SteamUiPatchSnapshot> snapshots, string patchId)
    {
        return new[] { SteamPageSurface.PatchId, patchId }.All(id =>
            snapshots.Any(snapshot => snapshot is { Enabled: true, State: SteamUiPatchState.Verified }
                                      && snapshot.Id == id));
    }

    /// <summary>Whether a Screensaver settings patch in this state still vouches for Steam's report.</summary>
    /// <param name="surface">The patch's snapshot, or null when it is not registered.</param>
    /// <returns>True while it is enabled and applying, applied or verified.</returns>
    internal static bool ScreensaverReportHolds(SteamUiPatchSnapshot? surface)
    {
        return surface is
        {
            Enabled: true,
            State: SteamUiPatchState.Applying or SteamUiPatchState.Applied or SteamUiPatchState.Verified
        };
    }

    /// <summary>
    ///     Every Steam UI surface this session offers, one declaration each: which toolkit surface it
    ///     is, the state WSGM feeds it, and the backend that answers it.
    /// </summary>
    /// <remarks>
    ///     The toolkit owns each surface's patches, wire shapes and payload readers, so a module here is
    ///     exactly "this is our data, and it maps to that feature". A surface whose backend is absent
    ///     in this session is simply not declared. WSGM's own features — download sorting and glyph
    ///     delivery — are patches of WSGM's own and are declared beside them. Plugin modules are not
    ///     declared here: <see cref="ComposeModules" /> adds the ready plugins' modules.
    /// </remarks>
    private List<ISteamUiModule> CreateModules()
    {
        List<ISteamUiModule> modules =
        [
            new SteamUiModule(
                "shell",
                commands: [new SteamUiCommandHandler(ShellPatchId, "toggleQuickAccess", HandleToggleQuickAccessAsync)]),

            // Valve's TDP toggle and slider, fed the primary power limit's range and routed back to
            // the device capability.
            SteamPowerLimitSurface.Module(
                Enabled,
                () => new ValueTask<SteamPowerLimitState?>(_tdp.PowerLimit),
                _tdp,
                "tdp"),

            SteamAutoTdpRow.Module(Enabled, () => new ValueTask<SteamAutoTdpState?>(_autoTdp.Current), _autoTdp),

            // The frame limit is the toolkit's unified row rather than Valve's notch slider, and
            // the Q12 retirement does not apply: a free 30-120 range made Valve's unusable.
            SteamFrameLimitRow.Module(Enabled, () => new ValueTask<SteamFrameLimitState?>(_performance.FrameLimit),
                _performance),
            SteamPowerProfileRow.Module(Enabled, _powerProfiles.ReadAsync, _powerProfiles),
            SteamHybridCoreRow.Module(Enabled, _hybridCores.ReadAsync, _hybridCores),
            SteamPowerPresetRow.Module(Enabled, _powerPresets.ReadAsync, _powerPresets),

            SteamControllerTargetRow.Module(
                Enabled,
                () => new ValueTask<SteamControllerTargetState?>(_controllerTarget.Current),
                _controllerTarget),

            // Declared unconditionally — whether the switch appears is decided by whether the
            // device publishes a variable-refresh capability, which the state carries.
            SteamVariableRefreshRow.Module(Enabled, () => new ValueTask<SteamVariableRefreshState?>(_performance.Vrr),
                _performance),

            // The backend behind Valve's own Performance tab and the Valve rows that read it.
            // Declared unconditionally because the performance service always exists; what the
            // panel then shows is decided entirely by which fields the projected state carries.
            SteamPerformanceSurface.Module(
                Enabled,
                () => new ValueTask<SteamPerformanceState?>(_performance.PerfState),
                _performance,
                "perf"),

            // Declared unconditionally: the panel backlight depends on nothing WSGM has to supply.
            SteamBrightnessSurface.Module(Enabled, _brightness.ReadAsync, _brightness),

            SteamDeviceControlsRow.Module(
                Enabled,
                () => new ValueTask<SteamDeviceControlsState?>(_deviceControls.Current),
                _deviceControls),

            SteamDownloadSort.Module(),

            new SteamUiModule(
                "glyph-style",
                [new SteamInputGlyphStylePatch(_glyphDeliveryState)]),

            // The library badge on every library tile, fed from the card model. Declared
            // unconditionally: which cards exist is the reading's business, and a session with
            // none publishes an empty list, which names every installed game as internal.
            SteamLibraryBadgeSurface.Module(
                () => _switches.LibraryBadge,
                () => new ValueTask<SteamLibraryBadgeState?>(LibraryBadges.Current),
                _libraryBadge),

            // Home's carousel, built from the same card reading as the badge so the two never
            // disagree about which card is in the reader. Rides LibraryBadges.Changed for card moves.
            SteamHomeCarouselSurface.Module(
                () => _switches.HomeCarousel,
                () => new ValueTask<SteamHomeCarouselState?>(HomeCarousel.Build(LibraryBadges.Current,
                    _switches.CarouselShowUninstalled)),
                _homeCarousel)
        ];

        if (_chordMirror is { } chordMirror)
        {
            // The hook exists whenever the mirror does; its state says whether a reset needs
            // reporting, so a session with the mirror off installs the hook and never hears from it.
            modules.Add(SteamChordResetSurface.Module(HostSteamUiEnabled, chordMirror));
        }

        // The controller's capabilities as Steam's pages read them. Declared unconditionally: the
        // state carries an empty mask until a profile marks a whole pair of controls absent.
        modules.Add(SteamControllerCapsSurface.Module(HostSteamUiEnabled, () => _glyphDeliveryState.Current));

        if (_cpuBoost is { } cpuBoost)
        {
            // Beside the core preference, and equally independent of a device plugin: it is Windows
            // power policy carried per game.
            modules.Add(SteamCpuBoostRow.Module(Enabled, cpuBoost.ReadAsync, cpuBoost));
        }

        // Steam's game menu. Declared unconditionally: WSGM's own Change Artwork entry is in it
        // whether or not a plugin contributes anything.
        modules.Add(SteamGameContextMenuSurface.Module(
            HostSteamUiEnabled,
            () => new ValueTask<SteamGameContextMenuState?>(_gameContextMenu.ReadState()),
            _gameContextMenu));

        if (_powerMenu is { } powerMenu)
        {
            // Steam's own Switch to Desktop, offered while WSGM holds Game Mode.
            modules.Add(SteamPowerMenuSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamPowerMenuState?>(powerMenu.ReadState()),
                powerMenu));
        }

        // Every custom route in one module. The page host owns one patch and one publication, so a
        // second page owner cannot register the same patch id and take the whole session down with
        // it; each owner contributes routes and the host merges them.
        modules.Add(SteamPageSurface.Module(
            HostSteamUiEnabled,
            () => new ValueTask<SteamPageState?>(ReadPages())));

        if (_artwork is { } artwork)
        {
            modules.Add(SteamArtworkBrowserSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamArtworkBrowserState?>(artwork.ReadState()),
                artwork));
        }

        if (_libraryImport is { } libraryImport)
        {
            modules.Add(SteamLibraryImportSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<GameLibraryState?>(libraryImport.ReadState()),
                () => libraryImport.Revision,
                libraryImport));
        }

        // The toolkit's file and folder picker, which the Game Library opens to add a shortcuts
        // folder and the artwork page to browse for a local image. It lists names only, and only
        // while WSGM's own Steam pages are enabled.
        if (_libraryImport is not null || _artwork is not null)
        {
            modules.Add(SteamFilePickerSurface.Module(HostSteamUiEnabled));
        }

        // WSGM's settings page, and the WSGM row in Steam's main menu that opens it. The row carries
        // the page's route, so Valve's own entry navigates and the backend is never asked.
        if (_wsgmSettings is { } wsgmSettings)
        {
            modules.Add(SteamWsgmSettingsSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<WsgmSteamSettingsState?>(wsgmSettings.ReadState()),
                wsgmSettings));
            modules.Add(SteamNavigationPanelSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamNavigationPanelState?>(WsgmSteamSettingsService.ReadMenu(_wsgmSettingsReady)),
                wsgmSettings));
        }

        // GPU controls in Quick Access share their projection with Steam's Display settings.
        if (_graphics is { } graphics)
        {
            modules.Add(SteamSettingsQuickAccessRow.Module(Enabled,
                () => new ValueTask<SteamSettingsQuickAccessState?>(graphics.ReadQuickAccessState()), graphics));
        }

        modules.Add(SteamNativeSettingsSurface.Module(HostSteamUiEnabled, _nativeSettings.ReadAsync, _nativeSettings));

        // The themes: the page they are browsed and managed on, and the cascade the toolkit installs
        // into every window. Both follow CEF itself; the cascade also follows the themes' own switch,
        // and publishes nothing while it is off, which leaves Steam's styling as it was.
        if (_themes is { } themes)
        {
            modules.Add(SteamThemesSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamThemesState?>(themes.ReadState()),
                () => themes.Revision,
                themes));
            modules.Add(SteamThemeStyleSurface.Module(
                () => HostSteamUiEnabled() && themes.Enabled,
                () => new ValueTask<SteamThemeState?>(themes.ReadStyles()),
                () => themes.StylesRevision));
        }

        if (_sounds is { } sounds)
        {
            modules.Add(SteamSoundOverrideSurface.Module(HostSteamUiEnabled,
                () => new ValueTask<SteamSoundOverrideState?>(sounds.ReadOverrides()), () => sounds.Revision,
                reportStatus: sounds.ReportPlaybackStatus));
        }

        // The boot movie's page. Follows CEF itself, like the themes' page.
        if (_animations is { } animations)
        {
            modules.Add(SteamAnimationsSurface.Module(
                HostSteamUiEnabled,
                () => new ValueTask<SteamAnimationsState?>(animations.ReadState()),
                () => animations.Revision,
                animations));
        }

        // Which Quick Access sections the user opened. Declared unconditionally: the sections exist
        // whenever the tabs do.
        modules.Add(SteamPanelFoldsSurface.Module(
            Enabled,
            () => new ValueTask<SteamPanelFoldsState?>(_panelFolds.ReadState()),
            _panelFolds));

        // The sections those folds belong to, and the label a game-override row leads with. Constant,
        // and declared beside the folds for the same reason.
        modules.Add(SteamQuickAccessLayoutSurface.Module(
            Enabled,
            () => new ValueTask<SteamQuickAccessLayout?>(NativeQamLayout.Layout)));

        // The plugin tab. Declared unconditionally: WSGM's own tools are on it whether or not any
        // package is installed, which is the state every install was actually in.
        modules.Add(SteamExtensionsTabSurface.Module(
            HostSteamUiEnabled,
            () => new ValueTask<SteamExtensionsTabState?>(_extensionsTab.ReadState()),
            _extensionsTab));

        // WSGM's display-off rows in Steam's Screensaver settings, over the same timeouts the overlay
        // edits. Reading goes to Windows each time, so an overlay change reaches Steam on the next
        // publication and a change made in Windows reaches it when the page next opens.
        if (_displayTimeouts is { } timeouts)
        {
            modules.Add(SteamScreensaverSurface.Module(
                () => _switches.ScreensaverRows,
                () => new ValueTask<SteamScreensaverState?>(timeouts.ReadState()),
                timeouts));
        }

        if (_resolution is { } resolution)
        {
            modules.Add(SteamResolutionRow.Module(
                Enabled,
                () => new ValueTask<SteamResolutionState?>(resolution.Current),
                resolution));
        }

        if (_audio is { } audio)
        {
            // Publishing once after injection updates the store whose availability was cached when
            // Steam started before the replacement namespace existed.
            modules.Add(SteamAudioSurface.Module(Enabled, () => new ValueTask<SteamAudioState?>(audio.Current), audio));
        }

        if (_audioFormat is { } audioFormat)
        {
            modules.Add(SteamAudioFormatRow.Module(Enabled, audioFormat.ReadAsync, audioFormat));
        }

        // The gate reveals Steam's Wi-Fi surface, and the surface is only worth revealing if
        // something can populate it — which is the radio manager. Bluetooth rides the same
        // condition for the same reason.
        if (_network is { } network)
        {
            modules.Add(SteamNetworkSurface.Module(
                // The one publication not gated on native Quick Access alone: the header Wi-Fi
                // indicator is shown on the desktop side too, where the rest of the QAM is not.
                () =>
                {
                    var switches = _switches;
                    return switches.NativeQuickAccess || switches.NetworkIndicator;
                },
                () => network.ReadStateAsync(_switches.NetworkIndicator),
                network));
        }

        if (_bluetooth is not null)
        {
            modules.Add(SteamBluetoothSurface.Module(Enabled, _bluetooth.ReadStateAsync, _bluetooth));
        }

        // Reading is synchronous: it projects the managers' rows and reads the local volumes, Steam's
        // library file and each card's marker, skipping network and optical drives.
        if (_storage is { } storage)
        {
            modules.Add(SteamStorageSurface.Module(Enabled,
                () => new ValueTask<SteamStorageState?>(storage.ReadState()), storage));
        }

        return modules;
    }

    /// <summary>The publication gate every surface shares: on while native Quick Access is.</summary>
    private bool Enabled()
    {
        return _switches.NativeQuickAccess;
    }

    /// <summary>Whether the host-rendered Steam surfaces may publish.</summary>
    /// <returns>Whether CEF itself is on, regardless of the native Quick Access switch.</returns>
    private bool HostSteamUiEnabled()
    {
        return _switches.HostSurfaces;
    }

    /// <summary>Every custom route this session serves, WSGM's own first.</summary>
    /// <remarks>
    ///     One owner publishes the route list, because the page host keys its patch and publication by
    ///     one id: two owners publishing it would alternately clobber each other's routes, and two
    ///     owners registering the patch is refused outright by the module set. A plugin route that
    ///     collides with one already admitted is dropped and named, so a package cannot shadow
    ///     another's page or WSGM's.
    /// </remarks>
    private SteamPageState ReadPages()
    {
        List<SteamPage> pages = [];
        if (_artwork is not null)
        {
            pages.Add(new SteamPage(
                "artwork-browser",
                SteamArtworkBrowserSurface.Route,
                "Change Artwork",
                Template: "artwork-browser"));
        }

        if (_libraryImport is not null)
        {
            pages.Add(new SteamPage(
                "library-import",
                SteamLibraryImportSurface.Route,
                "Import games",
                Template: SteamLibraryImportSurface.Template));
        }

        if (_wsgmSettings is not null)
        {
            pages.Add(new SteamPage(
                "wsgm-settings",
                SteamWsgmSettingsSurface.Route,
                "WSGM",
                Template: SteamWsgmSettingsSurface.Template));
        }

        if (_themes is not null)
        {
            pages.Add(new SteamPage(
                "themes",
                SteamThemesSurface.Route,
                "Themes",
                Template: SteamThemesSurface.Template));
        }

        if (_animations is not null)
        {
            pages.Add(new SteamPage(
                "animations",
                SteamAnimationsSurface.Route,
                "Animations",
                Template: SteamAnimationsSurface.Template));
        }

        if (_switches.HostSurfaces && _pluginSteamUi is { } pluginSteamUi)
        {
            HashSet<string> claimed = new(pages.Select(page => page.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var page in pluginSteamUi.ReadPages())
            {
                if (claimed.Add(page.Path))
                {
                    pages.Add(page);
                }
                else
                {
                    Log.Change(
                        $"steam-page-collision:{page.Path}",
                        $"A plugin page was refused: {page.Path} is already served.",
                        LogLevel.Warn);
                }
            }
        }

        return new SteamPageState(pages);
    }

    private async Task<SteamUiCommandResult> HandleToggleQuickAccessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        var succeeded = await _toggleQuickAccess(cancellationToken).ConfigureAwait(false);
        return succeeded
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, "Quick access is not currently available.");
    }

    private void OnSemanticStateChanged()
    {
        QueueStatePublication();
    }

    private void OnCapabilitiesChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        QueueStatePublication();
    }

    private void OnPerformanceStateChanged(PerformanceState state)
    {
        QueueStatePublication();
    }

    private void OnProfilesChanged(ProfileSnapshot snapshot, ProfileChangeKind kind)
    {
        QueueStatePublication();
    }

    // The cascade follows the themes' own switch, which changes without any of the host's feature
    // switches: off, the patch is retracted and every owned node leaves every window; on, it is
    // installed. The patch switches are derived as everywhere else, so this cannot overwrite a newer
    // switch; SetPatchEnabled synchronizes only when a switch actually moves.
    private void OnThemesChanged()
    {
        lock (_switchGate)
        {
            if (!_disposed)
            {
                ApplySwitchStates();
            }
        }

        QueueStatePublication();
    }

    /// <summary>The host's own modules and the ready plugins' ones, as one set.</summary>
    /// <param name="pluginModules">The plugin modules, in the source's order.</param>
    /// <returns>The set, and the patch ids of the plugin modules it took.</returns>
    /// <remarks>
    ///     A plugin module whose id, patch, state or command collides with one already taken is dropped
    ///     with one log line, so one package cannot take every Steam surface down with it.
    /// </remarks>
    private (SteamUiModuleSet Modules, HashSet<string> PluginPatchIds) ComposeModules(
        IReadOnlyList<ISteamUiModule> pluginModules)
    {
        List<ISteamUiModule> accepted = [];
        foreach (var module in pluginModules)
        {
            // The bridge and the overlay activation are registered beside the set, so the set cannot
            // refuse a module claiming them; the patch registry would, after part of the swap.
            if (module.Patches.Any(patch => patch.Id == SteamUiBridgePatch.PatchId
                                            || patch.Id == _overlayActivation.Id))
            {
                Log.Warn($"Steam UI plugin module {module.Id} was not registered: it claims a host patch.");
                _pluginSteamUi?.FailModule(module.Id, "Module collides with a host-owned patch.");
                continue;
            }

            try
            {
                _ = new SteamUiModuleSet([.. _hostModules, .. accepted, module]);
                accepted.Add(module);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                _pluginSteamUi?.FailModule(module.Id, ex.Message);
                Log.Warn($"Steam UI plugin module {module.Id} was not registered: {ex.Message}");
            }
        }

        accepted = [.. accepted.Where(module => _pluginSteamUi?.ModuleEnabled(module.Id) != false)];
        HashSet<string> pluginPatchIds = [.. accepted.SelectMany(module => module.Patches).Select(patch => patch.Id)];
        return (new SteamUiModuleSet([.. _hostModules, .. accepted]), pluginPatchIds);
    }

    /// <summary>Registers a plugin's modules when it becomes ready and removes them when it stops.</summary>
    private void OnPluginModulesChanged()
    {
        if (!_disposed)
        {
            Log.Observe(ReplacePluginModulesAsync(), "Steam UI plugin module registration");
        }
    }

    /// <summary>Hands the runtime the module set for the plugins ready now, then applies their switches.</summary>
    /// <remarks>
    ///     The runtime registers added patches, swaps the bridge vocabulary and the set, and retracts
    ///     removed patches. Their switches follow the host surfaces like every plugin patch, and the
    ///     queued pass applies them and installs the bridge again with the new vocabulary.
    /// </remarks>
    private async Task ReplacePluginModulesAsync()
    {
        if (_disposed || _pluginSteamUi is null)
        {
            return;
        }

        try
        {
            await _pluginModulesChange.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                var pluginModules = _pluginSteamUi.ReadModules();
                if (_disposed || ReferenceEquals(pluginModules, _composedPluginModules))
                {
                    return;
                }

                var composed = ComposeModules(pluginModules);
                lock (_switchGate)
                {
                    // Both lists while the runtime swaps, so neither an added nor a leaving plugin
                    // patch is switched as a Quick Access row in between.
                    _pluginPatchIds = [.. _pluginPatchIds, .. composed.PluginPatchIds];
                }

                await _runtime.ReplaceModulesAsync(composed.Modules, _shutdown.Token).ConfigureAwait(false);
                _composedPluginModules = pluginModules;
                lock (_switchGate)
                {
                    _pluginPatchIds = composed.PluginPatchIds;
                    if (!_disposed)
                    {
                        ApplySwitchStates();
                    }
                }

                QueueSynchronization();
            }
            finally
            {
                _pluginModulesChange.Release();
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Shutdown took the runtime or the patch registry first; it retracts every patch itself.
        }
    }

    // The runtime quarantines the module and faults its patches in the manager, which keeps them off
    // whatever the switches here say. This side only records it.
    private void OnModuleFailed(object? sender, SteamUiModuleFailure failure)
    {
        _pluginSteamUi?.FailModule(failure.Module.Id, $"{failure.Operation}: {failure.Error}");
        Log.Warn($"Steam UI module {failure.Module.Id} was disabled after {failure.Operation}: {failure.Error}");
    }

    private void QueueStatePublication()
    {
        if (!_disposed)
        {
            _runtime.QueuePublication();
        }
    }

    /// <summary>Republishes the shared display state after a topology notification.</summary>
    internal void RefreshDisplayState()
    {
        QueueStatePublication();
    }

    /// <summary>Sets every registered patch's switch from the given switches. The caller holds <c>_switchGate</c>.</summary>
    /// <param name="switches">The switches in force.</param>
    /// <param name="bootstrap">Whether the bridge stays applied.</param>
    private void SetPatchStates(SteamUiSurfaceSwitches switches, bool bootstrap)
    {
        // The registry is the source of truth for which patches exist; a hand-kept id list here
        // drifts. Glyphs and download sorting have independent switches; the network gate may also
        // outlive native QAM to keep the configured header indicator.
        var components = switches.NativeQuickAccess;
        var host = switches.HostSurfaces;
        foreach (var patch in _patches.GetSnapshots())
        {
            if (patch.Id == SteamInputGlyphStylePatch.PatchId || patch.Id == _overlayActivation.Id)
            {
                continue;
            }

            var enabled = patch.Id switch
            {
                SteamDownloadSort.PatchId => switches.DownloadSort,
                SteamLibraryBadgeSurface.PatchId or SteamLibraryBadgeSurface.DetailsPatchId => switches.LibraryBadge
                    || (host && _pluginSteamUi?.ReadModules().Count > 0),
                SteamHomeCarouselSurface.PatchId => switches.HomeCarousel,
                SteamScreensaverSurface.PatchId => switches.ScreensaverRows,
                SteamUiBridgePatch.PatchId => bootstrap,
                SteamNetworkSurface.PatchId => components || switches.NetworkIndicator,
                // Host-rendered surfaces follow CEF itself. Native Quick Access can be off while a
                // user still wants the artwork page and the plugin tab.
                SteamPageSurface.PatchId or SteamExtensionsTabSurface.PatchId
                    or SteamGameContextMenuSurface.PatchId or SteamPowerMenuSurface.PatchId
                    or SteamArtworkBrowserSurface.PatchId
                    or SteamLibraryImportSurface.PatchId or SteamWsgmSettingsSurface.PatchId
                    or SteamNavigationPanelSurface.PatchId or SteamNativeSettingsSurface.PatchId
                    or SteamThemesSurface.PatchId
                    or SteamAnimationsSurface.PatchId or SteamSoundOverrideSurface.PatchId => host,
                // The cascade follows the themes' own switch as well; off, the gate is retracted and
                // every owned node leaves every window.
                SteamThemeStyleSurface.PatchId => host && _themes is { Enabled: true },
                SteamSettingsQuickAccessRow.PatchId => components || (host && _pluginSteamUi?.ReadModules().Count > 0),
                _ when _pluginPatchIds.Contains(patch.Id) => host,
                _ => components
            };
            _patches.SetPatchEnabled(patch.Id, enabled);
        }
    }

    /// <summary>
    ///     Enables the one glyph stylesheet when the active plugin profile supplies something to draw.
    /// </summary>
    /// <param name="switches">The switches in force; the caller holds <c>_switchGate</c>.</param>
    /// <remarks>
    ///     One switch, because there is one stylesheet. The previous four independent tier switches
    ///     existed to gate four separate mapping namespaces; a single stylesheet either has rules or it
    ///     does not, and the patch itself refuses to apply an empty one.
    /// </remarks>
    private void SetGlyphDeliveryPatchStates(SteamUiSurfaceSwitches switches)
    {
        var presentation = _glyphDeliveryState.Current;
        // Absent controls count as rules. A reviewed profile may legitimately carry nothing but
        // them — hiding trackpad or extra-paddle rows on a handheld that has neither, while keeping
        // Valve's own artwork — and SteamGlyphCss.Build emits real hiding rules for exactly that.
        // Requiring a resource or an image left those profiles with no stylesheet at all, so the
        // controls the device does not have stayed on screen.
        var deliver = switches.Glyphs
                      && presentation is not null
                      && (presentation.StableResources.Count > 0
                          || presentation.ControllerImages.Count > 0
                          || presentation.AbsentControls.Count > 0);

        // Three independent conditions, and failing any of them leaves the Steam Input page showing
        // Valve's Steam Deck artwork instead of the handheld's own. The patch then reports itself
        // Disabled, which is honest but says nothing about which condition was missing — the
        // setting, a profile that never resolved, or a profile that resolved with nothing to draw.
        Log.Change(
            "steam.ui.glyphs",
            $"Steam Input glyph delivery {(deliver ? "enabled" : "disabled")}: "
            + $"setting={switches.Glyphs}, profile={presentation is not null}, "
            + $"stableResources={presentation?.StableResources.Count ?? 0}, "
            + $"controllerImages={presentation?.ControllerImages.Count ?? 0}, "
            + $"absentControls={presentation?.AbsentControls.Count ?? 0}",
            deliver ? LogLevel.Info : LogLevel.Warn);
        _patches.SetPatchEnabled(SteamInputGlyphStylePatch.PatchId, deliver);
        _glyphDeliveryEnabled = deliver;
    }

    private void CancelAllInflightRequests()
    {
        _runtime.CancelAllInflight();
    }
}
