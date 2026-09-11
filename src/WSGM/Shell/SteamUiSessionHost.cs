using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit.Surfaces;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

/// <summary>
/// Owns the narrow bridge and registered patches over the injected process-long Steam UI transport.
/// </summary>
/// <remarks>
/// Session lifetime only: which patches are applied when, generation changes, synchronization, and
/// publication gating. The surfaces themselves — what each gate installs, which rows mount, how a
/// payload is read — are the toolkit's; this host feeds them WSGM's data through the backend
/// services and decides which are on.
/// </remarks>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    private const string ShellPatchId = "wsgm.native-qam.shell";
    private readonly ISteamUiTransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _synchronizeSignal = new(0, 1);
    private readonly object _observationGate = new();
    private readonly SteamUiModuleSet _modules;
    private readonly SteamUiModuleRuntime _runtime;
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly DeviceCoordinatorNativeQamTdpService _tdp;
    private readonly DeviceCoordinatorNativeQamDeviceControlsService _deviceControls;
    private readonly DeviceCoordinatorNativeQamAutoTdpService _autoTdp;
    private readonly DeviceCoordinatorNativeQamControllerTargetService _controllerTarget;
    private readonly NativeQamBrightnessService _brightness;
    private readonly bool _ownsBrightness;
    private readonly NativeQamPowerPresetService _powerPresets;
    private readonly NativeQamPowerProfileService _powerProfiles = new(PowerSchemes.Windows,
        id => ConfigStore.Mutate(config => config.LastSelectedPowerSchemeId = id));
    private readonly NativeQamHybridCoreService _hybridCores = new(HybridCores.Windows);

    /// <summary>
    /// Null when no audio manager exists for this session, which is the overlay-test case.
    /// </summary>
    /// <remarks>
    /// Unlike the semantic services above there is no "unavailable" stand-in, because audio is
    /// supplied as a namespace rather than drawn as a row: with nothing to supply, the right
    /// behaviour is to leave the namespace absent so Steam's own store stays unavailable, not to
    /// install one that answers with nothing.
    /// </remarks>
    private readonly AudioManagerNativeQamAudioService? _audio;

    /// <summary>The Wi-Fi surface, or null when this session has no radio manager.</summary>
    private readonly NativeQamNetworkService? _network;

    /// <summary>The Bluetooth surface, riding the same radio-manager condition.</summary>
    private readonly NativeQamBluetoothService? _bluetooth;

    /// <summary>
    /// Steam's revived storage pages over WSGM's own eject, format and library registration, or
    /// null when this session has no storage managers to answer with.
    /// </summary>
    private readonly SteamStorageBridge? _storage;

    /// <summary>Hears the library badge's Home layout report.</summary>
    private readonly LibraryBadgeBackend _libraryBadge = new();

    private readonly PerformanceService _performanceService;
    private readonly PerformanceServiceNativeQamAdapter _performance;
    private readonly Action<PerformanceState> _onPerformanceStateChanged;

    /// <summary>
    /// The display-resolution row's backend, or null when this session must not move the display.
    /// </summary>
    /// <remarks>
    /// Null in overlay-test, which runs without a real display to change. The patch is not
    /// registered at all in that case, so the row cannot appear and offer a control with nothing
    /// behind it.
    /// </remarks>
    private readonly NativeQamResolutionService? _resolution;
    private readonly SteamInputGlyphDeliveryState _glyphDeliveryState = new();
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiPatchManager _patches;
    private readonly SteamOverlayActivationPatch _overlayActivation = new();
    private readonly Task _synchronization;
    private int _signalPending;
    private IDisposable? _performanceObservation;
    private volatile bool _enabled;
    private volatile bool _networkIndicatorEnabled;
    private volatile bool _downloadSortEnabled;
    private volatile bool _libraryBadgeEnabled;
    private volatile bool _glyphsEnabled;
    private volatile bool _glyphDeliveryEnabled;
    private volatile bool _disposed;
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
    /// What the device can back, for the reactivated performance panel. Supplied by the session
    /// because the frame-limit options come from display-mode discovery and the VRR flag from the
    /// device plugin, and this host owns neither. Null hides every performance control, which is
    /// the correct state for a session that cannot yet say what it can honour.
    /// </param>
    /// <param name="applyRefreshRate">Applies a manually chosen refresh rate, or null.</param>
    /// <param name="applyVariableRefreshRate">Applies the VRR flag, or null.</param>
    /// <param name="showBluetoothPanel">Opens the session's Bluetooth prompt and status surface.</param>
    /// <param name="brightness">Session-owned brightness, or null for a standalone host.</param>
    /// <param name="storage">
    /// The bridge over the session's own storage managers, or null when this session has none —
    /// overlay-test, which owns no drive or format manager to answer with. The surface is then not
    /// declared at all, so Steam's storage pages stay as inert as they are without WSGM rather than
    /// opening onto controls with nothing behind them.
    /// </param>
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
        SteamStorageBridge? storage = null)
    {
        _storage = storage;
        _resolution = resolution is null ? null : new NativeQamResolutionService(resolution);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _tdp = new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _powerPresets = new NativeQamPowerPresetService(deviceCoordinator?.PowerPresets, deviceCoordinator?.PowerAssignments);
        _deviceControls = new DeviceCoordinatorNativeQamDeviceControlsService(deviceCoordinator);
        _performanceService = performance;
        _performance = new PerformanceServiceNativeQamAdapter(performance)
        {
            PerfSupport = perfSupport,
            ApplyRefreshRate = applyRefreshRate,
            ApplyVariableRefreshRate = applyVariableRefreshRate,
        };
        _autoTdp = new DeviceCoordinatorNativeQamAutoTdpService(deviceCoordinator, autoTdp);
        _controllerTarget = new DeviceCoordinatorNativeQamControllerTargetService(deviceCoordinator);
        _audio = audio is null ? null : new AudioManagerNativeQamAudioService(audio);
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
        _modules = new SteamUiModuleSet(CreateModules());
        // WSGM's composed asset and the module-derived vocabulary, named here rather than reached
        // for from inside the bridge.
        _bridge = new SteamUiBridgeHost(
            _transport,
            new SteamUiInjectedAsset(
                SteamUiAssetCatalog.LoadNativeQamBootstrap(),
                SteamUiAssetCatalog.NativeQamBootstrapSha256),
            _modules.AllowedCommands);
        _patches = new SteamUiPatchManager(_transport);
        _patches.Register(new SteamUiBridgePatch(_bridge));
        _patches.Register(_overlayActivation);
        _patches.SetPatchEnabled(_overlayActivation.Id, false);
        _modules.RegisterPatches(_patches);
        SetPatchStates(bootstrap: false, components: false);
        SetGlyphDeliveryPatchStates();
        _patches.SetGlobalEnabled(false);
        // Traffic in both directions is the runtime's; which patches are applied when stays here,
        // because that is this application's policy and not a general rule.
        // The library badge can be the only thing on: it reports the Home layout back and needs
        // its libraries published, so both directions stay open for it without native Quick Access.
        _runtime = new SteamUiModuleRuntime(
            _bridge,
            _modules,
            commandsEnabled: () => _enabled || _libraryBadgeEnabled,
            publishEnabled: () => _enabled || _networkIndicatorEnabled || _libraryBadgeEnabled);
        _transport.GenerationChanged += OnGenerationChanged;
        LibraryBadges.Changed += OnSemanticStateChanged;
        _tdp.StateChanged += OnSemanticStateChanged;
        _deviceControls.StateChanged += OnSemanticStateChanged;
        _autoTdp.StateChanged += OnSemanticStateChanged;
        _onPerformanceStateChanged = _ => QueueStatePublication();
        _performanceService.StateChanged += _onPerformanceStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged += OnSemanticStateChanged;
        }

        _synchronization = Task.Run(SynchronizeLoopAsync);
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
            SetPatchStates(bootstrap: true, components: true);
        }
        else
        {
            CancelAllInflightRequests();
            ReleasePerformanceObservation();
            SetPatchStates(
                bootstrap: _networkIndicatorEnabled || _libraryBadgeEnabled,
                components: false);
        }
        QueueSynchronization();
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
        else if (!_enabled && _network is { } network)
        {
            network.PostStopScanning();
        }
        SetPatchStates(bootstrap: _enabled || enabled || _libraryBadgeEnabled, components: _enabled);
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
        SetPatchStates(
            bootstrap: _enabled || _networkIndicatorEnabled || _libraryBadgeEnabled,
            components: _enabled);
        QueueSynchronization();
    }

    /// <summary>Shows or retracts the library badge on Steam's library tiles.</summary>
    /// <param name="enabled">Whether the badge surface should be claimed and fed.</param>
    /// <remarks>
    /// Independent of native Quick Access, like download sorting: the badge belongs to the card
    /// manager feature, and a session with Quick Access off still names the card a game is on.
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
        SetPatchStates(
            bootstrap: _enabled || _networkIndicatorEnabled || enabled,
            components: _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Returns the immutable patch-registry view used by diagnostics and isolated tests.</summary>
    internal IReadOnlyList<SteamUiPatchSnapshot> GetPatchSnapshots() => _patches.GetSnapshots();

    /// <summary>
    /// Applies handheld glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <param name="enabled">Whether WSGM presents handheld glyphs at all.</param>
    /// <param name="profile">The resolved plugin profile, including its control availability.</param>
    /// <param name="nativeArtwork">Keeps Valve artwork while still hiding absent controls.</param>
    /// <remarks>
    /// One call because there is one thing to install. The profile is the plugin's and is the only
    /// source of artwork; WSGM turns it into a stylesheet. Either switch off, or a profile that
    /// supplies no artwork or absent controls, removes WSGM's stylesheet. Native artwork selection
    /// retains the active plugin's control filtering.
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
        _networkIndicatorEnabled = false;
        _downloadSortEnabled = false;
        _libraryBadgeEnabled = false;
        _glyphsEnabled = false;
        _surfaceObservationEnabled = false;
        _patches.SetPatchEnabled(_overlayActivation.Id, false);
        CancelAllInflightRequests();
        ReleasePerformanceObservation();
        if (_network is { } network)
        {
            await network.StopScanningAsync().ConfigureAwait(false);
        }
        SetPatchStates(bootstrap: true, components: false);
        SetGlyphDeliveryPatchStates();
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
        _glyphDeliveryState.Update(null);
        SetPatchStates(bootstrap: false, components: false);
        _patches.SetGlobalEnabled(false);
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
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
        }

        // The patch manager marks patches for every changed target role, so every role change must
        // queue synchronization.
        if (_enabled
            || _networkIndicatorEnabled
            || _downloadSortEnabled
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
                if (_enabled || _networkIndicatorEnabled)
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
                    SetPatchStates(bootstrap: false, components: false);
                    _patches.SetGlobalEnabled(
                        _downloadSortEnabled || _glyphsEnabled || _glyphDeliveryEnabled || _surfaceObservationEnabled);
                    await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
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

    /// <summary>
    /// Every Steam UI surface this session offers, one declaration each: which toolkit surface it
    /// is, the state WSGM feeds it, and the backend that answers it.
    /// </summary>
    /// <remarks>
    /// The toolkit owns each surface's patches, wire shapes and payload readers, so a module here is
    /// exactly "this is our data, and it maps to that feature". A surface whose backend is absent
    /// in this session is simply not declared. WSGM's own features — download sorting and glyph
    /// delivery — are patches of WSGM's own and are declared beside them.
    /// </remarks>
    private IReadOnlyList<ISteamUiModule> CreateModules()
    {
        List<ISteamUiModule> modules =
        [
            new SteamUiModule(
                "shell",
                commands: [new(ShellPatchId, "toggleQuickAccess", HandleToggleQuickAccessAsync)]),

            // Valve's TDP toggle and slider, fed the primary power limit's range and routed back to
            // the device capability.
            SteamPowerLimitSurface.Module(
                Enabled,
                () => new(_tdp.PowerLimit),
                _tdp,
                id: "tdp"),

            SteamAutoTdpRow.Module(Enabled, () => new(_autoTdp.Current), _autoTdp),

            // The frame limit is the toolkit's unified row rather than Valve's notch slider, and
            // the Q12 retirement does not apply: a free 30-120 range made Valve's unusable.
            SteamFrameLimitRow.Module(Enabled, () => new(_performance.FrameLimit), _performance),
            SteamPowerProfileRow.Module(Enabled, _powerProfiles.ReadAsync, _powerProfiles),
            SteamHybridCoreRow.Module(Enabled, _hybridCores.ReadAsync, _hybridCores),
            SteamPowerPresetRow.Module(Enabled, _powerPresets.ReadAsync, _powerPresets),

            SteamControllerTargetRow.Module(
                Enabled,
                () => new(_controllerTarget.Current),
                _controllerTarget),

            // Declared unconditionally — whether the switch appears is decided by whether the
            // device publishes a variable-refresh capability, which the state carries.
            SteamVariableRefreshRow.Module(Enabled, () => new(_performance.Vrr), _performance),

            // The backend behind Valve's own Performance tab and the Valve rows that read it.
            // Declared unconditionally because the performance service always exists; what the
            // panel then shows is decided entirely by which fields the projected state carries.
            SteamPerformanceSurface.Module(
                Enabled,
                () => new(_performance.PerfState),
                _performance,
                id: "perf"),

            // Declared unconditionally: the panel backlight depends on nothing WSGM has to supply.
            SteamBrightnessSurface.Module(Enabled, _brightness.ReadAsync, _brightness),

            SteamDeviceControlsRow.Module(
                Enabled,
                () => new(_deviceControls.Current),
                _deviceControls),

            new SteamUiModule("download-sort", patches: [new SteamDownloadSortPatch()]),

            new SteamUiModule(
                "glyph-style",
                patches: [new SteamInputGlyphStylePatch(_glyphDeliveryState)]),

            // The library badge on every library tile, fed from the card model. Declared
            // unconditionally: which cards exist is the reading's business, and a session with
            // none publishes an empty list, which names every installed game as internal.
            SteamLibraryBadgeSurface.Module(
                () => _libraryBadgeEnabled,
                () => new(LibraryBadges.Current),
                _libraryBadge),
        ];

        if (_resolution is { } resolution)
        {
            modules.Add(SteamResolutionRow.Module(
                Enabled,
                () => new(resolution.Current),
                resolution));
        }

        if (_audio is { } audio)
        {
            // Publishing once after injection updates the store whose availability was cached when
            // Steam started before the replacement namespace existed.
            modules.Add(SteamAudioSurface.Module(Enabled, () => new(audio.Current), audio));
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

        if (_bluetooth is { } bluetooth)
        {
            modules.Add(SteamBluetoothSurface.Module(Enabled, bluetooth.ReadStateAsync, bluetooth));
        }

        // Reading is synchronous: both managers keep their own state and this only projects it,
        // so there is nothing to await and no reason to hop threads to answer Steam.
        if (_storage is { } storage)
        {
            modules.Add(SteamStorageSurface.Module(Enabled, () => new(storage.ReadState()), storage));
        }

        return modules;
    }

    /// <summary>The publication gate every surface shares: on while native Quick Access is.</summary>
    private bool Enabled() => _enabled;

    private async Task<SteamUiCommandResult> HandleToggleQuickAccessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        bool succeeded = await _toggleQuickAccess(cancellationToken).ConfigureAwait(false);
        return succeeded
            ? SteamUiCommandResult.Applied
            : new(false, "Quick access is not currently available.");
    }

    private void OnSemanticStateChanged() => QueueStatePublication();

    private void QueueStatePublication() => _runtime?.QueuePublication();

    private void SetPatchStates(bool bootstrap, bool components)
    {
        // The registry is the source of truth for which patches exist; a hand-kept id list here
        // drifts. Glyphs and download sorting have independent switches; the network gate may also
        // outlive native QAM to keep the configured header indicator.
        foreach (SteamUiPatchSnapshot patch in _patches.GetSnapshots())
        {
            if (patch.Id == SteamInputGlyphStylePatch.PatchId || patch.Id == _overlayActivation.Id)
            {
                continue;
            }

            _patches.SetPatchEnabled(
                patch.Id,
                patch.Id == SteamDownloadSortPatch.PatchId
                    ? _downloadSortEnabled
                    : patch.Id == SteamLibraryBadgeSurface.PatchId
                        ? _libraryBadgeEnabled
                        : patch.Id == SteamUiBridgePatch.PatchId
                            ? bootstrap
                            : patch.Id == SteamNetworkSurface.PatchId
                                ? components || _networkIndicatorEnabled
                                : components);
        }
    }

    /// <summary>
    /// Enables the one glyph stylesheet when the active plugin profile supplies something to draw.
    /// </summary>
    /// <remarks>
    /// One switch, because there is one stylesheet. The previous four independent tier switches
    /// existed to gate four separate mapping namespaces; a single stylesheet either has rules or it
    /// does not, and the patch itself refuses to apply an empty one.
    /// </remarks>
    private void SetGlyphDeliveryPatchStates()
    {
        SteamInputGlyphPresentation? presentation = _glyphDeliveryState.Current;
        // Absent controls count as rules. A reviewed profile may legitimately carry nothing but
        // them — hiding trackpad or extra-paddle rows on a handheld that has neither, while keeping
        // Valve's own artwork — and SteamGlyphCss.Build emits real hiding rules for exactly that.
        // Requiring a resource or an image left those profiles with no stylesheet at all, so the
        // controls the device does not have stayed on screen.
        bool deliver = _glyphsEnabled
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
        IReadOnlyList<SteamUiPatchSnapshot> snapshots = _patches.GetSnapshots();
        bool performancePatchVerified = false;
        foreach (SteamUiPatchSnapshot snapshot in snapshots)
        {
            // The rows that actually render, whichever they are — WSGM's own frame limit and
            // Valve's overlay level. Observation must follow the mounted rows or it never starts.
            performancePatchVerified |= (snapshot.Id == SteamFrameLimitRow.PatchId
                || snapshot.Id == SteamPerformanceSurface.OverlayLevelRow.Id)
                && snapshot.State == SteamUiPatchState.Verified;
        }
        bool shouldObserve = _enabled && _bridge.IsReady && performancePatchVerified;
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

    private void CancelAllInflightRequests() => _runtime.CancelAllInflight();

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
        if (_ownsBrightness) { _brightness.Dispose(); }
        if (_bluetooth is { } bluetooth) { await bluetooth.StopDiscoveryAsync().ConfigureAwait(false); }
        // A session that ends while Steam's network page is open would otherwise leave the radio
        // sweeping and this host subscribed to a collection it no longer publishes.
        if (_network is { } network)
        {
            await network.DisposeAsync().ConfigureAwait(false);
        }
        _transport.GenerationChanged -= OnGenerationChanged;
        LibraryBadges.Changed -= OnSemanticStateChanged;
        _tdp.StateChanged -= OnSemanticStateChanged;
        _deviceControls.StateChanged -= OnSemanticStateChanged;
        _performanceService.StateChanged -= _onPerformanceStateChanged;
        _autoTdp.StateChanged -= OnSemanticStateChanged;
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged -= OnSemanticStateChanged;
        }

        _enabled = false;
        ReleasePerformanceObservation();
        // The runtime first: it stops answering, cancels what is in flight and drains its own
        // request tasks, so nothing is still writing to the bridge when that is disposed below.
        await _runtime.DisposeAsync().ConfigureAwait(false);
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
}
