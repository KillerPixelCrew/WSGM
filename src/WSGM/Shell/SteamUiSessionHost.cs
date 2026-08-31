using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
// The handler methods below read as this host's own vocabulary, and the module contract is the
// same shape under a name that belongs to the contract rather than to this file.
using SemanticCommandResult = WSGM.Core.SteamUiCommandResult;
using StatePublication = WSGM.Core.SteamUiStatePublication;

namespace WSGM.Shell;

/// <summary>
/// Owns the narrow bridge and registered patches over the injected process-long Steam UI transport.
/// </summary>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    private const string BootstrapPatchId = "wsgm.native-qam.bootstrap";
    private const string ShellPatchId = "wsgm.native-qam.shell";
    private const string TdpPatchId = "wsgm.native-qam.tdp";
    private const string FrameLimitPatchId = "wsgm.native-qam.frame-limit";
    private const string VrrPatchId = "wsgm.native-qam.vrr";
    private const string ValveOverlayLevelPatchId = "wsgm.native-qam.valve-overlay-level";
    private const string AutoTdpPatchId = "wsgm.native-qam.auto-tdp";
    private const string ControllerTargetPatchId = "wsgm.native-qam.controller-target";
    private const string PerfPatchId = "wsgm.native-qam.perf";
    private const string ResolutionPatchId = "wsgm.native-qam.resolution";
    private const string AudioPatchId = "wsgm.native-qam.audio";
    private const string NetworkGatePatchId = "wsgm.steam-network.gate";
    private const string BluetoothPatchId = "wsgm.steam-bluetooth.service";
    private const string BrightnessPatchId = "wsgm.steam-display.brightness";
    private const string DownloadSortPatchId = "wsgm.download-sort";
    private const string GlyphStylePatchId = SteamInputGlyphStylePatch.PatchId;
    private readonly ISteamUiTransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _synchronizeSignal = new(0, 1);
    private readonly SemaphoreSlim _publicationSignal = new(0, 1);
    private readonly object _requestGate = new();
    private readonly object _observationGate = new();
    private readonly Dictionary<long, CancellationTokenSource> _inflightRequests = [];
    private readonly HashSet<Task> _requestTasks = [];
    private readonly SteamUiModuleSet _modules;
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly INativeQamTdpService _tdp;

    /// <summary>
    /// Watches the panel backlight for changes made outside Steam, so the revealed slider follows
    /// them. Field-rooted for its lifetime and disposed with the host, per the long-lived-callback
    /// rule; the read is one ioctl on a handle opened and closed per poll.
    /// </summary>
    private readonly Timer _backlightPoll;
    private readonly Timer _networkPoll;
    private readonly Timer _networkPublishDebounce;
    private int _lastPolledBacklight = -1;

    /// <summary>
    /// Null when no audio manager exists for this session, which is the overlay-test case.
    /// </summary>
    /// <remarks>
    /// Unlike the semantic services above there is no "unavailable" stand-in, because audio is
    /// supplied as a namespace rather than drawn as a row: with nothing to supply, the right
    /// behaviour is to leave the namespace absent so Steam's own store stays unavailable, not to
    /// install one that answers with nothing.
    /// </remarks>
    private readonly INativeQamAudioService? _audio;

    /// <summary>
    /// The session's radio manager, borrowed rather than owned, or null in overlay-test.
    /// </summary>
    /// <remarks>
    /// Only its scanning lifetime is driven from here. Joining, forgetting and the radio toggles
    /// stay with the surfaces that already own them.
    /// </remarks>
    private readonly RadioManager? _radios;

    private readonly PerformanceServiceNativeQamAdapter _performance;

    /// <summary>
    /// The display-resolution row's backend, or null when this session must not move the display.
    /// </summary>
    /// <remarks>
    /// Null in overlay-test, which runs without a real display to change. The patch is not
    /// registered at all in that case, so the row cannot appear and offer a control with nothing
    /// behind it.
    /// </remarks>
    private readonly NativeQamResolutionService? _resolution;
    private readonly INativeQamAutoTdpService _autoTdp;
    private readonly INativeQamControllerTargetService _controllerTarget;
    private readonly SteamInputGlyphDeliveryState _glyphDeliveryState = new();
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiPatchManager _patches;
    private readonly Task _synchronization;
    private readonly Task _publication;
    private int _signalPending;
    private int _publicationPending;
    private IDisposable? _performanceObservation;
    private volatile bool _enabled;
    private volatile bool _networkIndicatorEnabled;
    private volatile bool _downloadSortEnabled;
    private volatile bool _glyphsEnabled;
    private volatile bool _glyphDeliveryEnabled;
    private volatile bool _disposed;

    internal SteamUiSessionHost(
        ISteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        DeviceCoordinator? deviceCoordinator,
        PerformanceService performance,
        AudioManager? audio = null,
        RadioManager? radios = null,
        DisplayResolutionService? resolution = null)
    {
        _radios = radios;
        _resolution = resolution is null ? null : new NativeQamResolutionService(resolution);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _tdp = deviceCoordinator is null
            ? new UnavailableNativeQamTdpService()
            : new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _performance = new PerformanceServiceNativeQamAdapter(performance);
        _autoTdp = deviceCoordinator is null
            ? new UnavailableNativeQamAutoTdpService()
            : new DeviceCoordinatorNativeQamAutoTdpService(deviceCoordinator);
        _controllerTarget = deviceCoordinator is null
            ? new UnavailableNativeQamControllerTargetService()
            : new DeviceCoordinatorNativeQamControllerTargetService(deviceCoordinator);
        _audio = audio is null ? null : new AudioManagerNativeQamAudioService(audio);
        _bridge = new SteamUiBridgeHost(_transport);
        _patches = new SteamUiPatchManager(_transport);
        _modules = new SteamUiModuleSet(CreateModules());
        _modules.RegisterPatches(_patches);
        SetPatchStates(bootstrap: false, components: false);
        SetGlyphDeliveryPatchStates();
        _patches.SetGlobalEnabled(false);
        _bridge.RequestReceived += OnRequestReceived;
        _backlightPoll = new Timer(OnBacklightPoll, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _networkPoll = new Timer(OnNetworkPoll, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        _networkPublishDebounce = new Timer(
            OnNetworkPublishDebounce,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _transport.GenerationChanged += OnGenerationChanged;
        _tdp.StateChanged += OnSemanticStateChanged;
        _autoTdp.StateChanged += OnSemanticStateChanged;
        _performance.StateChanged += OnSemanticStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged += OnSemanticStateChanged;
        }

        _synchronization = Task.Run(SynchronizeLoopAsync);
        _publication = Task.Run(PublishLoopAsync);
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
            SetPatchStates(bootstrap: _networkIndicatorEnabled, components: false);
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
        else if (!_enabled && _radios is { } radios)
        {
            Dispatcher.UIThread.Post(() =>
            {
                radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
                radios.StopScanning();
            });
        }
        SetPatchStates(bootstrap: _enabled || enabled, components: _enabled);
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
        SetPatchStates(bootstrap: _enabled || _networkIndicatorEnabled, components: _enabled);
        QueueSynchronization();
    }

    /// <summary>Returns the immutable patch-registry view used by diagnostics and isolated tests.</summary>
    internal IReadOnlyList<SteamUiPatchSnapshot> GetPatchSnapshots() => _patches.GetSnapshots();

    /// <summary>
    /// Applies handheld glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <param name="enabled">Whether WSGM presents handheld glyphs at all.</param>
    /// <param name="profile">The resolved plugin profile, or null for native Steam glyphs.</param>
    /// <remarks>
    /// One call because there is one thing to install. The profile is the plugin's and is the only
    /// source of artwork; WSGM turns it into a stylesheet. Either switch off, or a profile that
    /// supplies nothing to draw, removes WSGM's stylesheet and leaves native Valve glyphs in place.
    /// </remarks>
    internal void ApplyGlyphs(bool enabled, ImportedGlyphProfile? profile)
    {
        if (_disposed)
        {
            return;
        }

        _glyphsEnabled = enabled;
        _glyphDeliveryState.Update(enabled ? profile : null);
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
        _glyphsEnabled = false;
        CancelAllInflightRequests();
        ReleasePerformanceObservation();
        if (_radios is { } radios)
        {
            await RunUiAsync(() =>
            {
                radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
                radios.StopScanning();
            }).ConfigureAwait(false);
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
            || _glyphDeliveryEnabled)
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
                        _downloadSortEnabled || _glyphsEnabled || _glyphDeliveryEnabled);
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
    /// Every Steam UI surface this session offers, one declaration each: the patches that install
    /// it, the state it publishes, and the commands it answers.
    /// </summary>
    /// <remarks>
    /// A surface used to be four separate edits in this file — a registration, a publication row, a
    /// command row and an id constant — which is how one could be added with a control that
    /// rendered and did nothing. Here the whole surface is one entry, and a module whose backend is
    /// absent is simply not declared.
    /// </remarks>
    private IReadOnlyList<ISteamUiModule> CreateModules()
    {
        List<ISteamUiModule> modules =
        [
            new SteamUiModule("bootstrap", patches: [new NativeQamBootstrapPatch(_bridge)]),

            new SteamUiModule(
                "shell",
                commands: [new(ShellPatchId, "toggleQuickAccess", HandleToggleQuickAccessAsync)]),

            // The SteamOS Manager RPC seam and the rows it reveals, in place of the hand-rolled TDP
            // row. The gate supplies the one answer the Windows client's service stub never fills
            // in — is_tdp_limit_available and the watt range — and watches the steamos_tdp_limit
            // client settings Valve's rows write, routing them to hardware. Both halves share the
            // wsgm.native-qam.tdp id and its published state, so nothing else had to move.
            new SteamUiModule(
                "tdp",
                patches: [new NativeQamSteamOsManagerPatch(), new NativeQamValveTdpPatch()],
                publications: [Publish(TdpPatchId, () => JsonSerializer.SerializeToElement(
                    _tdp.Current,
                    NativeQamSemanticJsonContext.Default.NativeQamTdpState))],
                commands: [new(TdpPatchId, "setPrimaryLimit", HandlePowerLimitAsync)]),

            new SteamUiModule(
                "auto-tdp",
                patches: [new NativeQamAutoTdpPatch()],
                publications: [Publish(AutoTdpPatchId, () => JsonSerializer.SerializeToElement(
                    _autoTdp.Current,
                    NativeQamSemanticJsonContext.Default.NativeQamAutoTdpState))],
                commands: [new(AutoTdpPatchId, "setAutoTdp", HandleAutoTdpAsync)]),

            // The frame limit is WSGM's own row, deliberately, and this is the one place the Q12
            // retirement does not apply. Valve's component is a NOTCH slider fed by
            // fps_limit_options, and SteamOS itself stopped working that way when it unified frame
            // limit and refresh rate into one continuous slider labelled "60 FPS (60 Hz)" —
            // verified against a Steam Deck. Feeding the notch row a free 30-120 range put 91
            // labels in a strip that fits about twelve, and the row became unusable above the first
            // few. The cap is a free number and the PAIRING is what snaps, so the row has to be
            // notchless.
            new SteamUiModule(
                "frame-limit",
                patches: [new NativeQamFrameLimitPatch()],
                publications: [Publish(FrameLimitPatchId, () => JsonSerializer.SerializeToElement(
                    _performance.FrameLimit,
                    NativeQamSemanticJsonContext.Default.NativeQamFrameLimitState))],
                commands:
                [
                    new(FrameLimitPatchId, "setFrameLimit", HandleFrameLimitAsync),
                    new(FrameLimitPatchId, "setRefreshRate", HandleRefreshRateAsync),
                ]),

            // The overlay level stays Valve's: it is genuinely five discrete levels.
            new SteamUiModule(
                "overlay-level",
                patches: [new NativeQamValveOverlayLevelPatch()]),

            new SteamUiModule(
                "controller-target",
                patches: [new NativeQamControllerTargetPatch()],
                publications:
                [
                    Publish(ControllerTargetPatchId, () => JsonSerializer.SerializeToElement(
                        _controllerTarget.Current,
                        NativeQamSemanticJsonContext.Default.NativeQamControllerTargetState)),
                ],
                commands:
                [
                    new(ControllerTargetPatchId, "setControllerTarget", HandleControllerTargetAsync),
                ]),

            // WSGM's own VRR switch, not Valve's. Valve's component is gated on a react-query over
            // SteamClient.System.DisplayManager, which this client does not define: the query never
            // succeeds and the component returns null before it reads anything WSGM publishes, so
            // the row was simply absent. Declared unconditionally — whether it appears is decided
            // by whether the device publishes a variable-refresh capability, which the state
            // carries.
            new SteamUiModule(
                "vrr",
                patches:
                [
                    new NativeQamVrrPatch(),
                    new NativeQamValveProfileHeaderPatch(),
                    new NativeQamValveResetPatch(),
                    new NativeQamValveRefreshRatePatch(),
                ],
                publications: [Publish(VrrPatchId, () => JsonSerializer.SerializeToElement(
                    _performance.Vrr,
                    NativeQamSemanticJsonContext.Default.NativeQamVrrState))],
                commands: [new(VrrPatchId, "setVariableRefreshRate", HandleVrrAsync)]),

            // The backend behind Valve's own Performance tab. Declared unconditionally because the
            // performance service always exists; what the panel then shows is decided entirely by
            // which fields the projected state carry, not by whether this patch installed.
            // Valve's protobuf field names stay on their dedicated source-generated boundary.
            new SteamUiModule(
                "perf",
                patches: [new NativeQamPerfPatch()],
                publications: [Publish(PerfPatchId, () => JsonSerializer.SerializeToElement(
                    _performance.PerfState,
                    NativeQamPerfJsonContext.Default.NativeQamPerfState))],
                commands: [new(PerfPatchId, "updateSettings", HandlePerformanceDeltaAsync)]),

            // No backend of WSGM's behind it: Steam's own brightness backend already works on
            // Windows, and only its availability flag says otherwise. Declared unconditionally for
            // that reason — it depends on nothing WSGM has to supply.
            new SteamUiModule(
                "brightness",
                patches: [new SteamBrightnessGatePatch()],
                publications: [new(BrightnessPatchId, () => _enabled, ReadBrightnessPublication)],
                commands: [new(BrightnessPatchId, "setBrightness", HandleBrightnessAsync)]),

            new SteamUiModule("download-sort", patches: [new SteamDownloadSortPatch()]),

            new SteamUiModule(
                "glyph-style",
                patches: [new SteamInputGlyphStylePatch(_glyphDeliveryState)]),
        ];

        if (_resolution is { } resolution)
        {
            modules.Add(new SteamUiModule(
                "resolution",
                patches: [new NativeQamResolutionPatch()],
                publications: [Publish(ResolutionPatchId, () => JsonSerializer.SerializeToElement(
                    resolution.Current,
                    NativeQamSemanticJsonContext.Default.NativeQamResolutionState))],
                commands: [new(ResolutionPatchId, "setResolution", HandleResolutionAsync)]));
        }

        if (_audio is { } audio)
        {
            // Publishing once after injection updates the store whose availability was cached when
            // Steam started before the replacement namespace existed.
            modules.Add(new SteamUiModule(
                "audio",
                patches: [new NativeQamAudioPatch()],
                publications:
                [
                    Publish(AudioPatchId, () => SerializeAudioState(audio.Current)),
                ],
                commands:
                [
                    new(AudioPatchId, "getDevices", HandleAudioDevicesAsync),
                    new(AudioPatchId, "setDefaultDevice", HandleAudioDeviceAsync),
                    new(AudioPatchId, "setVolume", HandleAudioVolumeAsync),
                ]));
        }

        // The gate reveals Steam's Wi-Fi surface, and the surface is only worth revealing if
        // something can populate it — which is the radio manager. Bluetooth rides the same
        // condition for the same reason.
        if (_radios is { } radios)
        {
            modules.Add(new SteamUiModule(
                "network",
                patches: [new SteamNetworkGatePatch()],
                publications:
                [
                    // The one publication not gated on _enabled alone: the header Wi-Fi indicator
                    // is shown on the desktop side too, where the rest of the QAM is not.
                    new(NetworkGatePatchId,
                        () => _enabled || _networkIndicatorEnabled,
                        async () => JsonSerializer.SerializeToElement(
                            await ReadNetworkStateAsync(_networkIndicatorEnabled)
                                .ConfigureAwait(false),
                            NativeQamSemanticJsonContext.Default.SteamNetworkState)),
                ],
                commands:
                [
                    new(NetworkGatePatchId, "startScan", HandleNetworkScanStartAsync),
                    new(NetworkGatePatchId, "stopScan", HandleNetworkScanStopAsync),
                ]));

            modules.Add(new SteamUiModule(
                "bluetooth",
                patches: [new SteamBluetoothServicePatch()],
                publications:
                [
                    new(BluetoothPatchId, () => _enabled, async () =>
                        JsonSerializer.SerializeToElement(
                            await ReadBluetoothStateAsync(radios).ConfigureAwait(false),
                            NativeQamSemanticJsonContext.Default.SteamBluetoothState)),
                ],
                commands:
                [
                    .. BluetoothCommands.Select(command =>
                        new SteamUiCommandHandler(BluetoothPatchId, command, HandleBluetoothAsync)),
                ]));
        }

        return modules;
    }

    /// <summary>The Bluetooth commands, all answered by one handler that switches on the name.</summary>
    private static readonly string[] BluetoothCommands =
    [
        "setDiscovering",
        "pair",
        "cancelPair",
        "connect",
        "disconnect",
        "forget",
        "setTrusted",
        "setWakeAllowed",
    ];

    /// <summary>Publishes a value that is always readable while the session is enabled.</summary>
    private StatePublication Publish(string patchId, Func<JsonElement> read) =>
        new(patchId, () => _enabled, () => Ready(read()));

    private static ValueTask<JsonElement?> Ready(JsonElement payload) =>
        ValueTask.FromResult<JsonElement?>(payload);

    private static ValueTask<JsonElement?> ReadBrightnessPublication() =>
        Backlight.TryReadBrightness(out int percent)
            ? Ready(JsonSerializer.SerializeToElement(
                new SteamBrightnessState(percent),
                NativeQamSemanticJsonContext.Default.SteamBrightnessState))
            : ValueTask.FromResult<JsonElement?>(null);

    private async Task<SemanticCommandResult> HandleToggleQuickAccessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        bool succeeded = await _toggleQuickAccess(cancellationToken).ConfigureAwait(false);
        return succeeded
            ? SemanticCommandResult.Applied
            : new(false, "Quick access is not currently available.");
    }

    private async Task<SemanticCommandResult> HandlePowerLimitAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadPowerLimitPayload(request.Payload, out int watts, out bool enabled))
        {
            return new(false, "The primary power-limit payload is invalid.");
        }
        if (enabled)
        {
            return From(await _tdp.SetPrimaryLimitAsync(watts, cancellationToken)
                .ConfigureAwait(false));
        }
        if (_tdp.Current.MaximumWatts is not int ceiling)
        {
            const string error = "The device does not report a power-limit ceiling to release to.";
            Log.Warn($"Native QAM power limit release refused: {error}");
            return new(false, error);
        }

        Log.Info(
            "Native QAM power limit released to the device ceiling "
            + $"{ceiling} W: Steam's TDP toggle is off (slider holds {watts} W).");
        return From(await _tdp.SetPrimaryLimitAsync(ceiling, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleFrameLimitAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadPerformancePayload(
            request.Payload,
            out int value,
            out PerformancePersistenceTarget persistence))
        {
            return new(false, "The frame-limit payload is invalid.");
        }
        return From(await _performance.SetAsync(
            PerformanceControl.FrameLimit,
            value,
            persistence,
            CorrelationId(request),
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleRefreshRateAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadPerformancePayload(
            request.Payload,
            out int hz,
            out PerformancePersistenceTarget _))
        {
            return new(false, "The refresh-rate payload is invalid.");
        }
        return From(await _performance.ApplyRefreshRateAsync(hz, cancellationToken)
            .ConfigureAwait(false));
    }

    private Task<SemanticCommandResult> HandleBrightnessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadIntegerProperty(request.Payload, "percent", out int percent)
            || percent is < 0 or > 100)
        {
            return Task.FromResult(new SemanticCommandResult(
                false,
                "The brightness payload is invalid."));
        }
        return Task.FromResult(Backlight.TrySetBrightness(percent)
            ? SemanticCommandResult.Applied
            : new SemanticCommandResult(false, "The panel backlight refused the write."));
    }

    private async Task<SemanticCommandResult> HandleVrrAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadEnabledPayload(request.Payload, out bool enabled))
        {
            return new(false, "The variable-refresh payload is invalid.");
        }
        return From(await _performance.ApplyVariableRefreshRateAsync(enabled, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleAutoTdpAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadEnabledPayload(request.Payload, out bool enabled))
        {
            return new(false, "The AutoTDP payload is invalid.");
        }
        return From(await _autoTdp.SetEnabledAsync(enabled, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleControllerTargetAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryReadTargetPayload(request.Payload, out string target))
        {
            return new(false, "The controller-target payload is invalid.");
        }
        return From(await _controllerTarget.SetTargetAsync(target, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleResolutionAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_resolution is null)
        {
            return SemanticCommandResult.Refused;
        }
        if (!TryReadTargetPayload(request.Payload, out string value))
        {
            return new(false, "The resolution payload is invalid.");
        }
        return From(await _resolution.ApplyAsync(value, cancellationToken).ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandlePerformanceDeltaAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        (bool succeeded, string? error) = await ApplyPerfDeltaAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return new(succeeded, error);
    }

    private Task<SemanticCommandResult> HandleAudioDevicesAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(_audio is { } audio
            ? new SemanticCommandResult(true, null, SerializeAudioState(audio.Current))
            : SemanticCommandResult.Refused);

    private async Task<SemanticCommandResult> HandleAudioDeviceAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_audio is null)
        {
            return SemanticCommandResult.Refused;
        }
        if (!TryReadAudioDevicePayload(request.Payload, out string id, out bool input))
        {
            return new(false, "The audio device payload is invalid.");
        }
        return From(await _audio.SetDefaultDeviceAsync(id, input, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleAudioVolumeAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_audio is null)
        {
            return SemanticCommandResult.Refused;
        }
        if (!TryReadAudioVolumePayload(request.Payload, out int percent))
        {
            return new(false, "The audio volume payload is invalid.");
        }
        return From(await _audio.SetVolumeAsync(percent, cancellationToken).ConfigureAwait(false));
    }

    private async Task<SemanticCommandResult> HandleNetworkScanStartAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_radios is null)
        {
            return SemanticCommandResult.Refused;
        }
        await RunUiAsync(() =>
        {
            _radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
            _radios.Networks.CollectionChanged += OnScannedNetworksChanged;
            _radios.StartScanning();
        }).ConfigureAwait(false);
        QueueNetworkPublication();
        return SemanticCommandResult.Applied;
    }

    private async Task<SemanticCommandResult> HandleNetworkScanStopAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_radios is null)
        {
            return SemanticCommandResult.Refused;
        }
        await RunUiAsync(() =>
        {
            _radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
            _radios.StopScanning();
        }).ConfigureAwait(false);
        return SemanticCommandResult.Applied;
    }

    private async Task<SemanticCommandResult> HandleBluetoothAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (_radios is null)
        {
            return SemanticCommandResult.Refused;
        }
        (bool succeeded, string? error) = await ExecuteBluetoothAsync(
            _radios,
            request,
            cancellationToken).ConfigureAwait(false);
        return new(succeeded, error);
    }

    private static SemanticCommandResult From(NativeQamCommandResult result) =>
        new(result.Succeeded, result.Error);

    private async Task PublishLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _publicationSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _publicationPending, 0);
                if ((!_enabled && !_networkIndicatorEnabled) || !_bridge.IsReady)
                {
                    continue;
                }

                foreach (StatePublication publication in _modules.Publications)
                {
                    if (!publication.Enabled())
                    {
                        continue;
                    }
                    JsonElement? payload = await publication.Read().ConfigureAwait(false);
                    if (payload is { } state)
                    {
                        await _bridge.PublishStateAsync(
                                publication.PatchId,
                                state,
                                _shutdown.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI semantic state publication failed: {ex.Message}");
            }
        }
    }

    private void OnSemanticStateChanged() => QueueStatePublication();

    /// <remarks>
    /// Queues a publication only on an actual level change, so a stable backlight costs one ioctl
    /// every two seconds and no bridge traffic at all. Steam's slider writes land back here too —
    /// that is one transition per drag, which keeps the slider and the panel agreeing without a
    /// second mechanism.
    /// </remarks>
    private void OnBacklightPoll(object? state)
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        if (!Backlight.TryReadBrightness(out int percent)
            || percent == Interlocked.Exchange(ref _lastPolledBacklight, percent))
        {
            return;
        }

        Log.Change("display.backlight", $"Panel backlight at {percent}%.");
        QueueStatePublication();
    }

    private void OnNetworkPoll(object? state)
    {
        if (!_disposed && _networkIndicatorEnabled)
        {
            QueueStatePublication();
        }
    }

    private void OnScannedNetworksChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueNetworkPublication();

    private void QueueNetworkPublication() =>
        _networkPublishDebounce.Change(
            TimeSpan.FromMilliseconds(400),
            Timeout.InfiniteTimeSpan);

    private void OnNetworkPublishDebounce(object? state) => QueueStatePublication();

    private void QueueStatePublication()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _publicationPending, 1) == 0)
        {
            _publicationSignal.Release();
        }
    }

    private void SetPatchStates(bool bootstrap, bool components)
    {
        // The registry is the source of truth. Hand-listing ids here drifted to 8 of 17 registered
        // patches; disabling native QAM removed the bridge while the omitted patches kept cycling
        // Applying -> Degraded against it. Glyphs and download sorting have independent switches;
        // the network gate may also outlive native QAM to keep the configured header indicator.
        foreach (SteamUiPatchSnapshot patch in _patches.GetSnapshots())
        {
            if (patch.Id == GlyphStylePatchId)
            {
                continue;
            }

            _patches.SetPatchEnabled(
                patch.Id,
                patch.Id == DownloadSortPatchId
                    ? _downloadSortEnabled
                    : patch.Id == BootstrapPatchId
                        ? bootstrap
                        : patch.Id == NetworkGatePatchId
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
            deliver ? "info " : "warn ");
        _patches.SetPatchEnabled(GlyphStylePatchId, deliver);
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
            performancePatchVerified |= (snapshot.Id is FrameLimitPatchId
                or ValveOverlayLevelPatchId)
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

            _performanceObservation ??= _performance.AcquireObservation();
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

    private void OnRequestReceived(object? sender, SteamUiBridgeRequest request)
    {
        if (request.Type == "cancel")
        {
            CancelInflightRequest(request.Sequence);
            return;
        }

        Task task = RespondToRequestAsync(request);
        lock (_requestGate)
        {
            _requestTasks.Add(task);
        }
        _ = ObserveRequestCompletionAsync(task);
    }

    private async Task RespondToRequestAsync(SteamUiBridgeRequest request)
    {
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_requestGate)
        {
            if (!_inflightRequests.TryAdd(request.Sequence, requestCancellation))
            {
                return;
            }
        }

        SemanticCommandResult outcome;
        try
        {
            if (!_enabled)
            {
                outcome = SemanticCommandResult.Refused;
            }
            else if (!_modules.TryGetCommand(
                request.PatchId,
                request.Command,
                out SteamUiCommandDelegate? handler)
                || handler is null)
            {
                outcome = SemanticCommandResult.Refused;
            }
            else
            {
                outcome = await handler(request, requestCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            RemoveInflightRequest(request.Sequence);
            return;
        }
        catch (Exception ex)
        {
            outcome = new SemanticCommandResult(false, ex.Message);
        }

        bool succeeded = outcome.Succeeded;
        string? error = outcome.Error;
        JsonElement? responsePayload = outcome.Payload;

        // Every refusal, named. The reason was built here and handed straight back to the injected
        // side, which has nowhere to put it — so a control the user operated that quietly did
        // nothing left no trace at all on this side of the bridge. That is exactly the defect the
        // repository rules call the most expensive recurring one, and it cost a session: Steam had
        // a 28 W limit stored, the gate had forwarded it, and the EC was still at 30 W with not one
        // line saying why.
        //
        // Log.Change keyed per patch and command, because a gate can repeat a refused write on its
        // own schedule: the first prints, the repeats are counted.
        if (!succeeded)
        {
            Log.Change(
                $"steam.ui.request.{request.PatchId}.{request.Command}",
                $"Steam UI request {request.PatchId}/{request.Command} did nothing: "
                    + (error ?? "no reason reported"),
                "warn ");
        }

        try
        {
            if (requestCancellation.IsCancellationRequested)
            {
                return;
            }

            await _bridge.RespondAsync(
                    request,
                    succeeded,
                    responsePayload,
                    error,
                    requestCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI bridge response failed: {ex.Message}");
        }
        finally
        {
            RemoveInflightRequest(request.Sequence);
        }
    }

    private void CancelInflightRequest(long sequence)
    {
        CancellationTokenSource? cancellation;
        lock (_requestGate)
        {
            _inflightRequests.TryGetValue(sequence, out cancellation);
        }

        CancelSafely(cancellation);
    }

    private void CancelAllInflightRequests()
    {
        CancellationTokenSource[] inflight;
        lock (_requestGate)
        {
            inflight = [.. _inflightRequests.Values];
        }

        foreach (CancellationTokenSource cancellation in inflight)
        {
            CancelSafely(cancellation);
        }
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request completed between the bounded lookup and cancellation.
        }
    }

    private void RemoveInflightRequest(long sequence)
    {
        lock (_requestGate)
        {
            _inflightRequests.Remove(sequence);
        }
    }

    private async Task ObserveRequestCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI semantic request failed unexpectedly: {ex.Message}");
        }
        finally
        {
            lock (_requestGate)
            {
                _requestTasks.Remove(task);
            }
        }
    }

    /// <summary>Reads the power-limit payload: the watts and the switch beside them.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="watts">The limit the slider is sitting on, when this returns true.</param>
    /// <param name="enabled">Whether the limit applies at all, when this returns true.</param>
    /// <returns>Whether the payload was readable.</returns>
    /// <remarks>
    /// Its own reader because this command carries two fields. The switch is not optional: a limit
    /// switched off still carries the watts the slider holds, and reading only the number would
    /// apply a cap the user had just turned off.
    /// </remarks>
    private static bool TryReadPowerLimitPayload(
        JsonElement payload,
        out int watts,
        out bool enabled)
    {
        watts = default;
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("watts", out JsonElement wattsProperty)
            || wattsProperty.ValueKind != JsonValueKind.Number
            || !wattsProperty.TryGetInt32(out watts)
            || !payload.TryGetProperty("enabled", out JsonElement enabledProperty)
            || enabledProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        enabled = enabledProperty.ValueKind is JsonValueKind.True;
        return true;
    }

    /// <summary>Reads one required integer without imposing an unrelated object-arity rule.</summary>
    private static bool TryReadIntegerProperty(
        JsonElement payload,
        string propertyName,
        out int value)
    {
        value = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out value))
        {
            return false;
        }

        return true;
    }

    /// <summary>Reads the one boolean an AutoTDP request may carry.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="enabled">Receives the requested state.</param>
    /// <returns>Whether the payload was exactly one boolean named <c>enabled</c>.</returns>
    /// <remarks>
    /// Exact rather than lenient, matching the target payload beside it. The page is WSGM's own
    /// script, so anything else arriving here is either a defect or something that is not WSGM,
    /// and neither should reach a setting.
    /// </remarks>
    private static bool TryReadEnabledPayload(JsonElement payload, out bool enabled)
    {
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("enabled", out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        if (propertyCount != 1)
        {
            return false;
        }

        enabled = property.GetBoolean();
        return true;
    }

    /// <summary>
    /// Reads the radio manager's Bluetooth view into the shape Steam's panel consumes.
    /// </summary>
    /// <param name="radios">The session's radio manager.</param>
    /// <returns>The state to publish.</returns>
    /// <remarks>
    /// Reported unavailable when the radio is off rather than as an empty device list. Steam's panel
    /// distinguishes the two — "Bluetooth is off" is a state a user can act on, while an empty list
    /// reads as "nothing found" and invites them to keep waiting for devices that will never arrive.
    /// </remarks>
    private static async Task<SteamBluetoothState> ReadBluetoothStateAsync(RadioManager radios)
    {
        List<SteamBluetoothDevice> devices = [];
        bool available = false;
        bool enabled = false;
        bool discovering = false;
        await RunUiAsync(() =>
        {
            // Available means "this machine has a Bluetooth radio WSGM can drive", never "the radio
            // is on". Wiring it to the on/off state made turning Bluetooth off remove the entire
            // settings page and the toggle with it — the exact control needed to turn it back on.
            available = radios.BluetoothPower
                is not RadioPower.Absent and not RadioPower.Disabled;
            enabled = radios.BluetoothOn;
            discovering = radios.BluetoothScanning;
            foreach (BluetoothDeviceEntry entry in radios.BluetoothDevices)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                devices.Add(new SteamBluetoothDevice(
                    entry.Id,
                    string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                    entry.Id,
                    // Steam's generic device type. WSGM does not classify Bluetooth devices, and a
                    // guessed class would put the wrong icon beside a real device.
                    0,
                    entry.Paired,
                    entry.Connected));
            }
        }).ConfigureAwait(false);

        return new SteamBluetoothState(available, enabled, discovering, devices);
    }

    /// <summary>
    /// Carries out one Bluetooth operation from Steam's own pairing UI.
    /// </summary>
    /// <param name="radios">The session's radio manager.</param>
    /// <param name="request">The bridge request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it succeeded, and why not when it did not.</returns>
    /// <remarks>
    /// Pairing has no direct call: <see cref="RadioManager"/> drives it through a prompt the user
    /// answers, and inventing a headless pair here would either bypass a PIN confirmation the
    /// device requires or silently fail on one that does. Steam's Pair button therefore starts
    /// discovery and lets the existing prompt flow run, which is the same path the taskbar uses.
    /// <para>
    /// Trusted and wake-allowed are accepted and do nothing. They are Linux BlueZ concepts with no
    /// Windows equivalent, and refusing them would make Steam's UI report a failure for a control
    /// that was never going to change anything.
    /// </para>
    /// </remarks>
    private static async Task<(bool Succeeded, string? Error)> ExecuteBluetoothAsync(
        RadioManager radios,
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Command is "setDiscovering")
        {
            if (!TryReadEnabledPayload(request.Payload, out bool discovering))
            {
                return (false, "The discovery payload is invalid.");
            }

            // BluetoothScanning is manager-owned and driven by the same sweep as Wi-Fi, so
            // discovery goes through the scanning lifecycle rather than being set directly. One
            // sweep covering both radios is also what the taskbar's panel does.
            await RunUiAsync(() =>
            {
                if (discovering)
                {
                    radios.StartScanning();
                }
                else
                {
                    radios.StopScanning();
                }
            }).ConfigureAwait(false);
            return (true, null);
        }

        if (request.Command is "setTrusted" or "setWakeAllowed")
        {
            Log.Info($"Bluetooth: '{request.Command}' accepted with no Windows equivalent.");
            return (true, null);
        }

        if (!TryReadDeviceIdPayload(request.Payload, out string deviceId))
        {
            return (false, "The Bluetooth device payload is invalid.");
        }

        BluetoothDeviceEntry? device = null;
        await RunUiAsync(() => device = radios.BluetoothDevices.FirstOrDefault(entry =>
            string.Equals(entry.Id, deviceId, StringComparison.Ordinal))).ConfigureAwait(false);
        if (device is null)
        {
            Log.Warn($"Bluetooth: '{deviceId}' is no longer present.");
            return (false, "That device is no longer present.");
        }

        switch (request.Command)
        {
            case "connect":
                await radios.SetAudioConnectionAsync(device, connect: true).ConfigureAwait(false);
                return (true, null);
            case "disconnect":
                await radios.SetAudioConnectionAsync(device, connect: false).ConfigureAwait(false);
                return (true, null);
            case "forget":
                await radios.UnpairAsync(device).ConfigureAwait(false);
                return (true, null);
            case "pair":
                // Discovery drives the prompt; the user answers it exactly as they do from the
                // taskbar's radio panel.
                await RunUiAsync(radios.StartScanning).ConfigureAwait(false);
                return (true, null);
            case "cancelPair":
                await RunUiAsync(radios.StopScanning).ConfigureAwait(false);
                return (true, null);
            default:
                return (false, "The requested semantic service is not active.");
        }
    }

    private static bool TryReadDeviceIdPayload(JsonElement payload, out string deviceId)
    {
        deviceId = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("device", out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 256)
        {
            return false;
        }

        deviceId = candidate;
        return true;
    }

    private async Task<SteamNetworkState> ReadNetworkStateAsync(bool indicatorEnabled)
    {
        List<SteamNetworkAccessPointState> networks = [];
        if (_radios is { } radios)
        {
            await RunUiAsync(() =>
            {
                foreach (WifiNetworkEntry entry in radios.Networks.Take(24))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Ssid))
                    {
                        networks.Add(new SteamNetworkAccessPointState(
                            entry.Ssid,
                            MapNetworkStrength(entry.Signal),
                            entry.Secured,
                            entry.Connected));
                    }
                }
            }).ConfigureAwait(false);
        }

        WindowsRadio.WifiStatus connected = indicatorEnabled
            ? WindowsRadio.GetWifiStatus()
            : default;
        if (indicatorEnabled
            && connected.State == 0
            && !string.IsNullOrWhiteSpace(connected.Ssid))
        {
            int existing = networks.FindIndex(network =>
                string.Equals(network.Ssid, connected.Ssid, StringComparison.Ordinal));
            var joined = new SteamNetworkAccessPointState(
                connected.Ssid,
                MapNetworkStrength(connected.Signal),
                existing >= 0 ? networks[existing].Secured : true,
                true);
            if (existing >= 0)
            {
                networks[existing] = joined;
            }
            else
            {
                networks.Insert(0, joined);
                if (networks.Count > 24)
                {
                    networks.RemoveAt(networks.Count - 1);
                }
            }
        }

        return new SteamNetworkState(networks);
    }

    internal static int MapNetworkStrength(int signalPercent) => signalPercent switch
    {
        >= 75 => 4,
        >= 50 => 3,
        >= 25 => 2,
        _ => 1,
    };

    /// <summary>
    /// Runs a radio-manager call on the UI thread.
    /// </summary>
    /// <param name="action">The call to make.</param>
    /// <returns>A task completing after it ran.</returns>
    /// <remarks>
    /// <see cref="RadioManager"/> reconciles observable collections the taskbar binds to, so its
    /// scanning calls are UI-thread owned. Requests arrive off the bridge's own thread.
    /// </remarks>
    private static Task RunUiAsync(Action action) =>
        Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <summary>Reads the endpoint and direction of a default-device change.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="id">The endpoint identifier.</param>
    /// <param name="input">Whether the capture default is being set.</param>
    /// <returns><see langword="true"/> when the payload is exactly the expected shape.</returns>
    private static bool TryReadAudioDevicePayload(JsonElement payload, out string id, out bool input)
    {
        id = string.Empty;
        input = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("id", out JsonElement idProperty)
            || idProperty.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("input", out JsonElement inputProperty)
            || inputProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        string? candidate = idProperty.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512)
        {
            return false;
        }

        id = candidate;
        input = inputProperty.ValueKind is JsonValueKind.True;
        return true;
    }

    /// <summary>Reads the target volume of a volume change.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="percent">The requested volume, 0-100.</param>
    /// <returns><see langword="true"/> when the payload is exactly the expected shape.</returns>
    private static bool TryReadAudioVolumePayload(JsonElement payload, out int percent)
    {
        percent = 0;
        return payload.ValueKind is JsonValueKind.Object
            && payload.TryGetProperty("percent", out JsonElement property)
            && property.ValueKind is JsonValueKind.Number
            && property.TryGetInt32(out percent)
            && percent is >= 0 and <= 100;
    }

    /// <summary>Serializes the audio state into the shape Steam's store reads.</summary>
    /// <param name="state">The state to send.</param>
    /// <returns>The payload element.</returns>
    /// <remarks>
    /// The device shape here is deliberately WSGM's own; the bootstrap maps it into Steam's field
    /// names. Emitting Steam's names on this side would put its schema in two places, and the one
    /// that changes with a client rebuild is the injected half.
    /// </remarks>
    private static JsonElement SerializeAudioState(NativeQamAudioState state)
    {
        string json = JsonSerializer.Serialize(
            state,
            NativeQamSemanticJsonContext.Default.NativeQamAudioState);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool TryReadTargetPayload(JsonElement payload, out string target)
    {
        target = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("target", out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        string? candidate = property.GetString();
        if (propertyCount != 1
            || candidate is not { Length: >= 1 and <= 64 }
            || !ValidTargetId(candidate))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    private static bool TryReadPerformancePayload(
        JsonElement payload,
        out int value,
        out PerformancePersistenceTarget persistence)
    {
        value = default;
        persistence = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("value", out JsonElement valueProperty)
            || valueProperty.ValueKind != JsonValueKind.Number
            || !valueProperty.TryGetInt32(out value)
            || !payload.TryGetProperty("persistence", out JsonElement persistenceProperty)
            || persistenceProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        persistence = persistenceProperty.GetString() switch
        {
            "automatic" => PerformancePersistenceTarget.Automatic,
            "global" => PerformancePersistenceTarget.Global,
            "application" => PerformancePersistenceTarget.Application,
            _ => (PerformancePersistenceTarget)(-1),
        };
        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        return propertyCount == 2
            && persistence is PerformancePersistenceTarget.Automatic
                or PerformancePersistenceTarget.Global
                or PerformancePersistenceTarget.Application;
    }

    /// <summary>Supplies what the device can back, for the reactivated performance panel.</summary>
    /// <param name="support">Reads the current support, or null to report nothing supported.</param>
    /// <remarks>
    /// Set by the session rather than resolved here: the frame-limit options come from display-mode
    /// discovery and the VRR flag from the device plugin, and this host owns neither. Until it is
    /// set, every performance control stays hidden, which is the correct state for a session that
    /// cannot yet say what it can honour.
    /// </remarks>
    internal void SetPerfSupport(Func<NativeQamPerfSupport>? support)
    {
        _performance.PerfSupport = support;
        QueueStatePublication();
    }

    /// <summary>Supplies the way to apply a manually chosen refresh rate.</summary>
    /// <param name="applyRefreshRate">Applies a rate, reporting whether it took, or null.</param>
    /// <remarks>
    /// Separate from the support projection because they answer different questions: that one
    /// decides whether the row is drawn, this decides whether its writes go anywhere. Both are the
    /// session's to answer, and a row drawn without this would be a control that refuses every
    /// change it offers.
    /// </remarks>
    internal void SetRefreshRateApply(Func<int, bool>? applyRefreshRate)
        => _performance.ApplyRefreshRate = applyRefreshRate;

    /// <summary>Supplies the way to turn variable refresh rate on or off.</summary>
    /// <param name="applyVrr">Applies the flag, reporting whether it took, or null.</param>
    internal void SetVariableRefreshRateApply(Func<bool, CancellationToken, Task<bool>>? applyVrr)
        => _performance.ApplyVariableRefreshRate = applyVrr;

    /// <summary>Applies one <c>UpdateSettings</c> call from Steam's own performance panel.</summary>
    /// <param name="request">The forwarded request.</param>
    /// <param name="cancellationToken">Cancels the applies.</param>
    /// <returns>Whether every recognized change applied, and the first failure if not.</returns>
    /// <remarks>
    /// Every setter in Valve's store funnels through the one <c>UpdateSettings</c> method, so a
    /// single call can carry several changes and each is applied in the order it arrived — a delta
    /// that turns the cap on and sets it in one message must not apply the two out of order.
    /// <para>
    /// Failures are collected rather than aborting: refusing the rest of a delta because one field
    /// has no backend would drop settings WSGM can honour, and the panel's own state would then
    /// disagree with the device until the next publish.
    /// </para>
    /// </remarks>
    private async Task<(bool Succeeded, string? Error)> ApplyPerfDeltaAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (!NativeQamPerfDeltaReader.TryRead(
                request.Payload,
                out NativeQamPerfDelta delta,
                out string? readError))
        {
            Log.Warn($"Native QAM performance delta refused: {readError}");
            return (false, readError);
        }

        if (delta.Unsupported.Count > 0)
        {
            // Named, because the alternative is a control the user operates that quietly does
            // nothing and cannot be diagnosed from a pasted log.
            Log.Warn(
                "Native QAM performance delta carried fields with no WSGM backend: "
                + string.Join(", ", delta.Unsupported));
        }

        if (delta.ResetToDefault)
        {
            Log.Info(
                "Native QAM performance reset requested for "
                + $"{(delta.SteamAppId is { } id ? $"AppID {id}" : "the global profile")}.");
            NativeQamCommandResult reset = await _performance.ResetProfileAsync(cancellationToken)
                .ConfigureAwait(false);

            // A reset arrives on its own, not alongside value changes: Valve's button sends only
            // this flag. Returning here rather than falling through keeps that explicit.
            return (reset.Succeeded, reset.Error);
        }

        if (delta.Recognized.Count == 0)
        {
            Log.Info(
                "Native QAM performance delta contained nothing WSGM backs; no change was made.");
            return (false, "The performance delta carried no supported change.");
        }

        string? failure = null;
        foreach (NativeQamPerfChange change in delta.Recognized)
        {
            NativeQamCommandResult result = await _performance.ApplyPerfChangeAsync(
                change,
                CorrelationId(request),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                continue;
            }

            Log.Warn(
                $"Native QAM performance change {change.Kind}={change.Value} failed: "
                + (result.Error ?? "no reason reported"));
            failure ??= result.Error;
        }

        return (failure is null, failure);
    }

    private static string CorrelationId(SteamUiBridgeRequest request) =>
        $"native-qam:{request.ContextGeneration}:{request.DocumentGeneration}:"
        + $"{request.Sequence}:{request.ActionGeneration}";

    private static bool ValidTargetId(string target)
    {
        foreach (char character in target)
        {
            if (!(character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
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
        if (_radios is { } radios)
        {
            radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
        }
        _backlightPoll.Dispose();
        _networkPoll.Dispose();
        _networkPublishDebounce.Dispose();
        _transport.GenerationChanged -= OnGenerationChanged;
        _bridge.RequestReceived -= OnRequestReceived;
        _tdp.StateChanged -= OnSemanticStateChanged;
        _performance.StateChanged -= OnSemanticStateChanged;
        _autoTdp.StateChanged -= OnSemanticStateChanged;
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged -= OnSemanticStateChanged;
        }

        // A session that ends while Steam's network page is open would otherwise leave the radio
        // sweeping and this host subscribed to a collection it no longer publishes.
        if (_radios is { } activeRadios)
        {
            await RunUiAsync(activeRadios.StopScanning).ConfigureAwait(false);
        }

        _enabled = false;
        ReleasePerformanceObservation();
        CancelAllInflightRequests();
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_synchronization, _publication).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] requestTasks;
        lock (_requestGate)
        {
            requestTasks = [.. _requestTasks];
        }
        try
        {
            await Task.WhenAll(requestTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI semantic request cleanup failed: {ex.Message}");
        }

        await _patches.DisposeAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _autoTdp.Dispose();
        _audio?.Dispose();
        _controllerTarget.Dispose();
        _performance.Dispose();
        _tdp.Dispose();
        _synchronizeSignal.Dispose();
        _publicationSignal.Dispose();
        _shutdown.Dispose();
    }
}
