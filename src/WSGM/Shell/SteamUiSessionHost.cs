using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit.Surfaces;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

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

    /// <summary>The artwork browser behind Steam's Change Artwork page, or null in overlay-test.</summary>
    private readonly SteamArtworkBrowserSource? _artwork;

    /// <summary>
    ///     Null when no audio manager exists for this session, which is the overlay-test case.
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
    private readonly DeviceCoordinatorNativeQamControllerTargetService _controllerTarget;
    private readonly DeviceCoordinatorNativeQamDeviceControlsService _deviceControls;

    /// <summary>The session's display-off timeouts, shared with the overlay, or null without one.</summary>
    private readonly DisplayTimeouts? _displayTimeouts;

    /// <summary>The Quick Access plugin tab, which carries WSGM's own tools as well.</summary>
    private readonly SteamExtensionsTabBackend _extensionsTab;

    private readonly Lock _failedPatchGate = new();

    // Patch ids of modules the runtime quarantined. Written from the publication and request paths,
    // read wherever patch states are decided.
    private readonly HashSet<string> _failedPatchIds = new(StringComparer.Ordinal);

    private readonly SteamGameContextMenuBackend _gameContextMenu;

    private readonly SteamInputGlyphDeliveryState _glyphDeliveryState = new();

    /// <summary>Hears what Big Picture Home's carousel holds.</summary>
    private readonly HomeCarouselBackend _homeCarousel = new();

    private readonly NativeQamHybridCoreService _hybridCores = new(HybridCores.Windows);

    /// <summary>Hears the library badge's Home layout report.</summary>
    private readonly LibraryBadgeBackend _libraryBadge = new();

    /// <summary>The library importer behind the Quick Access tab's page, or null in overlay-test.</summary>
    private readonly SteamLibraryImportSource? _libraryImport;

    /// <summary>The Wi-Fi surface, or null when this session has no radio manager.</summary>
    private readonly NativeQamNetworkService? _network;

    private readonly Lock _observationGate = new();
    private readonly Action<PerformanceState> _onPerformanceStateChanged;
    private readonly Action? _onPluginSteamUiChanged;
    private readonly Action<ProfileSnapshot, ProfileChangeKind> _onProfilesChanged;
    private readonly SteamOverlayActivationPatch _overlayActivation = new();
    private readonly bool _ownsBrightness;
    private readonly SteamUiPatchManager _patches;
    private readonly PerformanceServiceNativeQamAdapter _performance;

    private readonly PerformanceService _performanceService;
    private readonly IReadOnlyList<ISteamUiModule> _pluginModules;
    private readonly HashSet<string> _pluginPatchIds;
    private readonly CommonPluginSteamUiSource? _pluginSteamUi;
    private readonly NativeQamPowerPresetService _powerPresets;

    private readonly NativeQamPowerProfileService _powerProfiles = new(PowerSchemes.Windows,
        id => ConfigStore.Mutate(config => config.LastSelectedPowerSchemeId = id));

    private readonly NativeQamProfileOverrideService? _profileOverrides;
    private readonly ProfileService? _profiles;

    /// <summary>
    ///     The display-resolution row's backend, or null when this session must not move the display.
    /// </summary>
    /// <remarks>
    ///     Null in overlay-test, which runs without a real display to change. The patch is not
    ///     registered at all in that case, so the row cannot appear and offer a control with nothing
    ///     behind it.
    /// </remarks>
    private readonly NativeQamResolutionService? _resolution;

    private readonly SteamUiModuleRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    ///     Steam's revived storage pages over WSGM's own eject, format and library registration, or
    ///     null when this session has no storage managers to answer with.
    /// </summary>
    private readonly SteamStorageBridge? _storage;

    private readonly Task _synchronization;
    private readonly SemaphoreSlim _synchronizeSignal = new(0, 1);
    private readonly DeviceCoordinatorNativeQamTdpService _tdp;
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly ISteamUiTransport _transport;
    private volatile bool _carouselShowUninstalled;
    private volatile bool _disposed;
    private volatile bool _downloadSortEnabled;
    private volatile bool _enabled;
    private volatile bool _glyphDeliveryEnabled;
    private volatile bool _glyphsEnabled;
    private volatile bool _homeCarouselEnabled;
    private volatile bool _libraryBadgeEnabled;
    private volatile bool _networkIndicatorEnabled;
    private IDisposable? _performanceObservation;
    private volatile bool _pluginSteamUiEnabled;
    private volatile bool _screensaverEnabled;
    private int _signalPending;
    private volatile bool _surfaceObservationEnabled;

    /// <summary>Creates the host and its surface services.</summary>
    /// <param name="transport">The one process-long Steam UI transport.</param>
    /// <param name="toggleQuickAccess">Opens or closes WSGM's overlay.</param>
    /// <param name="deviceCoordinator">The device platform, or null when integration is off.</param>
    /// <param name="performance">The RTSS-backed performance service.</param>
    /// <param name="audio">The session's audio manager, or null in overlay-test.</param>
    /// <param name="radios">The session's radio manager, borrowed, or null in overlay-test.</param>
    /// <param name="resolution">The display-resolution backend, or null.</param>
    /// <param name="autoTdp">The session's AutoTDP service, or null when it is not running.</param>
    /// <param name="perfSupport">
    ///     What the device can back, for the reactivated performance panel. Supplied by the session
    ///     because the frame-limit options come from display-mode discovery and the VRR flag from the
    ///     device plugin, and this host owns neither. Null hides every performance control, which is
    ///     the correct state for a session that cannot yet say what it can honour.
    /// </param>
    /// <param name="applyRefreshRate">Applies a manually chosen refresh rate, or null.</param>
    /// <param name="applyVariableRefreshRate">Applies the VRR flag, or null.</param>
    /// <param name="showBluetoothPanel">Opens the session's Bluetooth prompt and status surface.</param>
    /// <param name="brightness">Session-owned brightness, or null for a standalone host.</param>
    /// <param name="storage">
    ///     The bridge over the session's own storage managers, or null when this session has none —
    ///     overlay-test, which owns no drive or format manager to answer with. The surface is then not
    ///     declared at all, so Steam's storage pages stay as inert as they are without WSGM rather than
    ///     opening onto controls with nothing behind them.
    /// </param>
    /// <param name="displayTimeouts">
    ///     The session's display-off timeouts, shared with the overlay, or null when this session has
    ///     none. Steam's Screensaver settings get no rows then.
    /// </param>
    /// <param name="audioProfiles">The live advanced-audio service, or null in overlay-test.</param>
    /// <param name="pluginSteamUi">The common-plugin projection rendered through host-owned Steam surfaces.</param>
    /// <param name="profiles">The profile owner Steam's per-game toggle and reset write to.</param>
    /// <param name="artwork">The artwork browser behind Steam's Change Artwork page, or null in overlay-test.</param>
    /// <param name="libraryImport">The library importer behind Steam's import page, or null in overlay-test.</param>
    internal SteamUiSessionHost(
        ISteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        DeviceCoordinator? deviceCoordinator,
        PerformanceService performance,
        AudioManager? audio = null,
        RadioManager? radios = null,
        DisplayResolutionService? resolution = null,
        AutoTdpService? autoTdp = null,
        Func<NativeQamPerfSupport>? perfSupport = null,
        Func<int, bool>? applyRefreshRate = null,
        Func<bool, CancellationToken, Task<bool>>? applyVariableRefreshRate = null,
        Func<bool>? showBluetoothPanel = null,
        NativeQamBrightnessService? brightness = null,
        SteamStorageBridge? storage = null,
        DisplayTimeouts? displayTimeouts = null,
        AudioProfileService? audioProfiles = null,
        CommonPluginSteamUiSource? pluginSteamUi = null,
        ProfileService? profiles = null,
        SteamArtworkBrowserSource? artwork = null,
        SteamLibraryImportSource? libraryImport = null)
    {
        _storage = storage;
        _displayTimeouts = displayTimeouts;
        _pluginSteamUi = pluginSteamUi;
        _pluginModules = pluginSteamUi?.ReadModules() ?? [];
        _pluginPatchIds = [.. _pluginModules.SelectMany(module => module.Patches).Select(patch => patch.Id)];
        // The menu exists for WSGM's own Change Artwork entry, so it is not conditional on a plugin
        // source the way it was while artwork was a package.
        _artwork = artwork;
        _libraryImport = libraryImport;
        _gameContextMenu = new SteamGameContextMenuBackend(
            pluginSteamUi,
            artwork is null ? null : artwork.OpenAsync,
            artwork is null ? null : SteamArtworkBrowserSurface.RouteFor);
        _extensionsTab = new SteamExtensionsTabBackend(
            pluginSteamUi,
            libraryImport is null ? null : () => SteamLibraryImportSurface.Route,
            libraryImport is null ? null : () => libraryImport.ReadState().SourceName);
        _resolution = resolution is null ? null : new NativeQamResolutionService(resolution);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _tdp = new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _powerPresets =
            new NativeQamPowerPresetService(deviceCoordinator?.PowerPresets, deviceCoordinator?.PowerAssignments);
        _deviceControls = new DeviceCoordinatorNativeQamDeviceControlsService(deviceCoordinator);
        _performanceService = performance;
        _profileOverrides = profiles is null
            ? null
            : new NativeQamProfileOverrideService(profiles, () => deviceCoordinator?.DeviceIdentityKey);
        _performance = new PerformanceServiceNativeQamAdapter(performance)
        {
            Profiles = profiles,
            PerfSupport = perfSupport,
            ApplyRefreshRate = applyRefreshRate,
            ApplyVariableRefreshRate = applyVariableRefreshRate
        };
        _autoTdp = new DeviceCoordinatorNativeQamAutoTdpService(deviceCoordinator, autoTdp);
        _controllerTarget = new DeviceCoordinatorNativeQamControllerTargetService(deviceCoordinator);
        _audio = audio is null ? null : new AudioManagerNativeQamAudioService(audio);
        _audioFormat = audio is null || audioProfiles is null
            ? null
            : new NativeQamAudioFormatService(audio, audioProfiles);
        _network = radios is null
            ? null
            : new NativeQamNetworkService(
                radios,
                () => !_disposed && _networkIndicatorEnabled,
                QueueStatePublication);
        _bluetooth = radios is null ? null : new NativeQamBluetoothService(radios, showBluetoothPanel);
        _ownsBrightness = brightness is null;
        _brightness = brightness ?? new NativeQamBrightnessService(
            () => !_disposed && _enabled,
            QueueStatePublication);
        _brightness.Changed += QueueStatePublication;
        var modules = new SteamUiModuleSet(CreateModules());
        // WSGM's composed asset and the module-derived vocabulary, named here rather than reached
        // for from inside the bridge.
        _bridge = new SteamUiBridgeHost(
            _transport,
            new SteamUiInjectedAsset(
                SteamUiAssetCatalog.LoadNativeQamBootstrap(),
                SteamUiAssetCatalog.NativeQamBootstrapSha256),
            modules.AllowedCommands);
        _patches = new SteamUiPatchManager(_transport);
        _patches.Register(new SteamUiBridgePatch(_bridge));
        _patches.Register(_overlayActivation);
        _patches.SetPatchEnabled(_overlayActivation.Id, false);
        modules.RegisterPatches(_patches);
        SetPatchStates(false, false);
        SetGlyphDeliveryPatchStates();
        _patches.SetGlobalEnabled(false);
        // Traffic in both directions is the runtime's; which patches are applied when stays here,
        // because that is this application's policy and not a general rule.
        // The library badge can be the only thing on: it reports the Home layout back and needs
        // its libraries published, so both directions stay open for it without native Quick Access.
        _runtime = new SteamUiModuleRuntime(
            _bridge,
            modules,
            () =>
                _enabled || _pluginSteamUiEnabled || _libraryBadgeEnabled || _homeCarouselEnabled
                || _screensaverEnabled,
            BootstrapWanted);
        _runtime.ModuleFailed += OnModuleFailed;
        _transport.GenerationChanged += OnGenerationChanged;
        LibraryBadges.Changed += OnSemanticStateChanged;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed += OnSemanticStateChanged;
        }

        _tdp.StateChanged += OnSemanticStateChanged;
        _deviceControls.StateChanged += OnSemanticStateChanged;
        _autoTdp.StateChanged += OnSemanticStateChanged;
        _onPerformanceStateChanged = _ => QueueStatePublication();
        _performanceService.StateChanged += _onPerformanceStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
        // A profile change moves the game-override markers even when no device value changed.
        _profiles = profiles;
        _onProfilesChanged = (_, _) => QueueStatePublication();
        if (_profiles is not null)
        {
            _profiles.Changed += _onProfilesChanged;
        }

        if (_pluginSteamUi is not null)
        {
            _onPluginSteamUiChanged = QueueStatePublication;
            _pluginSteamUi.Changed += _onPluginSteamUiChanged;
        }

        if (_audio is not null)
        {
            _audio.StateChanged += OnSemanticStateChanged;
        }

        if (_audioFormat is not null)
        {
            _audioFormat.StateChanged += OnSemanticStateChanged;
        }

        _synchronization = Task.Run(SynchronizeLoopAsync);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisableAsync().ConfigureAwait(false);
        _disposed = true;
        _brightness.Changed -= QueueStatePublication;
        if (_ownsBrightness)
        {
            _brightness.Dispose();
        }

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

        _transport.GenerationChanged -= OnGenerationChanged;
        LibraryBadges.Changed -= OnSemanticStateChanged;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed -= OnSemanticStateChanged;
        }

        _tdp.StateChanged -= OnSemanticStateChanged;
        _deviceControls.StateChanged -= OnSemanticStateChanged;
        _performanceService.StateChanged -= _onPerformanceStateChanged;
        _autoTdp.StateChanged -= OnSemanticStateChanged;
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
        if (_profiles is not null)
        {
            _profiles.Changed -= _onProfilesChanged;
        }

        if (_pluginSteamUi is not null && _onPluginSteamUiChanged is not null)
        {
            _pluginSteamUi.Changed -= _onPluginSteamUiChanged;
            _pluginSteamUi.Dispose();
        }

        if (_audio is not null)
        {
            _audio.StateChanged -= OnSemanticStateChanged;
        }

        if (_audioFormat is not null)
        {
            _audioFormat.StateChanged -= OnSemanticStateChanged;
            _audioFormat.Dispose();
        }

        _enabled = false;
        ReleasePerformanceObservation();
        // The runtime first: it stops answering, cancels what is in flight and drains its own
        // request tasks, so nothing is still writing to the bridge when that is disposed below.
        _runtime.ModuleFailed -= OnModuleFailed;
        await _runtime.DisposeAsync().ConfigureAwait(false);
        // ReSharper disable once MethodHasAsyncOverload
        _shutdown.Cancel();
        try
        {
            await _synchronization.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _patches.DisposeAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _autoTdp.Dispose();
        _audio?.Dispose();
        _controllerTarget.Dispose();
        _deviceControls.Dispose();
        _tdp.Dispose();
        _synchronizeSignal.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Observes native surface lifetime independently of custom QAM rows.</summary>
    internal void ApplySurfaceObservation(bool enabled)
    {
        if (_disposed || _surfaceObservationEnabled == enabled)
        {
            return;
        }

        _surfaceObservationEnabled = enabled;
        _patches.SetPatchEnabled(_overlayActivation.Id, enabled);
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        QueueSynchronization();
    }

    internal void Apply(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
            SetPatchStates(true, true);
        }
        else
        {
            CancelAllInflightRequests();
            ReleasePerformanceObservation();
            SetPatchStates(IndependentSurfacesEnabled(), false);
        }

        QueueSynchronization();
    }

    /// <summary>Shows host-rendered common-plugin commands in Steam while the CEF master is on.</summary>
    internal void ApplyPluginSteamUi(bool enabled)
    {
        if (_disposed || _pluginSteamUi is null || _pluginSteamUiEnabled == enabled)
        {
            return;
        }

        _pluginSteamUiEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        SetPatchStates(BootstrapWanted(), _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Feeds Steam's header and Internet page through the registered network gate.</summary>
    /// <param name="enabled">Whether the game-mode Wi-Fi projection is active.</param>
    internal void ApplyNetworkIndicator(bool enabled)
    {
        if (_disposed || _networkIndicatorEnabled == enabled)
        {
            return;
        }

        _networkIndicatorEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }
        else if (!_enabled && _network is not null)
        {
            _network.PostStopScanning();
        }

        SetPatchStates(BootstrapWanted(), _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Applies download-queue sorting through the shared patch lifecycle.</summary>
    /// <param name="enabled">Whether the MainWindow wrapper should be installed.</param>
    internal void ApplyDownloadSort(bool enabled)
    {
        if (_disposed || _downloadSortEnabled == enabled)
        {
            return;
        }

        _downloadSortEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        SetPatchStates(BootstrapWanted(), _enabled);
        QueueSynchronization();
    }

    /// <summary>Shows or retracts the library badge on Steam's library tiles.</summary>
    /// <param name="enabled">Whether the badge surface should be claimed and fed.</param>
    /// <remarks>
    ///     Independent of native Quick Access, like download sorting: the badge belongs to the card
    ///     manager feature, and a session with Quick Access off still names the card a game is on.
    /// </remarks>
    internal void ApplyLibraryBadge(bool enabled)
    {
        if (_disposed || _libraryBadgeEnabled == enabled)
        {
            return;
        }

        _libraryBadgeEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        SetPatchStates(BootstrapWanted(), _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Claims or retracts Home's carousel, and publishes whether it lists uninstalled games.</summary>
    /// <param name="enabled">Whether Home's carousel lists the libraries attached right now.</param>
    /// <param name="includeUninstalled">Whether it also lists owned games that are not installed.</param>
    /// <remarks>
    ///     Independent of native Quick Access, like the library badge. The preference alone changing is
    ///     a publication, not a patch change, so the carousel re-orders without being retracted.
    /// </remarks>
    internal void ApplyHomeCarousel(bool enabled, bool includeUninstalled)
    {
        if (_disposed)
        {
            return;
        }

        var preferenceChanged = _carouselShowUninstalled != includeUninstalled;
        _carouselShowUninstalled = includeUninstalled;
        if (_homeCarouselEnabled == enabled)
        {
            if (preferenceChanged)
            {
                QueueStatePublication();
            }

            return;
        }

        _homeCarouselEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        SetPatchStates(BootstrapWanted(), _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Adds or retracts WSGM's display-off rows in Steam's Screensaver settings.</summary>
    /// <param name="enabled">Whether the rows should be drawn and Steam's screensaver timeouts heard.</param>
    /// <remarks>
    ///     Independent of native Quick Access: the rows edit the same display-off timeouts as the overlay's
    ///     Power page, and hearing Steam's screensaver timeout is what keeps the display from turning off
    ///     before the screensaver can start. Not declared at all without a session timeout owner.
    /// </remarks>
    internal void ApplyScreensaverTimeouts(bool enabled)
    {
        if (_disposed || _displayTimeouts is null || _screensaverEnabled == enabled)
        {
            return;
        }

        _screensaverEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }
        else
        {
            _displayTimeouts.ForgetSteam();
        }

        SetPatchStates(BootstrapWanted(), _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Whether any surface that runs without native Quick Access is on.</summary>
    /// <remarks>
    ///     Download sort counts: it registers its transform on the toolkit's shared JSX-runtime claim,
    ///     which the bridge serves.
    /// </remarks>
    private bool IndependentSurfacesEnabled()
    {
        return _networkIndicatorEnabled
               || _pluginSteamUiEnabled
               || _libraryBadgeEnabled
               || _homeCarouselEnabled
               || _screensaverEnabled
               || _downloadSortEnabled;
    }

    /// <summary>Whether the bridge bootstrap is needed: native QAM, or a surface that works without it.</summary>
    private bool BootstrapWanted()
    {
        return _enabled || IndependentSurfacesEnabled();
    }

    /// <summary>Returns the immutable patch-registry view used by diagnostics and isolated tests.</summary>
    internal IReadOnlyList<SteamUiPatchSnapshot> GetPatchSnapshots()
    {
        return _patches.GetSnapshots();
    }

    /// <summary>
    ///     Applies handheld glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <param name="enabled">Whether WSGM presents handheld glyphs at all.</param>
    /// <param name="profile">The resolved plugin profile, including its control availability.</param>
    /// <param name="nativeArtwork">Keeps Valve artwork while still hiding absent controls.</param>
    /// <remarks>
    ///     One call because there is one thing to install. The profile is the plugin's and is the only
    ///     source of artwork; WSGM turns it into a stylesheet. Either switch off, or a profile that
    ///     supplies no artwork or absent controls, removes WSGM's stylesheet. Native artwork selection
    ///     retains the active plugin's control filtering.
    /// </remarks>
    internal void ApplyGlyphs(bool enabled, ImportedGlyphProfile? profile, bool nativeArtwork = false)
    {
        if (_disposed)
        {
            return;
        }

        _glyphsEnabled = enabled;
        _glyphDeliveryState.Update(enabled ? profile : null, nativeArtwork);
        SetGlyphDeliveryPatchStates();
        if (_glyphDeliveryEnabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        QueueSynchronization();
    }

    internal async Task DisableAsync()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = false;
        _pluginSteamUiEnabled = false;
        _networkIndicatorEnabled = false;
        _downloadSortEnabled = false;
        _libraryBadgeEnabled = false;
        _homeCarouselEnabled = false;
        _screensaverEnabled = false;
        _glyphsEnabled = false;
        _surfaceObservationEnabled = false;
        // ReSharper disable once MethodHasAsyncOverload
        _patches.SetPatchEnabled(_overlayActivation.Id, false);
        CancelAllInflightRequests();
        ReleasePerformanceObservation();
        if (_network is not null)
        {
            await _network.StopScanningAsync().ConfigureAwait(false);
        }

        SetPatchStates(true, false);
        SetGlyphDeliveryPatchStates();
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
        _glyphDeliveryState.Update(null);
        SetPatchStates(false, false);
        await _patches.SetGlobalEnabledAsync(false, _shutdown.Token).ConfigureAwait(false);
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            // A semantic operation is authorized against one execution-context/document pair.
            // Letting it continue after either generation moved could apply a result for a page
            // that can no longer receive its response, so replacement is cancellation just like
            // an explicit bridge cancel.
            CancelAllInflightRequests();
            ReleasePerformanceObservation();
            // A new document is a new client until its screensaver surface reports again.
            _displayTimeouts?.ForgetSteam();
        }

        // The patch manager marks patches for every changed target role, so every role change must
        // queue synchronization.
        // Every surface switch belongs here: a Steam restart replaces the SharedJSContext
        // generation, and a surface that is the only thing on would otherwise never be reapplied.
        if (BootstrapWanted()
            || _glyphsEnabled
            || _glyphDeliveryEnabled
            || _surfaceObservationEnabled)
        {
            QueueSynchronization();
        }
    }

    private void QueueSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            _synchronizeSignal.Release();
        }
    }

    private async Task SynchronizeLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _synchronizeSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPending, 0);
                await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
                ReconcileScreensaverReport();
                // Every surface that runs without native Quick Access keeps the bootstrap up. Only
                // the network indicator used to count here, so with Quick Access off the library
                // badge, the Home carousel and the screensaver rows were retracted after each pass.
                if (BootstrapWanted())
                {
                    if (_enabled)
                    {
                        UpdatePerformanceObservation();
                    }
                    else
                    {
                        ReleasePerformanceObservation();
                    }

                    QueueStatePublication();
                }
                else
                {
                    ReleasePerformanceObservation();
                    SetPatchStates(false, false);
                    await _patches.SetGlobalEnabledAsync(
                            _downloadSortEnabled || _glyphsEnabled || _glyphDeliveryEnabled ||
                            _surfaceObservationEnabled,
                            _shutdown.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI patch synchronization failed: {ex.Message}");
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
    ///     delivery — are patches of WSGM's own and are declared beside them.
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
                "tdp",
                _profileOverrides),

            SteamAutoTdpRow.Module(Enabled, () => new ValueTask<SteamAutoTdpState?>(_autoTdp.Current), _autoTdp),

            // The frame limit is the toolkit's unified row rather than Valve's notch slider, and
            // the Q12 retirement does not apply: a free 30-120 range made Valve's unusable.
            SteamFrameLimitRow.Module(Enabled, () => new ValueTask<SteamFrameLimitState?>(_performance.FrameLimit),
                _performance, overrides: _profileOverrides),
            SteamPowerProfileRow.Module(Enabled, _powerProfiles.ReadAsync, _powerProfiles),
            SteamHybridCoreRow.Module(Enabled, _hybridCores.ReadAsync, _hybridCores),
            SteamPowerPresetRow.Module(Enabled, _powerPresets.ReadAsync, _powerPresets),

            SteamControllerTargetRow.Module(
                Enabled,
                () => new ValueTask<SteamControllerTargetState?>(_controllerTarget.Current),
                _controllerTarget,
                overrides: _profileOverrides),

            // Declared unconditionally — whether the switch appears is decided by whether the
            // device publishes a variable-refresh capability, which the state carries.
            SteamVariableRefreshRow.Module(Enabled, () => new ValueTask<SteamVariableRefreshState?>(_performance.Vrr),
                _performance, overrides: _profileOverrides),

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
                _deviceControls,
                overrides: _profileOverrides),

            new SteamUiModule("download-sort", [new SteamDownloadSortPatch()]),

            new SteamUiModule(
                "glyph-style",
                [new SteamInputGlyphStylePatch(_glyphDeliveryState)]),

            // The library badge on every library tile, fed from the card model. Declared
            // unconditionally: which cards exist is the reading's business, and a session with
            // none publishes an empty list, which names every installed game as internal.
            SteamLibraryBadgeSurface.Module(
                () => _libraryBadgeEnabled,
                () => new ValueTask<SteamLibraryBadgeState?>(LibraryBadges.Current),
                _libraryBadge),

            // Home's carousel, built from the same card reading as the badge so the two never
            // disagree about which card is in the reader. Rides LibraryBadges.Changed for card moves.
            SteamHomeCarouselSurface.Module(
                () => _homeCarouselEnabled,
                () => new ValueTask<SteamHomeCarouselState?>(HomeCarousel.Build(LibraryBadges.Current,
                    _carouselShowUninstalled)),
                _homeCarousel)
        ];

        // Steam's game menu. Declared unconditionally: WSGM's own Change Artwork entry is in it
        // whether or not a plugin contributes anything.
        modules.Add(SteamGameContextMenuSurface.Module(
            Enabled,
            () => new ValueTask<SteamGameContextMenuState?>(_gameContextMenu.ReadState()),
            _gameContextMenu));

        // Every custom route in one module. The page host owns one patch and one publication, so a
        // second page owner cannot register the same patch id and take the whole session down with
        // it; each owner contributes routes and the host merges them.
        modules.Add(SteamPageSurface.Module(
            Enabled,
            () => new ValueTask<SteamPageState?>(ReadPages())));

        if (_artwork is { } artwork)
        {
            modules.Add(SteamArtworkBrowserSurface.Module(
                Enabled,
                () => new ValueTask<SteamArtworkBrowserState?>(artwork.ReadState()),
                artwork));
        }

        if (_libraryImport is { } libraryImport)
        {
            modules.Add(SteamLibraryImportSurface.Module(
                Enabled,
                () => new ValueTask<SteamLibraryImportState?>(libraryImport.ReadState()),
                libraryImport));
        }

        // The plugin tab. Declared unconditionally: WSGM's own tools are on it whether or not any
        // package is installed, which is the state every install was actually in.
        modules.Add(SteamExtensionsTabSurface.Module(
            Enabled,
            () => new ValueTask<SteamExtensionsTabState?>(_extensionsTab.ReadState()),
            _extensionsTab));

        if (_pluginSteamUi is not null)
        {
            modules.AddRange(_pluginModules);
        }

        // WSGM's display-off rows in Steam's Screensaver settings, over the same timeouts the overlay
        // edits. Reading goes to Windows each time, so an overlay change reaches Steam on the next
        // publication and a change made in Windows reaches it when the page next opens.
        if (_displayTimeouts is { } timeouts)
        {
            modules.Add(SteamScreensaverSurface.Module(
                () => _screensaverEnabled,
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
                // The one publication not gated on _enabled alone: the header Wi-Fi indicator is
                // shown on the desktop side too, where the rest of the QAM is not.
                () => _enabled || _networkIndicatorEnabled,
                () => network.ReadStateAsync(_networkIndicatorEnabled),
                network));
        }

        if (_bluetooth is not null)
        {
            modules.Add(SteamBluetoothSurface.Module(Enabled, _bluetooth.ReadStateAsync, _bluetooth));
        }

        // Reading is synchronous: both managers keep their own state and this only projects it,
        // so there is nothing to await and no reason to hop threads to answer Steam.
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
        return _enabled;
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

        if (_pluginSteamUiEnabled && _pluginSteamUi is { } pluginSteamUi)
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

    // The runtime refuses a quarantined module's traffic for the rest of its life, so this side
    // has to match it. Retracting the patches here alone was not enough: the next Quick Access
    // enable cycle ran SetPatchStates, which knows only the feature switches, and mounted the
    // surface again with nothing behind it.
    private void OnModuleFailed(object? sender, SteamUiModuleFailure failure)
    {
        lock (_failedPatchGate)
        {
            foreach (var patch in failure.Module.Patches)
            {
                _failedPatchIds.Add(patch.Id);
            }
        }

        foreach (var patch in failure.Module.Patches)
        {
            _patches.SetPatchEnabled(patch.Id, false);
        }

        Log.Warn($"Steam UI module {failure.Module.Id} was disabled after {failure.Operation}: {failure.Error}");
        QueueSynchronization();
    }

    private bool Quarantined(string patchId)
    {
        lock (_failedPatchGate)
        {
            return _failedPatchIds.Count > 0 && _failedPatchIds.Contains(patchId);
        }
    }

    private void QueueStatePublication()
    {
        _runtime.QueuePublication();
    }

    private void SetPatchStates(bool bootstrap, bool components)
    {
        // The registry is the source of truth for which patches exist; a hand-kept id list here
        // drifts. Glyphs and download sorting have independent switches; the network gate may also
        // outlive native QAM to keep the configured header indicator.
        foreach (var patch in _patches.GetSnapshots())
        {
            if (patch.Id == SteamInputGlyphStylePatch.PatchId || patch.Id == _overlayActivation.Id)
            {
                continue;
            }

            var enabled = patch.Id switch
            {
                SteamDownloadSortPatch.PatchId => _downloadSortEnabled,
                SteamLibraryBadgeSurface.PatchId or SteamLibraryBadgeSurface.DetailsPatchId => _libraryBadgeEnabled,
                SteamHomeCarouselSurface.PatchId => _homeCarouselEnabled,
                SteamScreensaverSurface.PatchId => _screensaverEnabled,
                SteamUiBridgePatch.PatchId => bootstrap,
                SteamNetworkSurface.PatchId => components || _networkIndicatorEnabled,
                _ when _pluginPatchIds.Contains(patch.Id) => _pluginSteamUiEnabled,
                _ => components
            };
            _patches.SetPatchEnabled(patch.Id, enabled && !Quarantined(patch.Id));
        }
    }

    /// <summary>
    ///     Enables the one glyph stylesheet when the active plugin profile supplies something to draw.
    /// </summary>
    /// <remarks>
    ///     One switch, because there is one stylesheet. The previous four independent tier switches
    ///     existed to gate four separate mapping namespaces; a single stylesheet either has rules or it
    ///     does not, and the patch itself refuses to apply an empty one.
    /// </remarks>
    private void SetGlyphDeliveryPatchStates()
    {
        var presentation = _glyphDeliveryState.Current;
        // Absent controls count as rules. A reviewed profile may legitimately carry nothing but
        // them — hiding trackpad or extra-paddle rows on a handheld that has neither, while keeping
        // Valve's own artwork — and SteamGlyphCss.Build emits real hiding rules for exactly that.
        // Requiring a resource or an image left those profiles with no stylesheet at all, so the
        // controls the device does not have stayed on screen.
        var deliver = _glyphsEnabled
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
            + $"setting={_glyphsEnabled}, profile={presentation is not null}, "
            + $"stableResources={presentation?.StableResources.Count ?? 0}, "
            + $"controllerImages={presentation?.ControllerImages.Count ?? 0}, "
            + $"absentControls={presentation?.AbsentControls.Count ?? 0}",
            deliver ? LogLevel.Info : LogLevel.Warn);
        _patches.SetPatchEnabled(SteamInputGlyphStylePatch.PatchId, deliver);
        _glyphDeliveryEnabled = deliver;
    }

    private void UpdatePerformanceObservation()
    {
        // RTSS polling exists for rendered native controls, not merely for the session. A failed
        // fingerprint or lost bridge generation therefore releases the shared service lease.
        // The rows that actually render, whichever they are — WSGM's own frame limit and Valve's
        // overlay level. Observation must follow the mounted rows or it never starts.
        var performancePatchVerified = _patches.GetSnapshots().Any(snapshot =>
            (snapshot.Id == SteamFrameLimitRow.PatchId || snapshot.Id == SteamPerformanceSurface.OverlayLevelRow.Id)
            && snapshot.State == SteamUiPatchState.Verified);
        var shouldObserve = _enabled && _bridge.IsReady && performancePatchVerified;
        if (!shouldObserve)
        {
            ReleasePerformanceObservation();
            return;
        }

        lock (_observationGate)
        {
            if (!_enabled || !_bridge.IsReady)
            {
                return;
            }

            _performanceObservation ??= _performanceService.AcquireObservation();
        }
    }

    private void ReleasePerformanceObservation()
    {
        IDisposable? observation;
        lock (_observationGate)
        {
            observation = _performanceObservation;
            _performanceObservation = null;
        }

        observation?.Dispose();
    }

    private void CancelAllInflightRequests()
    {
        _runtime.CancelAllInflight();
    }
}
