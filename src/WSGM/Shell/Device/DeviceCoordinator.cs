using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibHandheld;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Install;
using WSGM.Interop;
using WSGM.Shared;
using DeviceIdentitySnapshot = LibHandheld.Contracts.DeviceIdentitySnapshot;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using HapticCapabilities = LibHandheld.Contracts.HapticCapabilities;
using HapticOutputFrame = LibHandheld.Contracts.HapticOutputFrame;
using PhysicalDeviceIdentity = LibHandheld.Contracts.PhysicalDeviceIdentity;

namespace WSGM.Shell;

/// <summary>Authoritative process-long owner of the machine-wide hardware cycle.</summary>
internal sealed class DeviceCoordinator : IAsyncDisposable
{
    /// <summary>Machine-wide named marker preventing simultaneous production device owners.</summary>
    internal const string ProductionOwnerName = SessionProtocolNames.DeviceOwner;

    /// <summary>The delay before each automatic restart of a faulted handheld; its length is the restart budget.</summary>
    private static readonly TimeSpan[] AutomaticRestartBackoffs = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    private static readonly TimeSpan CanceledStartCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly Lock _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly DeviceChargeLimitController _chargeLimits;
    private readonly Func<DeviceIdentitySnapshot> _collectIdentity;
    private readonly Func<DeviceIdentitySnapshot, CancellationToken, Task<HandheldDefinition?>> _detectDevice;
    private readonly IAsyncDisposable? _diagnostics;
    private readonly DeviceHapticSink _hapticSink;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Func<HandheldDefinition, DeviceIdentitySnapshot, CancellationToken, string,
            Task<HandheldDeviceRuntime>>
        _loadRuntime;

    private readonly DeviceOemActionRouter _oemActions = new();
    private readonly IDisposable _ownerMutex;

    private readonly Func<TimeSpan, CancellationToken, Task> _restartDelay;
    private readonly uint _sessionId;
    private readonly ConfigStore _store;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private int _automaticRestartAttempts;
    private HandheldDeviceRuntime? _client;
    private AppConfig _config;
    private Task _controllerPublication = Task.CompletedTask;

    /// <summary>Cancels the controller-management start the last publication began.</summary>
    private CancellationTokenSource _controllerStartCancellation = new();

    private volatile bool _disposed;
    private bool _faultRecoveryPending;

    /// <summary>Whether the handheld was last told to own the physical controller. Written under the transition gate.</summary>
    private bool _handheldOwnsController;
    private DeviceIdentitySnapshot? _identity;
    private bool _intentionalStop;

    /// <summary>The lifecycle handler of the attached client, which carries that client.</summary>
    private Action<HandheldRuntimeState>? _lifecycleHandler;

    private Task _resumeRestore = Task.CompletedTask;
    private string? _runningApplicationId;
    private string? _runningExecutable;
    private Task _runtimeRetirement = Task.CompletedTask;
    private Task? _shutdownTask;

    private volatile DeviceCycleState _state = DeviceCycleState.Disabled;

    /// <summary>Composes device ownership, controller routing, profiles, the power policy and the command route.</summary>
    /// <param name="config">Initial normalized saved configuration.</param>
    /// <param name="store">Borrowed process configuration store.</param>
    /// <param name="sessionId">Windows session served by this coordinator and its diagnostics endpoint.</param>
    /// <param name="ownerMutex">
    ///     Handle-owned admission marker transferred for disposal during shutdown; it is never
    ///     mutex-owned.
    /// </param>
    /// <param name="postToUi">Queues router projection notifications on the UI thread.</param>
    /// <param name="profiles">Shared profile owner used for all global and per-game edits.</param>
    /// <param name="autoTdpTargetFrametimeMs">Reads the current frame-time target in milliseconds; zero disables control.</param>
    /// <param name="autoTdpMetrics">Reads the sensor snapshot used by AutoTDP.</param>
    /// <param name="manualVariableRefresh">Persists an explicitly chosen variable-refresh value to the active profile.</param>
    /// <param name="powerModes">Shared Windows power-mode owner borrowed by preset application.</param>
    /// <param name="createControllers">Creates the owned controller manager using this coordinator's physical haptic sink.</param>
    /// <param name="collectIdentity">Collects machine identity for device detection and profile addressing.</param>
    /// <param name="detectDevice">Matches a native definition against the already collected identity without opening hardware.</param>
    /// <param name="loadRuntime">Constructs an owned runtime for the selected definition, cycle, and family state directory.</param>
    /// <param name="registerPowerModeNotification">
    ///     Registers a wake signal; any returned subscription is disposed during
    ///     shutdown.
    /// </param>
    /// <param name="readOnAcPower">Reads AC/battery status; null means unknown.</param>
    /// <param name="restartDelay">Cancelable delay used by the bounded automatic restart policy.</param>
    /// <param name="createDiagnostics">Starts an optional owned read-only diagnostics endpoint after composition succeeds.</param>
    internal DeviceCoordinator(
        AppConfig config,
        ConfigStore store,
        uint sessionId,
        IDisposable ownerMutex,
        Action<Action> postToUi,
        ProfileService profiles,
        Func<double> autoTdpTargetFrametimeMs,
        Func<RtssOsdMetrics> autoTdpMetrics,
        Action<bool> manualVariableRefresh,
        WindowsPowerModes powerModes,
        Func<IPhysicalHapticSink, ControllerManager> createControllers,
        Func<DeviceIdentitySnapshot> collectIdentity,
        Func<DeviceIdentitySnapshot, CancellationToken, Task<HandheldDefinition?>> detectDevice,
        Func<HandheldDefinition, DeviceIdentitySnapshot, CancellationToken, string, Task<HandheldDeviceRuntime>>
            loadRuntime,
        Func<Action, IDisposable?> registerPowerModeNotification,
        Func<bool?> readOnAcPower,
        Func<TimeSpan, CancellationToken, Task> restartDelay,
        Func<uint, Func<DeviceCoordinatorDiagnosticsSnapshot>, IAsyncDisposable?> createDiagnostics)
    {
        _config = config;
        _store = store;
        Profiles = profiles;
        _sessionId = sessionId;
        _ownerMutex = ownerMutex;
        _collectIdentity = collectIdentity;
        _detectDevice = detectDevice;
        _loadRuntime = loadRuntime;
        _restartDelay = restartDelay;
        Capabilities = new DeviceCapabilityRouter(postToUi);
        _chargeLimits = new DeviceChargeLimitController(() =>
            {
                var sample = autoTdpMetrics();
                return _client?.Device.Controls.Controls.Any(control =>
                    control.Role == LibHandheld.Contracts.CapabilityRole.ChargeBypass
                    && control.State?.Available == true) == true
                    ? sample
                    : sample with { BatteryPercent = null };
            },
            async (bypass, token) =>
            {
                var client = _client;
                if (client is not { IsActive: true } || !IntegrationEnabled)
                {
                    return false;
                }

                var result = await client.Device.SetChargeBypassAsync(bypass,
                    LibHandheld.Contracts.Deadline.After(TimeSpan.FromSeconds(5)), token).ConfigureAwait(false);
                if (!result.Applied)
                {
                    Log.Warn(
                        $"Charge ceiling bypass write stopped: {result.Reason?.Detail ?? result.Outcome.ToString()}.");
                }

                return result.Applied;
            });
        // The desired-state gate: desired-value passes, lighting profile selection and processor resets.
        var reconcileGate = new SemaphoreSlim(1, 1);
        FanCurves = new DeviceFanCurves(Capabilities, Profiles, autoTdpMetrics,
            () => Volatile.Read(ref _client), () => FanPresets, () => IntegrationEnabled, () => State,
            ActiveProfileScope, () => _disposed, _lifetime.Token, ExecuteInternalAsync);
        Lighting = new DeviceLightingProfiles(Capabilities, Profiles, () => DeviceIdentityKey, readOnAcPower,
            () => DeviceDefinition, ActiveProfileScope, () => Volatile.Read(ref _client), () => IntegrationEnabled,
            () => State, () => _disposed, _lifetime.Token, reconcileGate,
            UserCommandsInFlight, ExecuteInternalAsync, ReconcileDesiredValuesAsync,
            Observe, () => ConfigurationChanged?.Invoke());
        Power = new DevicePowerPolicy(Capabilities, Profiles, () => _config, () => DeviceDefinition,
            () => _disposed, _lifetime.Token, EnterTransitionAsync, PersistConfigurationAsync,
            AutoTdpTraceRecorder.DefaultDirectory(_store.Context), autoTdpTargetFrametimeMs, autoTdpMetrics,
            manualVariableRefresh, powerModes, readOnAcPower, registerPowerModeNotification,
            () => Volatile.Read(ref _resumeRestore), Observe, ExecuteInternalAsync, ExecuteInLaneAsync);
        Commands = new DeviceCapabilityCommands(Capabilities, Profiles, Power, FanCurves, Lighting, _chargeLimits,
            ReconcileChargeLimitAsync, reconcileGate, () => Volatile.Read(ref _client), () => _identity is not null,
            () => DeviceIdentityKey, manualVariableRefresh, () => _disposed, _lifetime.Token);
        Capabilities.Changed += Lighting.OnCapabilitiesChanged;
        Capabilities.Changed += Power.OnControlsChanged;
        _hapticSink = new DeviceHapticSink(ApplyHapticOutputAsync);
        Controllers = createControllers(_hapticSink);
        Controllers.Forwarding.FilterOemButtons = _oemActions.FilterControllerButtons;
        Controllers.Rumble.ApplyCalibration(config.RumbleCalibration);
        Controllers.TargetLost += OnControllerTargetLost;
        // Last: it starts serving at once, and nothing after it may fail and leave it running.
        _diagnostics = createDiagnostics(sessionId, DiagnosticsSnapshot);
    }

    /// <summary>The power lane, manual TDP, presets, assignments, per-application limits and AutoTDP.</summary>
    internal DevicePowerPolicy Power { get; }

    /// <summary>The one command route: every capability write from a surface, controller or restore.</summary>
    /// <remarks>
    ///     <see cref="Capabilities" /> serves reads and events; writes go through here, which is the one
    ///     path that lets a manual power change pause AutoTDP.
    /// </remarks>
    internal DeviceCapabilityCommands Commands { get; }

    /// <summary>The software fan curve and the active model's authored fan profiles.</summary>
    internal DeviceFanCurves FanCurves { get; }

    /// <summary>The active model's lighting profiles and the lighting restore.</summary>
    internal DeviceLightingProfiles Lighting { get; }

    /// <summary>The stable key device values are stored under, or null before the machine is identified.</summary>
    internal string? DeviceIdentityKey => _identity is null || DeviceDefinition is null
        ? null
        : $"{DeviceDefinition.FamilyId}:{DeviceMachineIdentity.StableKey(_identity)}";

    /// <summary>The profile owner every per-game value is read from and written to.</summary>
    internal ProfileService Profiles { get; }

    /// <summary>Current process-long lifecycle state, readable from any thread.</summary>
    internal DeviceCycleState State => _state;

    /// <summary>Whether the persisted master switch currently exposes the Device surface.</summary>
    internal bool IntegrationEnabled => _config.DeviceIntegration.Enabled;

    /// <summary>The exact supported model selected without opening native resources.</summary>
    internal HandheldDefinition? DeviceDefinition { get; private set; }

    /// <summary>Whether a native implementation matches this machine.</summary>
    internal bool HasDevice => DeviceDefinition is not null;

    /// <summary>The fan curve preset shapes of <see cref="DeviceDefinition" />, resolved once with it.</summary>
    /// <remarks>
    ///     Every surface and the software curve read this one value: the model's measured table when its
    ///     definition carries one, otherwise the default shapes, including before detection.
    /// </remarks>
    internal FanCurvePresetTable FanPresets { get; private set; } = FanCurvePresetTable.HandheldCompanion;

    /// <summary>The device definition matched by the active native cycle.</summary>
    private string? ActiveDeviceDefinitionId { get; set; }

    /// <summary>The capability router, for snapshots and change subscriptions.</summary>
    /// <remarks>
    ///     Reads and events only. Writes go through <see cref="Commands" />, which is the one path that
    ///     lets a manual power change pause AutoTDP.
    /// </remarks>
    internal DeviceCapabilityRouter Capabilities { get; }

    /// <summary>The capability snapshot every user surface presents; see <see cref="ManualFanVisibility" />.</summary>
    internal IReadOnlyList<DeviceCapabilityView> PresentedCapabilities()
    {
        return ManualFanVisibility.Filter(Capabilities.Snapshot());
    }

    /// <summary>The controller manager, for status/sample subscriptions and reads.</summary>
    /// <remarks>
    ///     Reads and events only. Lifecycle, UI capture, and the release ordering stay behind this
    ///     coordinator's methods so a consumer cannot order the manager's steps out of sequence.
    /// </remarks>
    internal ControllerManager Controllers { get; }

    /// <summary>Current persisted physical-glyph presentation mode.</summary>
    internal DeviceGlyphSelection PhysicalGlyphSelection =>
        _config.DeviceIntegration.GlyphSelection;

    /// <summary>Whether controller management may run in this configuration.</summary>
    internal bool ControllerManagementEnabled =>
        _config.DeviceIntegration is { ControllerManagementEnabled: true, Enabled: true }
        && DeviceDefinition is { HasController: true };

    /// <summary>The target a definition's physical pad already is, if any.</summary>
    /// <param name="definition">The matched definition.</param>
    /// <returns>The Steam Deck target for a pad Steam drives natively; otherwise null.</returns>
    internal static ManagedControllerTarget? NativeTarget(HandheldDefinition? definition)
    {
        return definition is { NativeSteamController: true } ? ManagedControllerTarget.SteamDeckComposite : null;
    }

    /// <summary>Whether the handheld should own its physical controller. Pure, so every case is testable.</summary>
    /// <param name="managementEnabled">Whether controller management is switched on.</param>
    /// <param name="definition">The matched definition.</param>
    /// <param name="resolvedTarget">The target resolved for the running application.</param>
    /// <returns>
    ///     False when management is off, the definition has no controller, or the target is the pad itself:
    ///     a Steam Deck emulating a Steam Deck only takes the pad away from Steam.
    /// </returns>
    internal static bool OwnsPhysicalController(
        bool managementEnabled,
        HandheldDefinition? definition,
        ManagedControllerTarget resolvedTarget)
    {
        return managementEnabled
               && definition is { HasController: true }
               && NativeTarget(definition) != resolvedTarget;
    }

    /// <summary>Current saved rumble calibration; callers edit it through the coordinator rather than mutating this reference.</summary>
    internal RumbleCalibrationConfig RumbleCalibration => _config.RumbleCalibration;

    /// <summary>Whether integration and the current controller state permit a rumble preview.</summary>
    internal bool CanPreviewRumble => IntegrationEnabled && Controllers.Rumble.CanPreview;

    /// <summary>The catalog holding the installed package's glyph profiles.</summary>
    /// <remarks>
    ///     Exposed so one <see cref="PhysicalGlyphPlans" /> can be built over it and share its invalidation.
    ///     The catalog is immutable data plus a change event; handing it out does not let a consumer
    ///     load, replace or reach past a profile.
    /// </remarks>
    internal PhysicalGlyphCatalog PhysicalGlyphCatalog { get; } = new();

    /// <summary>The shared shutdown task, or an already completed task when shutdown has not started.</summary>
    internal Task Completion
    {
        get
        {
            lock (_backgroundGate)
            {
                return _shutdownTask ?? Task.CompletedTask;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ShutdownAsync(
            HandheldStopReason.WsgmExiting,
            NormalShutdownDeadline());
    }

    /// <summary>Starts a calibrated physical rumble preview while holding device transition admission.</summary>
    /// <param name="testFloor">Whether to preview the configured minimum strength instead of full calibrated strength.</param>
    /// <param name="token">Cancels transition waiting or the bounded preview.</param>
    /// <returns>False when disposed or preview is unavailable; otherwise the controller preview's acceptance.</returns>
    internal async Task<bool> PreviewRumbleAsync(bool testFloor, CancellationToken token)
    {
        using var transition = await EnterTransitionAsync(token).ConfigureAwait(false);
        return !_disposed && CanPreviewRumble
                          && await Controllers.Rumble.PreviewAsync(testFloor, token).ConfigureAwait(false);
    }

    /// <summary>Requests an explicit stop for any physical rumble preview.</summary>
    /// <returns>Completion of the controller manager's stop attempt.</returns>
    internal Task StopRumblePreviewAsync()
    {
        return Controllers.Rumble.StopPreviewAsync();
    }

    /// <summary>Validates and persists one calibration value, then updates the controller manager.</summary>
    /// <param name="key"><c>strength</c>, <c>floor</c>, or <c>duration</c>.</param>
    /// <param name="value">Strength/floor percent from 0 to 100, or minimum pulse duration from 0 to 500 milliseconds.</param>
    /// <param name="token">Cancels transition admission or configuration persistence.</param>
    /// <returns>Completion after saving and applying calibration to the manager.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key or its value is unsupported.</exception>
    internal async Task SetRumbleCalibrationAsync(string key, int value, CancellationToken token)
    {
        using var transition = await EnterTransitionAsync(token).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var saved = await PersistConfigurationAsync(config =>
        {
            switch (key)
            {
                case "strength"
                    when value is >= 0 and <= 100: config.RumbleCalibration.StrengthPercent = value; break;
                case "floor"
                    when value is >= 0 and <= 100: config.RumbleCalibration.MinimumStrengthPercent = value; break;
                case "duration"
                    when value is >= 0 and <= 500: config.RumbleCalibration.MinimumPulseMilliseconds = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(value));
            }
        }, token).ConfigureAwait(false);
        Controllers.Rumble.ApplyCalibration(saved.RumbleCalibration);
    }

    private Task ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        var client = _client;
        return client is null
            ? Task.CompletedTask
            : client.ApplyHapticOutputAsync(frame, cancellationToken);
    }

    /// <summary>Raised after the authoritative lifecycle state changes.</summary>
    internal event Action<DeviceCycleState>? StateChanged;

    /// <summary>Raised when settings change overlay visibility or desired presentation.</summary>
    /// <remarks>
    ///     Glyph-profile selection also changes with configuration; consumers of the active profile
    ///     subscribe to this and to <see cref="PhysicalGlyphCatalog" />'s change event.
    /// </remarks>
    internal event Action? ConfigurationChanged;

    internal event Action<LibHandheld.Contracts.OemControlEvent>? PhysicalOemEventReceived;

    private Action<LibHandheld.Contracts.OemControlEvent>? _glyphOemHandler;

    /// <summary>Re-reads the power source after Windows reported a switch between AC and battery.</summary>
    /// <remarks>
    ///     Capability availability per source and the source's preset assignment both follow it. The
    ///     session raises this from the message window's AC/DC notification.
    /// </remarks>
    internal void OnPowerSourceChanged()
    {
        if (_disposed)
        {
            return;
        }

        Lighting.UpdateDesiredContext();
        Power.RequestAssignmentReconcile();
        Lighting.ReplayAfterTransition("power source changed");
    }

    /// <summary>
    ///     Creates the one coordinator allowed to own hardware on this machine without blocking the UI.
    /// </summary>
    /// <param name="config">Initial normalized application configuration.</param>
    /// <param name="store">The process-owned configuration persistence.</param>
    /// <param name="profiles">The profile owner every per-game value is read from and written to.</param>
    /// <param name="autoTdpTargetFrametimeMs">The frame deadline AutoTDP judges against; zero permits no control.</param>
    /// <param name="autoTdpMetrics">Samples the sensors AutoTDP consults.</param>
    /// <param name="manualVariableRefresh">Saves a variable-refresh state the user set to the profile in force.</param>
    /// <param name="powerModes">The session's Windows power-mode owner, which power presets switch.</param>
    /// <param name="cancellationToken">Cancels admission before the coordinator is created.</param>
    /// <returns>The coordinator, or null when the process-wide device owner is already reserved.</returns>
    internal static Task<DeviceCoordinator?> TryStartAsync(
        AppConfig config,
        ConfigStore store,
        ProfileService profiles,
        Func<double> autoTdpTargetFrametimeMs,
        Func<RtssOsdMetrics> autoTdpMetrics,
        Action<bool> manualVariableRefresh,
        WindowsPowerModes powerModes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(powerModes);
        ArgumentNullException.ThrowIfNull(autoTdpTargetFrametimeMs);
        ArgumentNullException.ThrowIfNull(autoTdpMetrics);
        ArgumentNullException.ThrowIfNull(manualVariableRefresh);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = TryCreateOwnerMutex(ProductionOwnerName);
        if (owner is null)
        {
            Log.Warn(
                "Device cycle: machine-wide ownership is already active or unavailable; no cycle started.");
            return Task.FromResult<DeviceCoordinator?>(null);
        }

        DeviceCoordinator coordinator;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = (uint)ProcessLookup.CurrentSessionId;
            coordinator = new DeviceCoordinator(
                config, store,
                sessionId,
                owner,
                action => Dispatcher.UIThread.Post(action), profiles,
                autoTdpTargetFrametimeMs,
                autoTdpMetrics,
                manualVariableRefresh,
                powerModes,
                sink => ControllerManager.CreateProduction(store.Context.Root, sink),
                DeviceMachineIdentity.Collect,
                DetectDeviceAsync,
                HandheldDeviceRuntime.CreateAsync,
                callback => EffectivePowerModeNotification.Register(callback),
                PowerSource.ReadOnAc,
                Task.Delay,
                (id, snapshot) => new DeviceCoordinatorDiagnosticsServer(id, snapshot));
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        coordinator.Observe(coordinator.InitializeAsync(coordinator._lifetime.Token), "initial start");

        return Task.FromResult<DeviceCoordinator?>(coordinator);
    }

    /// <summary>Starts the configured cycle, or recovers an interrupted controller while integration is off.</summary>
    /// <param name="cancellationToken">Cancels cycle admission or interrupted-controller recovery.</param>
    /// <returns>Completion of that attempt; disposed coordinators return without starting work.</returns>
    internal Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (IntegrationEnabled)
        {
            return StartCycleAsync(cancellationToken);
        }

        Log.Info($"Device cycle: coordinator ready for session {_sessionId}; integration disabled.");
        return Controllers.RecoverPhysicalControllerAsync(
            "integration disabled after an interrupted device cycle", cancellationToken);
    }

    /// <summary>
    ///     Creates one handle-owned machine marker. It is deliberately never mutex-owned, so
    ///     coordinator disposal may close it from any continuation thread.
    /// </summary>
    /// <param name="name">Nonblank machine-marker name.</param>
    /// <param name="create">Optional factory returning an unowned mutex handle and whether it created the marker.</param>
    /// <returns>The newly created handle to dispose without calling ReleaseMutex, or null when reserved or inaccessible.</returns>
    internal static Mutex? TryCreateOwnerMutex(
        string name,
        Func<string, (Mutex Owner, bool CreatedNew)>? create = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var factory = create ?? CreateOwnerMutex;
            var (owner, createdNew) = factory(name);
            if (createdNew)
            {
                return owner;
            }

            owner.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or WaitHandleCannotBeOpenedException)
        {
            Log.Warn($"Device cycle: owner marker '{name}' could not be created: {ex.Message}");
            return null;
        }
    }

    private static (Mutex Owner, bool CreatedNew) CreateOwnerMutex(string name)
    {
        var owner = new Mutex(false, name, out var createdNew);
        return (owner, createdNew);
    }

    /// <summary>Applies a saved ownership configuration to this authoritative process.</summary>
    /// <param name="config">Normalized configuration already saved by the configuration owner; retained as current state.</param>
    /// <param name="cancellationToken">Cancels transition admission and dependent lifecycle work.</param>
    /// <returns>
    ///     Completion of the requested ownership/profile reconciliation; it does not itself save the supplied
    ///     configuration.
    /// </returns>
    internal async Task ApplyConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_disposed)
        {
            return;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission.Token;
        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        var previousConfig = _config;
        var wasEnabled = _config.DeviceIntegration.Enabled;
        _config = config;
        Controllers.Rumble.ApplyCalibration(config.RumbleCalibration);
        ConfigurationChanged?.Invoke();

        // Stored settings live in the configuration, so a reload can change what the handheld
        // should be running with even though the handheld itself never changed.
        Lighting.UpdateDesiredContext();
        UpdateOemConfiguration();
        await Controllers.ApplySelectionAsync(
            CurrentControllerSelection(),
            _runningApplicationId,
            _runningExecutable,
            cancellationToken).ConfigureAwait(false);
        switch (wasEnabled)
        {
            case false when config.DeviceIntegration.Enabled:
                _automaticRestartAttempts = 0;
                await Lighting.RearmAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (!_disposed)
                    {
                        RestoreConfigAfterCanceledStart(previousConfig);
                    }

                    throw;
                }

                return;
            case true when !config.DeviceIntegration.Enabled:
            {
                // AutoTDP hands its original limit back while the power limit is still writable.
                await Power.RestoreAutoTdpBeforeStopAsync(NormalShutdownDeadline(), cancellationToken).ConfigureAwait(false);
                var teardown = await StopCycleUnderGateAsync(
                    HandheldStopReason.IntegrationDisabled,
                    NormalShutdownDeadline(),
                    cancellationToken).ConfigureAwait(false);
                PhysicalGlyphCatalog.ReplacePackageProfiles([]);
                ReportDeviceTeardown(teardown, cancellationToken);
                if (!teardown.Verified)
                {
                    // Report this explicit disable's failure after retiring the runtime. A later
                    // explicit enable may start a fresh cycle without replaying the failed stop.
                    throw teardown.ToException();
                }

                return;
            }
        }

        if (config.DeviceIntegration.Enabled && ControllerOwnershipMustChange())
        {
            await ApplyControllerSelectionUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RestoreConfigAfterCanceledStart(AppConfig previousConfig)
    {
        _config = previousConfig;
        try
        {
            ConfigurationChanged?.Invoke();
            Lighting.UpdateDesiredContext();
            UpdateOemConfiguration();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Device cycle cancellation config restore notification failed", ex);
        }
    }

    /// <summary>Quiesces the active handheld for suspend or session lock.</summary>
    /// <param name="cancellationToken">Cancels transition waiting and the bounded handheld suspension.</param>
    /// <returns>Suspension completion; the virtual target and physical hiding are retained while forwarding is blocked.</returns>
    internal async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        var client = _client;
        if (client is null)
        {
            Log.Info("Device suspend skipped: no active handheld cycle exists.");
            return;
        }

        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
        // A controller start still attaching (it retries for seconds after a wake) would otherwise
        // finish after this decision and bring a target up under a suspended handheld.
        await CancelControllerStartAsync().ConfigureAwait(false);
        if (Controllers.State is ControllerManagementState.Active)
        {
            // As HC does: the virtual controller and the hidden pad stay exactly as they are across
            // a sleep, and only forwarding stops. The handheld closes its own device below and opens it
            // again on wake, and the kept target then resumes forwarding.
            await Controllers.Forwarding.BlockForwardingAsync("system sleep", cancellationToken).ConfigureAwait(false);
            Log.Info("Controller forwarding paused for suspend; the virtual controller is kept.");
        }

        await FanCurves.Controller.SuspendAsync().ConfigureAwait(false);
        await _chargeLimits.StopAsync().ConfigureAwait(false);
        var state = await client.SuspendAsync(deadline, cancellationToken).ConfigureAwait(false);
        _oemActions.Reset();
        SetState(state.State);
    }

    /// <summary>Revalidates and resumes into a fresh device generation.</summary>
    /// <param name="afterSystemSleep">
    ///     Whether the machine slept, as opposed to a session unlock. A sleep may restart an already
    ///     faulted missing cycle; a failure during the resume itself restarts for either trigger, and an
    ///     unverified teardown is reported for an unlock as for any other restart.
    /// </param>
    /// <param name="cancellationToken">Cancels the resume.</param>
    /// <returns>Completion of the selected resume/restart attempt, or immediate completion when there is no eligible cycle.</returns>
    internal async Task ResumeAsync(bool afterSystemSleep, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        var client = _client;
        var action = DecideResume(
            client is not null,
            State,
            client?.LifecycleState,
            afterSystemSleep,
            _config.DeviceIntegration.Enabled && !_disposed);
        switch (action)
        {
            case DeviceResumeAction.Skip:
                Log.Info($"Device resume skipped: state={State}, cycle present={client is not null}.");
                return;
            case DeviceResumeAction.Restart:
                await RestartCycleUnderGateAsync(afterSystemSleep, cancellationToken).ConfigureAwait(false);
                return;
        }

        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
        Exception? failure = null;
        try
        {
            await client!.ResumeAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            failure = ex;
        }
        finally
        {
            Lighting.UpdateDesiredContext();
            _oemActions.Reset();
        }

        if (_disposed)
        {
            return;
        }

        if (failure is not null)
        {
            Log.Warn($"Device resume failed after {(afterSystemSleep ? "a sleep" : "a session unlock")} "
                     + $"({failure.Message}); starting a fresh cycle.");
            await RestartCycleUnderGateAsync(afterSystemSleep, cancellationToken).ConfigureAwait(false);
            return;
        }

        SetState(client!.LifecycleState);
        await FanCurves.Controller.ResumeAsync().ConfigureAwait(false);
        await ReconcileChargeLimitAsync().ConfigureAwait(false);
        Lighting.ReplayAfterTransition(afterSystemSleep ? "system wake" : "session unlock");
        await Controllers.Forwarding.ResumeForwardingAsync(afterSystemSleep ? "system wake" : "session unlock",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the cycle with a fresh one; see docs\device-integration.md, "Sleep".</summary>
    private async Task RestartCycleUnderGateAsync(bool allowUnverifiedTeardown, CancellationToken cancellationToken)
    {
        Log.Warn($"Device resume: starting a fresh cycle (state={State}, "
                 + $"handheld={_client?.LifecycleState.ToString() ?? "none"}).");
        var repair = await StopCycleUnderGateAsync(
            HandheldStopReason.RuntimeFault,
            NormalShutdownDeadline(),
            cancellationToken).ConfigureAwait(false);
        if (allowUnverifiedTeardown)
        {
            // Wake recovery attempts all cleanup, then replaces the cycle. A completed
            // runtime stop frees its Device slot even if hardware restoration was unverified.
            if (!repair.Verified)
            {
                Log.Warn("Device teardown during resume recovery was unverified; starting fresh anyway.");
            }
        }
        else
        {
            ReportDeviceTeardown(repair, cancellationToken);
        }

        _automaticRestartAttempts = 0;
        await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decides a resume. Pure, so every case is testable.</summary>
    /// <param name="hasCycle">Whether a handheld cycle exists.</param>
    /// <param name="state">The coordinator's cycle state.</param>
    /// <param name="lifecycleState">The handheld runtime's last published state.</param>
    /// <param name="afterSystemSleep">Whether the machine slept rather than the session unlocking.</param>
    /// <param name="integrationWanted">Whether device integration is on and WSGM is not shutting down.</param>
    /// <returns>The action.</returns>
    /// <remarks>
    ///     Only a cleanly suspended handheld is resumed; the runtime refuses every other state, and a
    ///     suspend or a resume cut off by the freeze leaves exactly those. A cycle that is gone is
    ///     started again only after a sleep and only when it faulted, which is how the runtime ends
    ///     when the pad re-enumerates on wake.
    /// </remarks>
    internal static DeviceResumeAction DecideResume(
        bool hasCycle,
        DeviceCycleState state,
        DeviceCycleState? lifecycleState,
        bool afterSystemSleep,
        bool integrationWanted)
    {
        if (!hasCycle)
        {
            return afterSystemSleep && integrationWanted && state is DeviceCycleState.Faulted
                ? DeviceResumeAction.Restart
                : DeviceResumeAction.Skip;
        }

        return lifecycleState is DeviceCycleState.Suspended
            ? DeviceResumeAction.Resume
            : DeviceResumeAction.Restart;
    }

    /// <summary>Starts one user-requested attempt after automatic recovery was exhausted.</summary>
    /// <param name="cancellationToken">Cancels transition admission and the explicit startup attempt.</param>
    /// <returns>
    ///     True when a fresh attempt ran, not proof that the device became active; false unless faulted and fully
    ///     retired.
    /// </returns>
    internal async Task<bool> RetryAfterFaultAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            return false;
        }

        if (!_runtimeRetirement.IsCompleted)
        {
            Log.Warn("Device retry refused while the previous runtime is still retiring.");
            return false;
        }

        if (State is not DeviceCycleState.Faulted)
        {
            Log.Info($"Handheld retry ignored because state is {State}.");
            return false;
        }

        _automaticRestartAttempts = 0;
        await Lighting.RearmAsync(cancellationToken).ConfigureAwait(false);
        await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Closes external work without closing AutoTDP's final power-limit restore route.</summary>
    internal void CloseAdmission()
    {
        lock (_backgroundGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _oemActions.Dispose();
        }
        finally
        {
            _lifetime.CancelAsync().ObserveFaults();
        }
    }

    /// <summary>Stops the device cycle under the process exit path's single outer deadline.</summary>
    /// <param name="reason">Stop reason used by the first shutdown request.</param>
    /// <param name="deadline">Outer cleanup/wait budget; repeated calls share the first shutdown operation.</param>
    /// <returns>Completion after shutdown finishes or this wait's deadline expires; unfinished owners remain retained.</returns>
    internal ValueTask ShutdownAsync(HandheldStopReason reason, Deadline deadline)
    {
        try
        {
            CloseAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Device admission closure failed; ordered safety cleanup continues: {ex.Message}");
        }

        Task shutdown;
        lock (_backgroundGate)
        {
            if (_shutdownTask is null)
            {
                // The remaining internal command route closes only after AutoTDP's ordered restore.
                _shutdownTask = Task.Run(() => ShutdownCoreAsync(reason, deadline));
                Log.Observe(_shutdownTask, "Device shutdown", true);
            }

            shutdown = _shutdownTask;
        }

        return new ValueTask(WaitForShutdownAsync(shutdown, deadline));
    }

    private static async Task WaitForShutdownAsync(Task shutdown, Deadline deadline)
    {
        using var bounded = deadline.CreateCancellationSource();
        try
        {
            await shutdown.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            Log.Warn("Device shutdown reached its deadline; unfinished owners remain retained.");
        }
    }

    private async Task ShutdownCoreAsync(HandheldStopReason reason, Deadline deadline)
    {
        using var bounded = deadline.CreateCancellationSource();
        await FanCurves.Controller.StopAsync().ConfigureAwait(false);
        await _chargeLimits.StopAsync().ConfigureAwait(false);
        // First, while the power limit is still writable: AutoTDP hands back the limit it took over
        // from. Exiting with its last automatic wattage latched leaves the handheld on a value the
        // user never chose.
        var autoTdpStopped = false;
        try
        {
            await Power.AutoTdp.StopAsync(deadline).ConfigureAwait(false);
            autoTdpStopped = Power.AutoTdp.Completion.IsCompletedSuccessfully;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"AutoTDP restoration was unverified during device shutdown: {ex.Message}");
        }

        try
        {
            Capabilities.CloseCommandAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Device capability admission closure failed; controller safety continues: {ex.Message}");
        }

        var cycleStopped = false;
        try
        {
            var stop = StopCycleAsync();
            Log.Observe(stop, "Device cycle shutdown", true);
            cycleStopped = await stop.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Device cycle shutdown was unverified: {ex.Message}");
        }

        // Always attempt controller safety, even after the transition consumed the deadline.
        var controllers = Task.Run(async () => await Controllers.DisposeAsync(deadline).ConfigureAwait(false));
        Log.Observe(controllers, "Controller shutdown safety", true);
        var controllersStopped = await FinishStepAsync("controller safety", controllers).ConfigureAwait(false);

        Task[] background;
        lock (_backgroundGate)
        {
            background =
                [.. _backgroundTasks, Power.AssignmentLoop, _oemActions.Completion];
        }

        var backgroundStopped =
            await FinishStepAsync("background work", Task.WhenAll(background)).ConfigureAwait(false);
        if (autoTdpStopped)
        {
            // AutoTDP no longer reads frames or writes trace rows; its owner releases both.
            await FinishStepAsync("AutoTDP trace", Power.DisposeAutoTdpTraceAsync().AsTask()).ConfigureAwait(false);
            CleanupProvider("AutoTDP frame times", Power.DisposeAutoTdpFrametimes);
        }

        if (!autoTdpStopped || !cycleStopped || !controllersStopped || !backgroundStopped
            || bounded.IsCancellationRequested)
        {
            Log.Warn("Device cleanup retained its remaining owners because work is still active or unverified.");
            return;
        }

        CleanupProvider("power notifications", Power.DisposePowerModeNotification);
        var diagnosticsStopped = await FinishStepAsync("diagnostics",
            _diagnostics?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
        await FinishStepAsync("software fan curves", FanCurves.Controller.DisposeAsync().AsTask()).ConfigureAwait(false);
        await FinishStepAsync("software charge limits", _chargeLimits.DisposeAsync().AsTask()).ConfigureAwait(false);
        var capabilitiesStopped = await FinishStepAsync("capabilities", Capabilities.DisposeAsync().AsTask())
            .ConfigureAwait(false);
        if (diagnosticsStopped && capabilitiesStopped && !bounded.IsCancellationRequested)
        {
            CleanupProvider("glyphs", PhysicalGlyphCatalog.Dispose);
            CleanupProvider("owner marker", _ownerMutex.Dispose);
        }
        // Lifetime and transition sources are retained: late holders may still release/read them.

        static void CleanupProvider(string name, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Device {name} cleanup was unverified: {ex.Message}");
            }
        }

        async Task<bool> StopCycleAsync()
        {
            await _transitionGate.WaitAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                var teardown = await StopCycleUnderGateAsync(reason, deadline, CancellationToken.None)
                    .ConfigureAwait(false);
                foreach (var failure in teardown.Failures)
                {
                    Log.Warn($"Device teardown step was unverified: {failure.Message}");
                }

                // A failed restore or subscriber is still reported above, but cannot keep the
                // remaining owners alive once handheld code and its deferred disposal have retired.
                return _runtimeRetirement.IsCompleted;
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        async Task<bool> FinishStepAsync(string name, Task work)
        {
            try
            {
                await work.WaitAsync(bounded.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Device {name} remains unverified: {ex.Message}");
                return false;
            }
        }
    }

    private async Task StartCycleAsync(CancellationToken cancellationToken)
    {
        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for the transition gate and holds it until the returned lease is disposed.</summary>
    /// <param name="cancellationToken">Cancels the wait; once the gate is held the lease owns its release.</param>
    /// <returns>The lease that releases the gate.</returns>
    private async ValueTask<DeviceTransitionLease> EnterTransitionAsync(CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceTransitionLease(_transitionGate);
    }

    private async Task StartCycleUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        if (!_runtimeRetirement.IsCompleted)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Warn("Device start refused while the previous runtime is still retiring.");
            return;
        }

        if (_client is not null || !_config.DeviceIntegration.Enabled)
        {
            return;
        }

        Power.EnsureLoops();
        var retryState = State;
        using var startLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await StartCycleCoreUnderGateAsync(startLifetime.Token).ConfigureAwait(false);
            startLifetime.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (startLifetime.IsCancellationRequested)
        {
            try
            {
                await CleanupCanceledStartAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Device cycle cancellation cleanup failed", ex);
            }

            try
            {
                SetState(retryState);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Device cycle cancellation state restore failed", ex);
            }

            throw;
        }
    }

    private async Task StartCycleCoreUnderGateAsync(CancellationToken cancellationToken)
    {
        if (!_config.DeviceIntegration.ControllerManagementEnabled)
        {
            await Controllers.RecoverPhysicalControllerAsync("controller management disabled", cancellationToken)
                .ConfigureAwait(false);
        }

        _intentionalStop = false;
        SetState(DeviceCycleState.Detected);
        HandheldDefinition definition;
        HandheldDeviceRuntime client;
        try
        {
            _identity = _collectIdentity();
            DeviceDefinition = await _detectDevice(_identity, cancellationToken)
                .ConfigureAwait(false);
            FanPresets = HandheldUiProjection.FanPresets(DeviceDefinition);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleStartFault(new InvalidOperationException(
                "The native handheld definition could not be selected.",
                ex));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var detectedDefinition = DeviceDefinition;
        PhysicalGlyphCatalog.ReplacePackageProfiles([]);
        if (detectedDefinition is null)
        {
            if (_config.DeviceIntegration.ControllerManagementEnabled)
            {
                await Controllers.RecoverPhysicalControllerAsync("unsupported handheld identity", cancellationToken)
                    .ConfigureAwait(false);
            }

            SetState(DeviceCycleState.Passive);
            Log.Warn(
                $"Handheld unsupported: code={CapabilityReasonCode.Unsupported}, manufacturer={_identity?.SystemManufacturer ?? "unknown"}, "
                + $"model={_identity?.SystemProduct ?? "unknown"}, board={_identity?.BaseboardProduct ?? "unknown"}, "
                + $"bios={_identity?.BiosVersion ?? "unknown"}, source={HandheldDeviceRuntime.SourceVersion}.");
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        definition = detectedDefinition;
        if (!definition.HasController)
        {
            await Controllers.RecoverPhysicalControllerAsync("power-only device definition", cancellationToken)
                .ConfigureAwait(false);
        }

        SetState(DeviceCycleState.Activating);
        try
        {
            client = await _loadRuntime(
                definition,
                _identity!,
                cancellationToken,
                Path.Combine(_store.Context.Root, "DeviceState")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleStartFault(ex);
            return;
        }

        _client = client;
        client.ApplySoftwareFanCurveAsync = (curve, _) => FanCurves.ConfigureAsync(curve, true);
        client.ApplySoftwareChargeLimitAsync = (percent, _) => ConfigureChargeLimitAsync(percent, true);
        try
        {
            Attach(client);
            Capabilities.Attach(client);
            Lighting.UpdateDesiredContext();
            _oemActions.Attach(client);
            UpdateOemConfiguration();
            var controllerManagement = OwnsPhysicalController(
                _config.DeviceIntegration.ControllerManagementEnabled,
                definition,
                ChosenControllerTarget());
            _handheldOwnsController = controllerManagement;
            // Before the handheld starts, because the handheld's first job is to find the physical
            // controller and it cannot find one that HidHide is hiding from this process. Doing it
            // afterwards is too late for the cycle that needed it.
            await Controllers.EnsureHidHideReadableAsync(controllerManagement, cancellationToken)
                .ConfigureAwait(false);
            var activation = await client.StartAsync(controllerManagement,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (activation.State is DeviceCycleState.Passive)
            {
                if (controllerManagement)
                {
                    await Controllers.RecoverPhysicalControllerAsync("device detection was passive", cancellationToken)
                        .ConfigureAwait(false);
                }

                Detach(client);
                var passiveDeadline = NormalShutdownDeadline();
                await client.StopAsync(passiveDeadline, cancellationToken)
                    .ConfigureAwait(false);
                await DisposeRuntimeAsync(client, passiveDeadline).ConfigureAwait(false);
                _client = null;
                SetDeviceDefinitionId(null);
                SetState(DeviceCycleState.Passive);
                _automaticRestartAttempts = 0;
                Log.Warn($"Handheld passive: family={definition.FamilyId}, model={definition.Id}, "
                         + $"code={activation.Reason?.Code.ToString() ?? "Unknown"}, detail={activation.Reason?.Detail ?? "No native service acquired"}, "
                         + $"manufacturer={_identity?.SystemManufacturer ?? "unknown"}, board={_identity?.BaseboardProduct ?? "unknown"}, "
                         + $"bios={_identity?.BiosVersion ?? "unknown"}, source={HandheldDeviceRuntime.SourceVersion}; runtime retired.");
                return;
            }

            // Before the profiles load: glyph selection is gated on the matched device definition,
            // and a catalog that arrives first would be selected against a null id and rejected.
            SetDeviceDefinitionId(activation.DeviceDefinitionId);

            // Attached after the definition is known, because stored values are keyed by it and by
            // the package: a value authored for one device must never be handed to another.
            LoadPhysicalGlyphProfiles(client);
            SetState(activation.State);
            _automaticRestartAttempts = 0;
            if (!controllerManagement)
            {
                // No pad is published to start the manager, so the selection alone says whether the
                // physical pad is the native target.
                await Controllers.ApplySelectionAsync(
                    CurrentControllerSelection(),
                    _runningApplicationId,
                    _runningExecutable,
                    cancellationToken).ConfigureAwait(false);
            }

            Log.Info(
                $"Device cycle active: package={definition.FamilyId}, "
                + $"state={activation.State}.");
            Observe(ObserveRuntimeCompletionAsync(client), "handheld supervision");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // The runtime owns a bounded start deadline. If it expires after the handheld entered
            // StartAsync, hardware may already be acquired even though the caller token is live;
            // run the same bounded cleanup path used by caller cancellation.
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                HandheldStopReason.StartCanceled,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                HandheldStopReason.StartFailed,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask CleanupCanceledStartAsync()
    {
        // A start fault can enqueue recovery while cancellation races its return. The recovery
        // worker is serialized behind this same transition, so clearing admission here guarantees
        // a canceled caller cannot be followed by an automatic restart.
        _faultRecoveryPending = false;

        // A start cancelled by shutdown keeps its possibly active runtime: the shutdown owner performs
        // the bounded handoff. An independent caller cancellation runs its own fresh bounded teardown.
        return _lifetime.IsCancellationRequested
            ? ValueTask.CompletedTask
            : new ValueTask(CleanupAbortedStartAsync(HandheldStopReason.StartCanceled));
    }

    private async Task CleanupAbortedStartAsync(HandheldStopReason reason)
    {
        var teardownVerified = false;
        try
        {
            // A budget of its own: the start caller's token may already be cancelled.
            var deadline = Deadline.After(CanceledStartCleanupBudget);
            using var cleanupCancellation = deadline.CreateCancellationSource();
            var teardown = await StopCycleUnderGateAsync(
                reason,
                deadline,
                cleanupCancellation.Token).ConfigureAwait(false);
            teardownVerified = teardown.Verified;
            ReportDeviceTeardown(teardown, cleanupCancellation.Token);
        }
        catch (Exception ex) when (!teardownVerified && ex is not OutOfMemoryException)
        {
            Log.Warn($"Aborted device start cleanup was unverified: {ex.Message}");
            throw;
        }
    }

    private async Task ScheduleStartFaultAfterCleanupAsync(
        Exception startFailure,
        HandheldStopReason reason,
        CancellationToken startCancellationToken)
    {
        var failure = startFailure;
        try
        {
            await CleanupAbortedStartAsync(reason).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
        {
            failure = new AggregateException(
                "Device startup failed and its bounded cleanup was unverified.",
                startFailure,
                cleanupFailure);
        }

        // Cancellation can race the original exception and the fresh cleanup. A canceled caller
        // still owns the outcome and must never be followed by an automatic restart.
        startCancellationToken.ThrowIfCancellationRequested();
        ScheduleStartFault(failure);
    }

    private async Task ObserveRuntimeCompletionAsync(HandheldDeviceRuntime client)
    {
        var exit = await client.Completion.ConfigureAwait(false);
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            _client = null;
            var cleanupDeadline = NormalShutdownDeadline();
            using var cleanupCancellation = cleanupDeadline.CreateCancellationSource();
            var cleanup = await TeardownClientAsync(
                client, HandheldStopReason.RuntimeFault, cleanupDeadline,
                cleanupCancellation.Token).ConfigureAwait(false);

            // An unverified step is logged, never a reason to stay down: HC's Close ignores its results,
            // and blocking the restart here left the Ally without its pad, fans and TDP until the next
            // sleep whenever a release write failed because the pad had already dropped.
            if (!cleanup.Verified)
            {
                Log.Warn($"Handheld fault cleanup had unverified steps; restarting anyway: "
                         + $"{cleanup.ToException().Message}");
            }

            if (_intentionalStop
                || _disposed
                || !_config.DeviceIntegration.Enabled
                || exit.Reason is DeviceRuntimeExitReason.Intentional)
            {
                SetState(DeviceCycleState.Disabled);
                return;
            }

            Log.Warn(
                $"Handheld fault: reason={exit.Reason}, "
                + $"detail={exit.Detail}.");
            ScheduleFaultRecovery();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error("Handheld restart failed; cycle faulted", ex);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void ScheduleStartFault(Exception exception)
    {
        if (_faultRecoveryPending || _disposed || !_config.DeviceIntegration.Enabled)
        {
            Log.Warn(
                "Handheld start fault recovery suppressed: "
                + $"pending={_faultRecoveryPending}, disposed={_disposed}, "
                + $"integrationEnabled={_config.DeviceIntegration.Enabled}, "
                + $"failure={exception.Message}");
            return;
        }

        _faultRecoveryPending = true;
        Log.Error("Handheld start failed", exception);
        Observe(HandleStartFaultAsync(), "handheld start recovery");
    }

    private async Task HandleStartFaultAsync()
    {
        using var transition = await EnterTransitionAsync(_lifetime.Token).ConfigureAwait(false);
        if (!_faultRecoveryPending)
        {
            Log.Info("Handheld start-fault worker stopped: recovery is no longer pending.");
            return;
        }

        _faultRecoveryPending = false;
        if (_disposed || !_config.DeviceIntegration.Enabled || _client is not null)
        {
            Log.Info(
                "Handheld start-fault worker stopped: "
                + $"disposed={_disposed}, integrationEnabled={_config.DeviceIntegration.Enabled}, "
                + $"runtimePresent={_client is not null}.");
            return;
        }

        ScheduleFaultRecovery();
    }

    private void ScheduleFaultRecovery()
    {
        if (_automaticRestartAttempts >= AutomaticRestartBackoffs.Length)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error(
                $"Device cycle faulted after restart exhaustion: package={DeviceDefinition?.FamilyId}, "
                + $"the {AutomaticRestartBackoffs.Length} automatic restart attempts were exhausted.");
            Observe(Controllers.ShowPhysicalControllerAsync("the device cycle could not be restarted",
                CancellationToken.None), "controller hide release");
            return;
        }

        var backoff = AutomaticRestartBackoffs[_automaticRestartAttempts++];
        SetState(DeviceCycleState.Activating);
        Log.Warn(
            $"Handheld restart {_automaticRestartAttempts}/{AutomaticRestartBackoffs.Length} scheduled in "
            + $"{backoff.TotalSeconds:0.#} s.");
        Observe(RestartAfterDelayAsync(backoff), "delayed handheld restart");
    }

    private async Task RestartAfterDelayAsync(TimeSpan backoff)
    {
        await _restartDelay(backoff, _lifetime.Token).ConfigureAwait(false);
        if (!_disposed && _config.DeviceIntegration.Enabled && _client is null)
        {
            await StartCycleAsync(_lifetime.Token).ConfigureAwait(false);
            return;
        }

        Log.Info(
            "Handheld delayed restart skipped: "
            + $"disposed={_disposed}, integrationEnabled={_config.DeviceIntegration.Enabled}, "
            + $"runtimePresent={_client is not null}.");
    }

    private async Task<DeviceClientTeardownResult> StopCycleUnderGateAsync(
        HandheldStopReason reason,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        await FanCurves.Controller.StopAsync().ConfigureAwait(false);
        await _chargeLimits.StopAsync().ConfigureAwait(false);
        var client = _client;
        _client = null;
        if (client is null)
        {
            SetState(DeviceCycleState.Disabled);
            return DeviceClientTeardownResult.Clean;
        }

        List<Exception> failures = [];
        try
        {
            Capabilities.CloseCommandAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device command admission closure failed; cleanup continues: {ex.Message}");
        }

        try
        {
            SetState(DeviceCycleState.Deactivating);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device deactivation state notification failed; cleanup continues: {ex.Message}");
        }

        try
        {
            var teardown = await TeardownClientAsync(client, reason, deadline, cancellationToken).ConfigureAwait(false);
            failures.AddRange(teardown.Failures);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device client teardown faulted before reporting its result: {ex.Message}");
        }
        finally
        {
            try
            {
                SetState(DeviceCycleState.Disabled);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device disabled-state notification failed after cleanup: {ex.Message}");
            }
        }

        return new DeviceClientTeardownResult([.. failures]);
    }

    private async ValueTask DisposeRuntimeAsync(HandheldDeviceRuntime client, Deadline deadline)
    {
        try
        {
            await client.DisposeAsync(deadline).ConfigureAwait(false);
        }
        finally
        {
            _runtimeRetirement = client.LateCleanup;
            Observe(_runtimeRetirement, "device runtime retirement");
        }
    }

    /// <summary>
    ///     Attempts controller and handheld cleanup before detaching and disposing the runtime.
    ///     Every non-fatal unverified response or exception is retained while later phases continue.
    /// </summary>
    private async Task<DeviceClientTeardownResult> TeardownClientAsync(
        HandheldDeviceRuntime client,
        HandheldStopReason reason,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        try
        {
            try
            {
                await ReleaseControllerAsync(client, HandoffScope.FullDeactivation, deadline,
                    cancellationToken, reason is HandheldStopReason.RuntimeFault).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device controller release unverified; cleanup continues: {ex.Message}");
            }

            try
            {
                var stopped = await client.StopAsync(deadline, cancellationToken)
                    .ConfigureAwait(false);
                if (stopped.State is DeviceCycleState.Disabled && stopped.Reason is null)
                {
                    Log.Info($"Device hardware release: {stopped.State}, verified.");
                }
                else
                {
                    var failure = new InvalidOperationException(
                        $"Device hardware release was unverified: state={stopped.State}, "
                        + $"reason={stopped.Reason?.Code.ToString() ?? "none"}, "
                        + $"detail={stopped.Reason?.Detail ?? "none"}.");
                    failures.Add(failure);
                    Log.Warn(failure.Message);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device hardware release unverified; host will be terminated: {ex.Message}");
            }
        }
        finally
        {
            try
            {
                Detach(client);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device client detach was incomplete: {ex.Message}");
            }
            finally
            {
                try
                {
                    await DisposeRuntimeAsync(client, deadline).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add(ex);
                    Log.Warn($"Device client disposal was incomplete: {ex.Message}");
                }
            }
        }

        return new DeviceClientTeardownResult([.. failures]);
    }

    /// <summary>Logs a teardown whose steps were not all verified and rethrows the caller's cancellation.</summary>
    /// <remarks>Unverified release is diagnostic evidence; it does not by itself forbid a later restart.</remarks>
    /// <param name="teardown">Retained failures from all attempted cleanup stages; ordinary failures are logged.</param>
    /// <param name="cancellationToken">Caller cancellation rethrown after reporting, without repeating cleanup.</param>
    internal static void ReportDeviceTeardown(
        DeviceClientTeardownResult teardown,
        CancellationToken cancellationToken)
    {
        if (!teardown.Verified)
        {
            Log.Warn($"Device teardown had unverified steps: {teardown.ToException().Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Deadline NormalShutdownDeadline()
    {
        return Deadline.After(TimeSpan.FromSeconds(15));
    }

    private async Task SetControllerManagementUnderGateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        // Recorded before the conversation: one that fails is not started again by the next selection.
        _handheldOwnsController = enabled;
        var deadline = Deadline.After(TimeSpan.FromSeconds(6));
        if (!enabled)
        {
            await ReleaseControllerAsync(client, HandoffScope.ControllerOnly, deadline, cancellationToken)
                .ConfigureAwait(false);
            Log.Info("Controller management disabled.");

            // After the release, and never instead of it: the handheld remembers its acquisition policy
            // across suspend/resume.
            try
            {
                await client.SetControllerManagementAsync(
                    false,
                    Deadline.After(TimeSpan.FromSeconds(6)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn(
                    "The handheld did not acknowledge controller management being disabled; "
                    + $"restarting the handheld with the persisted policy: {ex.Message}");
                var teardown = await StopCycleUnderGateAsync(
                    HandheldStopReason.RuntimeFault,
                    NormalShutdownDeadline(),
                    cancellationToken).ConfigureAwait(false);
                ReportDeviceTeardown(teardown, cancellationToken);
                await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Before the handheld is asked to acquire, exactly as at cycle start: it cannot discover an
        // interface another application's HidHide allowlist is hiding from WSGM, and adding
        // the allowance afterwards does nothing for the acquisition that already failed.
        await Controllers.EnsureHidHideReadableAsync(true, cancellationToken).ConfigureAwait(false);
        // Controller management changes within the same device cycle. Capability generations stay
        // current; OEM controls and events carry no cycle generation.
        await client.SetControllerManagementAsync(
            true,
            deadline,
            cancellationToken).ConfigureAwait(false);
        Log.Info("Controller management enabled.");
    }

    /// <summary>Has the handheld let go after its virtual target was lost, and shows the physical pad.</summary>
    /// <param name="detail">Why the target was lost.</param>
    /// <remarks>
    ///     The handheld conversation runs only for the cycle that lost the target. When it is skipped (a newer
    ///     cycle, shutdown) or the transition gate is not reached in time, a manager still Faulted has no
    ///     virtual pad driving anything, so the physical pad is shown anyway rather than left hidden.
    /// </remarks>
    private void OnControllerTargetLost(string detail)
    {
        var client = _client;
        Observe(Task.Run(async () =>
        {
            var released = false;
            try
            {
                using var budget = Deadline.After(TimeSpan.FromSeconds(20)).CreateCancellationSource(_lifetime.Token);
                await _transitionGate.WaitAsync(budget.Token).ConfigureAwait(false);
                try
                {
                    if (_disposed || client is null || !ReferenceEquals(_client, client)
                        || Controllers.State is not ControllerManagementState.Faulted)
                    {
                        return;
                    }

                    await CancelControllerStartAsync().WaitAsync(budget.Token).ConfigureAwait(false);
                    if (Controllers.State is not ControllerManagementState.Faulted)
                    {
                        return;
                    }

                    // The release shows the physical pad in its own cleanup, whatever the handheld does, and
                    // ends Faulted with this detail.
                    released = true;
                    await ReleaseControllerAsync(client, HandoffScope.ControllerOnly,
                            Deadline.After(TimeSpan.FromSeconds(6)), budget.Token, faultDetail: detail)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _transitionGate.Release();
                }
            }
            finally
            {
                if (!released && Controllers.State is ControllerManagementState.Faulted)
                {
                    using var cleanup = Deadline.After(TimeSpan.FromSeconds(6)).CreateCancellationSource();
                    await Controllers.ShowPhysicalControllerAsync("virtual target lost", cleanup.Token)
                        .ConfigureAwait(false);
                }
            }
        }), "Controller target-loss recovery");
    }

    private void Attach(HandheldDeviceRuntime client)
    {
        // The handler carries its client, so a notification from a client that is no longer current
        // (a stopping one, or one whose caller stopped waiting) is dropped instead of reaching SetState.
        _lifecycleHandler = state => OnLifecycleState(client, state);
        client.LifecycleStateReceived += _lifecycleHandler;
        client.PhysicalIdentitiesReceived += OnPhysicalIdentities;
        client.ControllerSampleReceived += Controllers.Forwarding.Submit;
        _glyphOemHandler = input =>
        {
            if (ReferenceEquals(_client, client))
            {
                PhysicalOemEventReceived?.Invoke(input);
            }
        };
        client.OemEventReceived += _glyphOemHandler;
    }

    private void Detach(HandheldDeviceRuntime client)
    {
        if (_lifecycleHandler is { } lifecycleHandler)
        {
            client.LifecycleStateReceived -= lifecycleHandler;
        }

        client.PhysicalIdentitiesReceived -= OnPhysicalIdentities;
        client.ControllerSampleReceived -= Controllers.Forwarding.Submit;
        if (_glyphOemHandler is { } oemHandler)
        {
            client.OemEventReceived -= oemHandler;
            _glyphOemHandler = null;
        }
        // The handheld no longer owns the controller: no frame goes to it from here on.
        _hapticSink.Withdraw();
        Capabilities.Detach();
        _oemActions.Detach();
    }

    /// <summary>
    ///     Starts WSGM-side controller management for the controller the handheld just took.
    /// </summary>
    /// <remarks>
    ///     Driven by the publication rather than by cycle start: WSGM may only hide a device and create
    ///     a virtual target once the handheld has actually acquired the physical one, and the handheld
    ///     republishes after a controller-management re-enable and after resume.
    /// </remarks>
    private void OnPhysicalIdentities(
        (IReadOnlyList<PhysicalDeviceIdentity> Devices, HapticCapabilities? Output) notification)
    {
        _hapticSink.Publish(notification.Output);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Interlocked.Exchange(ref _controllerStartCancellation, cancellation).Dispose();
        var publication = StartControllerManagementAsync(notification.Devices, cancellation.Token);
        Volatile.Write(ref _controllerPublication, publication);
        Observe(publication, "controller management start");
    }

    private async Task StartControllerManagementAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        CancellationToken cancellationToken)
    {
        ControllerManagerStatus status;
        try
        {
            status = await Controllers.StartAsync(
                CurrentControllerSelection(),
                devices,
                _runningApplicationId,
                _runningExecutable,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                                                 && !_lifetime.IsCancellationRequested)
        {
            Log.Info("Controller management start cancelled by a suspend.");
            return;
        }

        Log.Info(
            $"Controller management: state={status.State}, target={status.Target}, "
            + $"source={status.TargetSource}, detail={status.Detail}");
    }

    /// <summary>Cancels a controller start still in flight and waits for it to let go.</summary>
    private async Task CancelControllerStartAsync()
    {
        try
        {
            await Volatile.Read(ref _controllerStartCancellation).CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A newer publication already retired this startup token.
        }

        await Volatile.Read(ref _controllerPublication).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>The one controller release: any start still in flight first, then the ordered handoff.</summary>
    /// <param name="client">The runtime holding the physical controller.</param>
    /// <param name="scope">How much of the controller is handed back.</param>
    /// <param name="deadline">Bounds the whole release.</param>
    /// <param name="cancellationToken">Cancels waiting; the physical pad is still shown.</param>
    /// <param name="keepPhysicalHidden">Whether a fault restart takes the controller again at once.</param>
    /// <param name="faultDetail">When set, the release ends Faulted with this detail instead of Idle.</param>
    /// <remarks>
    ///     A start still attaching would otherwise take the manager's transition after the release and
    ///     raise a virtual target over a handheld that has let go, with the physical pad hidden. A start
    ///     that outlives the deadline is logged and the release runs anyway.
    /// </remarks>
    private async Task ReleaseControllerAsync(
        HandheldDeviceRuntime client,
        HandoffScope scope,
        Deadline deadline,
        CancellationToken cancellationToken,
        bool keepPhysicalHidden = false,
        string? faultDetail = null)
    {
        using (var bounded = deadline.CreateCancellationSource(cancellationToken))
        {
            try
            {
                await CancelControllerStartAsync().WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Controller release: a controller start was still running at the deadline.");
            }
        }

        await Controllers.ReleaseAsync(
            scope,
            token => client.ReleaseControllerAsync(deadline, token),
            deadline,
            cancellationToken,
            keepPhysicalHidden,
            faultDetail).ConfigureAwait(false);
    }

    /// <summary>Applies a running-application change from the one shared monitor.</summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>A task completing after the controller target is reconciled.</returns>
    internal async Task ApplyRunningApplicationAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed)
        {
            return;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _runningApplicationId = snapshot.ApplicationId;
        _runningExecutable = snapshot.RtssProfileName;
        if (ControllerOwnershipMustChange())
        {
            await ApplyControllerSelectionAsync(admission.Token).ConfigureAwait(false);
            return;
        }

        await Controllers.ApplyRunningApplicationAsync(snapshot, admission.Token)
            .ConfigureAwait(false);
    }

    /// <summary>Applies a profile snapshot: desired device values, the fan profile and the controller target.</summary>
    /// <param name="snapshot">The profiles and the application they resolve for.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <returns>A task completing once every affected capability was attempted.</returns>
    /// <remarks>
    ///     The one path a profile change reaches the device by, whether the running application
    ///     changed, the per-game switch flipped, a value was reset to Global or another process saved.
    ///     Without it, turning a game profile on or off changed nothing until the next application
    ///     switch.
    /// </remarks>
    internal async Task ApplyProfilesAsync(ProfileSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed)
        {
            return;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission.Token;
        Lighting.UpdateDesiredContext();
        Power.RequestAssignmentReconcile();
        UpdateOemConfiguration();
        if (!IntegrationEnabled)
        {
            return;
        }

        await Commands.ReconcileDesiredValuesAsync(
            $"profile generation {snapshot.Generation}",
            cancellationToken).ConfigureAwait(false);

        // Applied after the per-capability values so an explicitly selected fan profile wins for the
        // capability it covers, rather than the order deciding at random.
        await ApplyControllerSelectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the selection in force wants the handheld's controller ownership changed.</summary>
    private bool ControllerOwnershipMustChange()
    {
        return _client is not null
               && OwnsPhysicalController(ControllerManagementEnabled, DeviceDefinition, ChosenControllerTarget())
               != _handheldOwnsController;
    }

    /// <summary>Applies the controller selection, taking the transition gate only when ownership changes.</summary>
    private async Task<ControllerManagerStatus> ApplyControllerSelectionAsync(CancellationToken cancellationToken)
    {
        if (!ControllerOwnershipMustChange())
        {
            return await Controllers.ApplySelectionAsync(
                CurrentControllerSelection(),
                _runningApplicationId,
                _runningExecutable,
                cancellationToken).ConfigureAwait(false);
        }

        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await ApplyControllerSelectionUnderGateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the controller selection and moves physical ownership to match it.</summary>
    /// <remarks>
    ///     Ordered as every other handoff: going native, the handheld lets go and the virtual pad goes before
    ///     the selection reports Native; going emulated, the selection leaves Native first and the handheld
    ///     then takes the pad, whose publication brings the virtual target up.
    /// </remarks>
    private async Task<ControllerManagerStatus> ApplyControllerSelectionUnderGateAsync(
        CancellationToken cancellationToken)
    {
        var wanted = _client is not null
                     && OwnsPhysicalController(ControllerManagementEnabled, DeviceDefinition,
                         ChosenControllerTarget());
        if (_client is not null && !wanted && _handheldOwnsController)
        {
            Log.Info("Controller ownership: the handheld lets go of the physical controller.");
            await SetControllerManagementUnderGateAsync(false, cancellationToken).ConfigureAwait(false);
        }

        var status = await Controllers.ApplySelectionAsync(
            CurrentControllerSelection(),
            _runningApplicationId,
            _runningExecutable,
            cancellationToken).ConfigureAwait(false);
        if (_client is not null && wanted && !_handheldOwnsController)
        {
            Log.Info("Controller ownership: the handheld takes the physical controller.");
            await SetControllerManagementUnderGateAsync(true, cancellationToken).ConfigureAwait(false);
            status = Controllers.Snapshot();
        }

        return status;
    }

    /// <summary>The controller selection from the device switches and the profile store.</summary>
    private ControllerSelection CurrentControllerSelection()
    {
        return ControllerSelection.From(_config.DeviceIntegration, Profiles.Current.Config, DeviceDefinition);
    }

    /// <summary>The target chosen for the running application, whether or not one is live.</summary>
    /// <remarks>Selectors use saved intent so a fault or a not-yet-attached target does not erase the user's choice.</remarks>
    /// <returns>The resolved saved target, including when no virtual target is currently attached.</returns>
    internal ManagedControllerTarget ChosenControllerTarget()
    {
        return ControllerTargetSelection.Resolve(
            Profiles.Current.Config,
            _runningApplicationId,
            _runningExecutable).Target;
    }

    /// <summary>Resolves the current persisted mode against only the active package's safe profiles.</summary>
    /// <returns>The safe selection or fallback explaining why an active physical glyph profile is unavailable.</returns>
    internal PhysicalGlyphSelectionResult PhysicalGlyphSelectionSnapshot()
    {
        return PhysicalGlyphCatalog.SelectProfile(
            _config.DeviceIntegration.Enabled,
            _config.DeviceIntegration.GlyphSelection,
            _config.DeviceIntegration.ManualGlyphProfileId);
    }

    /// <summary>Resolves the active device's physical-control layout using automatic profile selection.</summary>
    /// <returns>The device-matched control profile or its fallback, independent of manual glyph presentation.</returns>
    internal PhysicalGlyphSelectionResult PhysicalControlSelectionSnapshot()
    {
        return PhysicalGlyphCatalog.SelectProfile(_config.DeviceIntegration.Enabled, DeviceGlyphSelection.Automatic,
            null);
    }

    /// <summary>Sets the physical presentation policy without changing device ownership.</summary>
    /// <param name="selection">Defined glyph presentation policy; invalid enum values throw.</param>
    /// <param name="cancellationToken">Cancels transition admission or persistence.</param>
    /// <returns>Completion after saving the presentation preference and notifying configuration consumers.</returns>
    internal async Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(selection))
        {
            throw new ArgumentOutOfRangeException(nameof(selection));
        }

        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await PersistConfigurationAsync(
            config => config.DeviceIntegration.GlyphSelection = selection,
            cancellationToken).ConfigureAwait(false);
        Log.Info($"Physical glyph presentation changed: {selection}.");
    }

    /// <summary>Claims managed controller input for one visible WSGM surface.</summary>
    /// <param name="surfaceId">Nonblank identifier retained until this surface releases its claim.</param>
    /// <param name="cancellationToken">Cancels admission or target neutralization; coordinator shutdown also cancels it.</param>
    /// <returns>Completion after any required neutralization of the virtual target.</returns>
    internal async Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await Controllers.Forwarding.ClaimUiAsync(surfaceId, admission.Token).ConfigureAwait(false);
    }

    /// <summary>Releases one visible WSGM surface's managed controller claim.</summary>
    /// <param name="surfaceId">Identifier used by the matching UI claim.</param>
    /// <remarks>Forwarding resumes only after every captured control is released in a subsequent sample.</remarks>
    internal void ReleaseUi(string surfaceId)
    {
        Controllers.Forwarding.ReleaseUi(surfaceId);
    }

    /// <summary>Sends a bounded rear-button pulse through the managed virtual target.</summary>
    /// <param name="button">One-based rear-button number, 1 or 2.</param>
    /// <param name="cancellationToken">Cancels the bounded press interval; shutdown also cancels it.</param>
    /// <returns>Whether an active target and source sample accepted the pulse; false when disposed or unsupported.</returns>
    internal async Task<bool> PulseRearButtonAsync(
        int button,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await Controllers.Forwarding.PulseRearButtonAsync(button, admission.Token).ConfigureAwait(false);
    }

    /// <summary>Changes the managed-controller target in the layer in force and persists the choice.</summary>
    /// <param name="target">The target.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The controller state after the change was applied.</returns>
    /// <remarks>
    ///     The stored setting is changed and then the manager is asked to re-resolve, in that order, so
    ///     the persisted value and the running target cannot disagree if the apply fails â€” the setting
    ///     is what the next reload and the Settings checkbox both read. Like every other setting it lands
    ///     in the running game's profile while that is on, and in Global otherwise.
    /// </remarks>
    internal async Task<ControllerManagerStatus> SetControllerTargetAsync(
        ManagedControllerTarget target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission.Token;
        using var transition = await EnterTransitionAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Profiles.SetAsync(values => values.ControllerTarget = target, $"ControllerTarget={target}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await ApplyControllerSelectionUnderGateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists one configuration change and announces it to configuration consumers.</summary>
    /// <remarks>Called with the transition gate held, so concurrent surfaces cannot interleave.</remarks>
    private async Task<AppConfig> PersistConfigurationAsync(
        Action<AppConfig> mutate,
        CancellationToken cancellationToken)
    {
        var persisted = await Task.Run(
            () => _store.Update(updatedConfig =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                mutate(updatedConfig);
                return true;
            }),
            cancellationToken).ConfigureAwait(false);
        _config = persisted;
        ConfigurationChanged?.Invoke();
        return persisted;
    }

    /// <summary>Attaches WSGM-owned UI and system actions after the shell surfaces exist.</summary>
    /// <param name="actions">Borrowed callbacks owned by the shell; these supply UI/system policy rather than device firmware.</param>
    internal void ConfigureOemActions(DeviceOemActionServices actions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _oemActions.ConfigureActions(actions);
    }

    /// <summary>The command route the coordinator's parts write through, with the five-second command budget.</summary>
    private Task<CapabilityCommandResult> ExecuteInternalAsync(string capabilityId, string? instanceId,
        CapabilityValue? value, CapabilityCommandOrigin origin, bool applyPowerPair,
        CancellationToken cancellationToken)
    {
        return Commands.ExecuteAsync(capabilityId, instanceId, value, TimeSpan.FromSeconds(5), origin,
            applyPowerPair, cancellationToken);
    }

    /// <summary>The in-lane command route the power policy writes through, with the five-second command budget.</summary>
    private Task<CapabilityCommandResult> ExecuteInLaneAsync(string capabilityId, string? instanceId,
        CapabilityValue? value, CapabilityCommandOrigin origin, bool applyPowerPair,
        CancellationToken cancellationToken)
    {
        return Commands.ExecuteCoreAsync(capabilityId, instanceId, value, TimeSpan.FromSeconds(5), origin,
            applyPowerPair, cancellationToken);
    }

    // The lighting owner is composed before the command route it reads these from.
    private bool UserCommandsInFlight()
    {
        return Commands.UserCommandsInFlight;
    }

    private Task ReconcileDesiredValuesAsync(string reason, CancellationToken cancellationToken, bool lightingOnly)
    {
        return Commands.ReconcileDesiredValuesAsync(reason, cancellationToken, lightingOnly);
    }

    private Task ConfigureChargeLimitAsync(int? percent, bool explicitSelection)
    {
        return _chargeLimits.ConfigureAsync(percent,
            _client is { UsesSoftwareChargeLimit: true, IsActive: true } && IntegrationEnabled, explicitSelection);
    }

    private Task ReconcileChargeLimitAsync()
    {
        return _client is { UsesSoftwareChargeLimit: true }
            ? ConfigureChargeLimitAsync(Capabilities
                .TryGetView(new DeviceCapabilityKey(CapabilityIds.ChargeLimit, null))
                ?.Projection.DesiredValue?.IntegerValue, false)
            : _chargeLimits.StopAsync();
    }

    /// <summary>The authored profiles of the exact matched model, or null before detection.</summary>
    private DeviceProfileScope? ActiveProfileScope()
    {
        return DeviceDefinition is { } definition
            ? _config.DeviceIntegration.DeviceProfiles.FirstOrDefault(scope => scope.DeviceDefinitionId == definition.Id
                                                                               && scope.FamilyId == definition.FamilyId)
            : null;
    }

    private void UpdateOemConfiguration()
    {
        _oemActions.UpdateConfiguration(
            _config.DeviceIntegration.OemAssignments,
            ControllerManagementEnabled,
            ChosenControllerTarget());
    }

    private void LoadPhysicalGlyphProfiles(HandheldDeviceRuntime runtime)
    {
        try
        {
            var imported = runtime.Device is { } device
                ? GlyphPackageImporter.Import(new EmbeddedGlyphSource(device))
                : new GlyphPackageImportResult([], []);
            PhysicalGlyphCatalog.ReplacePackageProfiles(imported.Profiles);
            Log.Info(
                $"Handheld glyph catalog: model={runtime.Device.Definition.Id}, profiles={imported.Profiles.Count}, errors={imported.Errors.Count}.");
            foreach (var error in imported.Errors)
            {
                Log.Warn(
                    $"Device glyph profile rejected: profile={error.ProfileId}, code={error.Code}, path={error.Path}, detail={error.Message}");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PhysicalGlyphCatalog.ReplacePackageProfiles([]);
            Log.Warn($"Device glyph catalog unavailable: {exception.Message}");
        }
    }

    private static Task<HandheldDefinition?> DetectDeviceAsync(DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HandheldDevice.Detect(identity));
    }

    private DeviceCoordinatorDiagnosticsSnapshot DiagnosticsSnapshot()
    {
        var capabilities = Capabilities.Snapshot();
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = State,
            Handheld = DeviceDefinition is { } definition
                ? new HandheldDiagnostic(definition.FamilyId,
                    HandheldDeviceRuntime.SourceVersion)
                : null,
            CapabilityCount = capabilities.Count,
            AvailableCapabilityCount = capabilities.Count(capability => capability.Projection.State.Available),
            FaultedCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Quality is HardwareStateQuality.Faulted),
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private void OnLifecycleState(HandheldDeviceRuntime client, HandheldRuntimeState state)
    {
        // Every stop and fault teardown clears the current client before it starts and sets the
        // deactivating and disabled states itself; a late notification from that client must not
        // start a restore pass. Dropped, not queued.
        if (!ReferenceEquals(client, Volatile.Read(ref _client)))
        {
            return;
        }

        SetDeviceDefinitionId(state.DeviceDefinitionId);
        if (state.Reason is { } reason)
        {
            Log.Change("handheld.lifecycle.reason",
                $"Handheld {state.State}: family={client.Device.Definition.FamilyId}, "
                + $"model={state.DeviceDefinitionId ?? "unknown"}, code={reason.Code}, detail={reason.Detail ?? "none"}, "
                + $"source={HandheldDeviceRuntime.SourceVersion}.", LogLevel.Warn);
        }

        SetState(state.State);
    }

    /// <summary>Records which device definition the handheld matched.</summary>
    /// <param name="deviceDefinitionId">The matched definition, or null when detection did not match.</param>
    /// <remarks>
    ///     Every glyph surface â€” the Steam Input page, the overlay's glyph rows, and the navigation
    ///     hints â€” resolves through <see cref="PhysicalGlyphSelectionSnapshot" />, which will only return
    ///     a profile that names the active device. The handheld publishes it with lifecycle state;
    ///     retaining a prior cycle's value after a non-match would select artwork and
    ///     authored profiles for hardware the active cycle did not identify.
    /// </remarks>
    private void SetDeviceDefinitionId(string? deviceDefinitionId)
    {
        var normalized = string.IsNullOrWhiteSpace(deviceDefinitionId)
            ? null
            : deviceDefinitionId;
        if (string.Equals(ActiveDeviceDefinitionId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        ActiveDeviceDefinitionId = normalized;
        Log.Info(normalized is null
            ? "Device definition cleared: the active cycle did not match hardware."
            : $"Device definition matched: {normalized}.");
        PhysicalGlyphCatalog.SetActiveDevice(normalized);
    }

    private void SetState(DeviceCycleState state)
    {
        if (State == state)
        {
            return;
        }

        var wasRunning = State is DeviceCycleState.Active or DeviceCycleState.Degraded;
        _state = state;
        Log.Info($"Handheld state: {state}.");
        StateChanged?.Invoke(state);
        if (!wasRunning && state is DeviceCycleState.Active or DeviceCycleState.Degraded && IntegrationEnabled)
        {
            // A new cycle, including one after sleep, can start with firmware defaults, so every
            // desired value is restored once, whichever profile layer it comes from. The power preset
            // waits for this pass instead of competing with it for the handheld's command lane, and is
            // reconciled once the pass has completed.
            var restore = Task.Run(async () =>
            {
                try
                {
                    await Commands.ReconcileDesiredValuesAsync($"device {state}", _lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    Power.RequestAssignmentReconcile();
                }
            });
            Volatile.Write(ref _resumeRestore, restore);
            Observe(restore, "cycle restore");
        }

        Power.RequestAssignmentReconcile();

        Lighting.OnCapabilitiesChanged(Capabilities.Snapshot());
    }

    private void Observe(Task task, string operation)
    {
        var observed = CompleteObservedAsync(task, operation);
        lock (_backgroundGate)
        {
            _backgroundTasks.Add(observed);
        }

        _ = RemoveObservedAsync(observed);
    }

    private static async Task CompleteObservedAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Device cycle {operation} failed", ex);
        }
    }

    private async Task RemoveObservedAsync(Task observed)
    {
        await observed.ConfigureAwait(false);
        lock (_backgroundGate)
        {
            _backgroundTasks.Remove(observed);
        }
    }

    private sealed class EmbeddedGlyphSource(HandheldDevice device) : IGlyphPackageSource
    {
        private readonly HashSet<string> _paths = new(device.GlyphResources, StringComparer.Ordinal);

        public IReadOnlyList<string> EnumerateProfileIds()
        {
            return _paths
                .Where(path =>
                    path.StartsWith("glyphs/profiles/", StringComparison.Ordinal) &&
                    path.EndsWith(".json", StringComparison.Ordinal))
                .Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
        }

        public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
        {
            bytes = [];
            if (!_paths.Contains(relativePath) || maximumBytes <= 0)
            {
                return false;
            }

            using var stream = device.OpenGlyphResource(relativePath);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                return false;
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return true;
        }
    }
}
