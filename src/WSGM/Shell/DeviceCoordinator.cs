using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LibHandheld;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Input;
using WSGM.Install;
using WSGM.Interop;
using WSGM.Shared;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace WSGM.Shell;

/// <summary>Who asked for a capability command.</summary>
/// <remarks>
///     The origin decides whether a command may persist the user's preference. A limit the user moved
///     is an instruction; the one
///     AutoTDP wrote itself is the controller's own output, and treating it as a manual override would
///     pause the feature on its first tick.
/// </remarks>
internal enum CapabilityCommandOrigin
{
    /// <summary>A person moved this control on a WSGM surface.</summary>
    User,

    /// <summary>An automatic controller inside WSGM wrote it.</summary>
    AutomaticControl,

    /// <summary>AutoTDP's power-lane writes, including its final original-limit restore.</summary>
    AutoTdp,

    /// <summary>
    ///     WSGM is re-applying a stored per-application or global preference on an application change.
    /// </summary>
    /// <remarks>
    ///     Not <see cref="User" />: the value is already the user's saved preference, so persisting it
    ///     again is redundant and — on a release-to-ceiling or a fall back to the global layer — would
    ///     write the wrong value into the layer the funnel resolves. The transition path pauses or
    ///     resumes AutoTDP itself, so this origin deliberately skips the funnel.
    /// </remarks>
    ProfileRestore,

    /// <summary>Replays device desired state without saving it; restored power still pauses AutoTDP.</summary>
    DesiredStateRestore
}

/// <summary>Authoritative process-long owner of the machine-wide hardware cycle.</summary>
internal sealed class DeviceCoordinator : IAsyncDisposable
{
    /// <summary>Machine-wide named marker preventing simultaneous production device owners.</summary>
    internal const string ProductionOwnerName = SessionProtocolNames.DeviceOwner;

    /// <summary>The delay before each automatic restart of a faulted plugin; its length is the restart budget.</summary>
    private static readonly TimeSpan[] AutomaticRestartBackoffs = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    private static readonly TimeSpan CanceledStartCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly RtssFrametimeReader _autoTdpFrametimes = new();
    private readonly AutoTdpTraceRecorder _autoTdpTrace;
    private readonly Lock _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly Func<DeviceIdentitySnapshot> _collectIdentity;
    private readonly Func<DeviceIdentitySnapshot, CancellationToken, Task<HandheldDefinition?>> _detectDevice;
    private readonly IAsyncDisposable? _diagnostics;
    private readonly PluginHapticSink _hapticSink;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeviceLightingRestore _lightingRestore = new();

    private readonly Func<HandheldDefinition, long, CancellationToken, string, Task<HandheldDeviceRuntime>>
        _loadRuntime;

    private readonly Action<bool> _manualVariableRefresh;
    private readonly DeviceOemActionRouter _oemActions = new();
    private readonly IDisposable _ownerMutex;
    private readonly PluginSettingsCoordinator _pluginSettings;

    /// <summary>
    ///     Wakes the power-assignment reconcile. One pending signal stands for any number of changes,
    ///     because the reconcile reads everything it needs afresh.
    /// </summary>
    private readonly Channel<bool> _powerAssignmentChanges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>
    ///     The one lane every sustained, boost, scenario and preset write runs in: user writes, presets,
    ///     per-application restores and AutoTDP.
    /// </summary>
    /// <remarks>
    ///     Nothing that holds this lane waits for the transition gate, so a transition may wait for the
    ///     lane: AutoTDP's restore before a device stop does exactly that.
    /// </remarks>
    private readonly SemaphoreSlim _powerLane = new(1, 1);

    private readonly SemaphoreSlim _profileReconcileGate = new(1, 1);
    private readonly Func<bool?> _readOnAcPower;
    private readonly Func<Action, IDisposable?> _registerPowerModeNotification;
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

    private long _cycleGeneration;
    private volatile bool _disposed;
    private bool _faultRecoveryPending;
    private DeviceIdentitySnapshot? _identity;
    private bool _intentionalStop;

    /// <summary>The lifecycle handler of the attached client, which carries that client.</summary>
    private Action<DevicePluginState>? _lifecycleHandler;

    private int _lightingRestoreScheduled;

    /// <summary>Started with the first device cycle, so integration off runs no power loop.</summary>
    /// <remarks>Assigned and read under <see cref="_backgroundGate" />.</remarks>
    private Task _powerAssignmentTask = Task.CompletedTask;

    /// <summary>The power controls' last reading, so only a change to them wakes the assignment reconcile.</summary>
    /// <remarks>Read and written on the UI thread, where the router raises its change event.</remarks>
    private (PowerControlReading Sustained, PowerControlReading Slow, PowerControlReading Scenario) _powerControls;

    private bool _powerLoopsStarted;

    private IDisposable? _powerModeNotification;

    /// <summary>Whether WSGM imposed the power limit in force for the running application.</summary>
    /// <remarks>Read and written only inside <see cref="_powerLane" />.</remarks>
    private bool _profilePowerImposed;

    /// <summary>Whether the imposed limit moved the sustained/boost pair together.</summary>
    /// <remarks>Read and written only inside <see cref="_powerLane" />.</remarks>
    private bool _profilePowerPaired;

    private Task _resumeRestore = Task.CompletedTask;
    private string? _runningApplicationId;
    private string? _runningExecutable;
    private Task _runtimeRetirement = Task.CompletedTask;
    private Task? _shutdownTask;

    private volatile DeviceCycleState _state = DeviceCycleState.Disabled;
    private int _userCapabilityCommands;

    /// <summary>Composes device ownership, controller routing, profiles, and one serialized power lane.</summary>
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
        Func<HandheldDefinition, long, CancellationToken, string, Task<HandheldDeviceRuntime>> loadRuntime,
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
        _registerPowerModeNotification = registerPowerModeNotification;
        _readOnAcPower = readOnAcPower;
        _restartDelay = restartDelay;
        _manualVariableRefresh = manualVariableRefresh;
        Capabilities = new DeviceCapabilityRouter(postToUi);
        Capabilities.Changed += OnLightingStateChanged;
        Capabilities.Changed += OnPowerControlsChanged;
        Capabilities.DescriptorsAccepted += OnPowerDescriptorsAccepted;
        // AutoTDP writes through the same power lane as every other power write, and this owner
        // restores its original limit before the device stops.
        _autoTdpTrace = new AutoTdpTraceRecorder(
            AutoTdpTraceRecorder.DefaultDirectory(_store.Context),
            () => DeviceDefinition is { } definition
                ? (definition.FamilyId, typeof(HandheldDevice).Assembly.GetName().Version?.ToString())
                : (null, null),
            new AutoTdpTraceSystemContext());
        AutoTdp = new AutoTdpService(
            _autoTdpFrametimes,
            Capabilities.Snapshot,
            (power, value, pair, token) => ExecuteCapabilityAsync(
                power.Descriptor.CapabilityId,
                power.Descriptor.InstanceId,
                value,
                TimeSpan.FromSeconds(5),
                CapabilityCommandOrigin.AutoTdp,
                power.Projection.State.CycleGeneration,
                power.Projection.State.DescriptorGeneration, pair, token),
            autoTdpTargetFrametimeMs,
            _autoTdpTrace,
            autoTdpMetrics);
        // Scenario targets are one-shot preset steps. Persist only the watt controls through the
        // manual funnel; saving an AC scenario as desired state would replay it on battery later.
        PowerPresets = new DevicePowerPresets(() => IntegrationEnabled ? Capabilities.Snapshot() : [],
            ExecutePresetCapabilityAsync, powerModes, _readOnAcPower, _powerLane,
            () => AutoTdp.OwnsPower);
        PowerAssignments = new DevicePowerAssignments(PowerPresets,
            () => new DevicePowerAssignmentContext(Profiles.Current,
                DeviceDefinition?.FamilyId,
                _cycleGeneration, IntegrationEnabled, _readOnAcPower()),
            SavePowerAssignmentAsync,
            () => Volatile.Read(ref _resumeRestore));
        _pluginSettings = new PluginSettingsCoordinator(_store);
        _hapticSink = new PluginHapticSink(ApplyHapticOutputAsync);
        Controllers = createControllers(_hapticSink);
        Controllers.ApplyRumbleCalibration(config.RumbleCalibration);
        Controllers.TargetLost += OnControllerTargetLost;
        // Last: it starts serving at once, and nothing after it may fail and leave it running.
        _diagnostics = createDiagnostics(sessionId, DiagnosticsSnapshot);
    }

    /// <summary>The one AutoTDP service; it writes only through this owner's power lane.</summary>
    internal AutoTdpService AutoTdp { get; }

    /// <summary>The stable key device values are stored under, or null before the machine is identified.</summary>
    internal string? DeviceIdentityKey => _identity is null ? null : DeviceMachineIdentity.StableKey(_identity);

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

    /// <summary>The device definition matched by the active native cycle.</summary>
    private string? ActiveDeviceDefinitionId { get; set; }

    /// <summary>The capability router, for snapshots and change subscriptions.</summary>
    /// <remarks>
    ///     Reads and events only. Writes go through <see cref="ExecuteCapabilityAsync" />, which is the
    ///     one path that lets a manual power change pause AutoTDP.
    /// </remarks>
    internal DeviceCapabilityRouter Capabilities { get; }

    /// <summary>One-shot preset owner shared by device surfaces and the serialized power lane.</summary>
    internal DevicePowerPresets PowerPresets { get; }

    /// <summary>Owner of saved AC/battery preset assignments and their change-triggered reconciliation.</summary>
    internal DevicePowerAssignments PowerAssignments { get; }

    /// <summary>Whether the declared power pair permits manual control and whether the active profile couples its limits.</summary>
    internal (bool Available, bool Unified) ManualTdpMode =>
        (IntegrationEnabled && Capabilities.HasDescriptor(static descriptor =>
                descriptor is { Role: CapabilityRole.PowerSustainedLimit, PairedPowerLimitId: not null }),
            ManualTdpUnified);

    /// <summary>
    ///     Whether manual TDP is unified for the running application. Unlike
    ///     <see cref="ManualTdpMode" />, this builds no capability snapshot.
    /// </summary>
    internal bool ManualTdpUnified
    {
        get { return Profiles.Current.Layers.Value(values => values.TdpUnified).Value == true; }
    }

    /// <summary>The game profile overriding the manual power mode, or null when it comes from Global.</summary>
    internal string? ManualTdpOverrideId =>
        CapabilityProjection.OverrideId(Profiles.Current.Layers, new ProfileSettingKey(ProfileField.TdpUnified));

    /// <summary>The controller manager, for status/sample subscriptions and reads.</summary>
    /// <remarks>
    ///     Reads and events only. Lifecycle, UI capture, and the release ordering stay behind this
    ///     coordinator's methods so a consumer cannot order the manager's steps out of sequence.
    /// </remarks>
    internal ControllerManager Controllers { get; }

    /// <summary>Current persisted physical-glyph presentation mode.</summary>
    internal DeviceGlyphSelection PhysicalGlyphSelection =>
        _config.DeviceIntegration.GlyphSelection;

    /// <summary>Whether AutoTDP is switched on in the persisted configuration.</summary>
    internal bool AutoTdpEnabled => _config.DeviceIntegration.AutoTdpEnabled;

    /// <summary>Whether controller management may run in this configuration.</summary>
    internal bool ControllerManagementEnabled =>
        _config.DeviceIntegration is { ControllerManagementEnabled: true, Enabled: true }
        && DeviceDefinition is { HasController: true };

    /// <summary>Current saved rumble calibration; callers edit it through the coordinator rather than mutating this reference.</summary>
    internal RumbleCalibrationConfig RumbleCalibration => _config.RumbleCalibration;

    /// <summary>Whether integration and the current controller state permit a rumble preview.</summary>
    internal bool CanPreviewRumble => IntegrationEnabled && Controllers.CanPreviewRumble;

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
            PluginStopReason.WsgmExiting,
            NormalShutdownDeadline());
    }

    /// <summary>Starts a calibrated physical rumble preview while holding device transition admission.</summary>
    /// <param name="testFloor">Whether to preview the configured minimum strength instead of full calibrated strength.</param>
    /// <param name="token">Cancels transition waiting or the bounded preview.</param>
    /// <returns>False when disposed or preview is unavailable; otherwise the controller preview's acceptance.</returns>
    internal async Task<bool> PreviewRumbleAsync(bool testFloor, CancellationToken token)
    {
        await _transitionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return !_disposed && CanPreviewRumble
                              && await Controllers.PreviewRumbleAsync(testFloor, token).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Requests an explicit stop for any physical rumble preview.</summary>
    /// <returns>Completion of the controller manager's stop attempt.</returns>
    internal Task StopRumblePreviewAsync()
    {
        return Controllers.StopRumblePreviewAsync();
    }

    /// <summary>Validates and persists one calibration value, then updates the controller manager.</summary>
    /// <param name="key"><c>strength</c>, <c>floor</c>, or <c>duration</c>.</param>
    /// <param name="value">Strength/floor percent from 0 to 100, or minimum pulse duration from 0 to 500 milliseconds.</param>
    /// <param name="token">Cancels transition admission or configuration persistence.</param>
    /// <returns>Completion after saving and applying calibration to the manager.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key or its value is unsupported.</exception>
    internal async Task SetRumbleCalibrationAsync(string key, int value, CancellationToken token)
    {
        await _transitionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
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
            Controllers.ApplyRumbleCalibration(saved.RumbleCalibration);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Returns the manual power mode of the running application to the Global value.</summary>
    /// <returns>Completion of clearing the game's manual-mode override through the profile owner.</returns>
    internal Task UseGlobalManualTdpAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Profiles.ClearGameOverrideAsync(new ProfileSettingKey(ProfileField.TdpUnified), null, _lifetime.Token);
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

    /// <summary>Saves the active profile's manual sustained/boost coupling mode.</summary>
    /// <param name="unified">True to couple the limits; false to retain independent sustained and boost values.</param>
    /// <returns>Completion of the saved profile edit; shutdown cancels admission.</returns>
    /// <exception cref="InvalidOperationException">The current device does not expose an available power pair.</exception>
    internal async Task SetManualTdpModeAsync(bool unified)
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ManualTdpMode.Available)
            {
                throw new InvalidOperationException("Paired TDP is unavailable.");
            }

            await Profiles.SetAsync(values => values.TdpUnified = unified, $"TdpUnified={unified}",
                cancellationToken: _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>The power source, or null when Windows cannot say.</summary>
    /// <returns>True on AC, false on battery, or null when the Windows query fails or reports unknown status.</returns>
    internal static bool? ReadOnAcPower()
    {
        return WindowsPower.TryGetStatus(out var power) && power.ACLineStatus is 0 or 1
            ? power.ACLineStatus == 1
            : null;
    }

    private async Task<CapabilityCommandResult> ExecutePresetCapabilityAsync(
        string id, CapabilityValue value, long cycle, long generation, bool persist, CancellationToken token)
    {
        if (_disposed)
        {
            return ClosedCapabilityAdmission();
        }

        var result = await ExecuteCapabilityCoreAsync(id, null, value, TimeSpan.FromSeconds(5),
            persist && value.Kind == CapabilityValueKind.Integer
                ? CapabilityCommandOrigin.User
                : CapabilityCommandOrigin.AutomaticControl,
            cycle, generation, false, token).ConfigureAwait(false);
        if (!persist && value.IntegerValue is { } watts && result.Outcome.IsApplied()
            && FindDescriptor(id, null)?.Role == CapabilityRole.PowerSustainedLimit)
        {
            // An applied assignment pauses AutoTDP without saving its wattage as the user's limit.
            AutoTdp.NoteManualChange(watts);
        }

        return result;
    }

    private async Task SavePowerAssignmentAsync(DevicePowerAssignmentContext selection, bool ac,
        DevicePowerPresetReference? reference)
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Profiles.Current.Generation != selection.Profiles.Generation
                || DeviceDefinition?.FamilyId != selection.PluginId
                || IntegrationEnabled != selection.Enabled
                || Interlocked.Read(ref _cycleGeneration) != selection.Cycle
                || selection.OnAc != _readOnAcPower())
            {
                throw new InvalidOperationException(
                    "The running application, device or configuration changed before saving the assignment.");
            }

            await Profiles.SetAsync(values =>
                {
                    if (ac)
                    {
                        values.AcPowerPreset = reference;
                    }
                    else
                    {
                        values.BatteryPowerPreset = reference;
                    }
                }, $"{(ac ? "AC" : "battery")} power preset {reference?.PresetId ?? "(none)"}",
                cancellationToken: _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

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

        UpdateCapabilityDesiredContext();
        RequestPowerAssignmentReconcile();
    }

    private void RequestPowerAssignmentReconcile()
    {
        _powerAssignmentChanges.Writer.TryWrite(true);
    }

    /// <summary>Wakes the assignment reconcile when a power control's reading or availability moves.</summary>
    /// <remarks>
    ///     This is how presets that become available are applied and how a watt limit changed outside a
    ///     preset is adopted as Custom. The router raises this for every state publication, telemetry
    ///     included, so it compares three readings and allocates nothing.
    /// </remarks>
    private void OnPowerControlsChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        PowerControlReading sustained = default, slow = default, scenario = default;
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            switch (view.Descriptor.Role)
            {
                case CapabilityRole.PowerSustainedLimit:
                    sustained = PowerControlReading.Of(view);
                    break;
                case CapabilityRole.PowerSlowLimit:
                    slow = PowerControlReading.Of(view);
                    break;
                case CapabilityRole.ScenarioMode:
                    scenario = PowerControlReading.Of(view);
                    break;
            }
        }

        var reading = (sustained, slow, scenario);
        if (reading == _powerControls)
        {
            return;
        }

        _powerControls = reading;
        RequestPowerAssignmentReconcile();
    }

    private void OnPowerDescriptorsAccepted(long cycle, long generation)
    {
        if (_disposed || _identity is null || cycle != _cycleGeneration)
        {
            return;
        }

        var config = Profiles.Current.Config;
        if (config.Global.BoostWatts is null && config.Games.All(game => game.Values.BoostWatts is null))
        {
            return;
        }

        var boost = Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor is { Role: CapabilityRole.PowerSlowLimit, SupportsWrite: true });
        if (boost is not null)
        {
            Observe(Profiles.MigrateLegacyBoostAsync(DeviceIdentityKey!, boost.Descriptor.CapabilityId,
                boost.Descriptor.InstanceId, _lifetime.Token), "legacy PL2 migration");
        }
    }

    /// <summary>
    ///     Applies the power-source assignment whenever something it depends on changed: the power source,
    ///     the Windows power mode, the profile or running application, the device cycle and its restore, or
    ///     the power controls themselves.
    /// </summary>
    private async Task ObservePowerAssignmentsAsync()
    {
        var changes = _powerAssignmentChanges.Reader;
        try
        {
            while (await changes.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                changes.TryRead(out _);
                try
                {
                    await PowerAssignments.ReconcileAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Change("power.assignment.failure", $"Power assignment failed: {ex.Message}", LogLevel.Warn);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
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
            var sessionId = (uint)WindowFinder.CurrentSessionId;
            coordinator = new DeviceCoordinator(
                config, store,
                sessionId,
                owner,
                UiThread.Post, profiles,
                autoTdpTargetFrametimeMs,
                autoTdpMetrics,
                manualVariableRefresh,
                powerModes,
                sink => ControllerManager.CreateProduction(store.Context.Root, sink),
                DeviceMachineIdentity.Collect,
                DetectDeviceAsync,
                HandheldDeviceRuntime.StartAsync,
                callback => EffectivePowerModeNotification.Register(callback),
                ReadOnAcPower,
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
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var previousConfig = _config;
            var wasEnabled = _config.DeviceIntegration.Enabled;
            var controllerWasEnabled = ControllerManagementEnabled;
            _config = config;
            Controllers.ApplyRumbleCalibration(config.RumbleCalibration);
            var controllerIsEnabled = ControllerManagementEnabled;
            ConfigurationChanged?.Invoke();

            // Stored settings live in the configuration, so a reload can change what the plugin
            // should be running with even though the plugin itself never changed.
            _pluginSettings.ApplyConfig(config);
            UpdateCapabilityDesiredContext();
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
                    await RestoreAutoTdpBeforeStopAsync(cancellationToken).ConfigureAwait(false);
                    var teardown = await StopCycleUnderGateAsync(
                        PluginStopReason.IntegrationDisabled,
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

            if (config.DeviceIntegration.Enabled
                && controllerWasEnabled != controllerIsEnabled
                && _client is not null)
            {
                await SetControllerManagementUnderGateAsync(
                    controllerIsEnabled,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void RestoreConfigAfterCanceledStart(AppConfig previousConfig)
    {
        _config = previousConfig;
        try
        {
            ConfigurationChanged?.Invoke();
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Device cycle cancellation config restore notification failed", ex);
        }
    }

    /// <summary>Quiesces the active plugin for suspend or session lock.</summary>
    /// <param name="cancellationToken">Cancels transition waiting and the bounded plugin suspension.</param>
    /// <returns>Suspension completion; the virtual target and physical hiding are retained while forwarding is blocked.</returns>
    internal async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var client = _client;
            if (client is null)
            {
                Log.Info("Device suspend skipped: no active plugin cycle exists.");
                return;
            }

            var deadline = Deadline.After(TimeSpan.FromSeconds(5));
            // A controller start still attaching (it retries for seconds after a wake) would otherwise
            // finish after this decision and bring a target up under a suspended plugin.
            await CancelControllerStartAsync().ConfigureAwait(false);
            if (Controllers.State is ControllerManagementState.Active)
            {
                // As HC does: the virtual controller and the hidden pad stay exactly as they are across
                // a sleep, and only forwarding stops. The plugin closes its own device below and opens it
                // again on wake, and the kept target then resumes forwarding.
                await Controllers.BlockForwardingAsync("system sleep", cancellationToken).ConfigureAwait(false);
                Log.Info("Controller forwarding paused for suspend; the virtual controller is kept.");
            }

            var state = await client.SuspendAsync(deadline, cancellationToken).ConfigureAwait(false);
            _oemActions.Reset();
            SetState(state.State);
        }
        finally
        {
            _transitionGate.Release();
        }
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

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                case ResumeAction.Skip:
                    Log.Info($"Device resume skipped: state={State}, cycle present={client is not null}.");
                    return;
                case ResumeAction.Restart:
                    await RestartCycleUnderGateAsync(afterSystemSleep, cancellationToken).ConfigureAwait(false);
                    return;
            }

            _identity = _collectIdentity();
            var deadline = Deadline.After(TimeSpan.FromSeconds(5));
            var previousGeneration = Interlocked.Read(ref _cycleGeneration);
            var requestedGeneration = Interlocked.Increment(ref _cycleGeneration);
            Exception? failure = null;
            try
            {
                await client!.ResumeAsync(requestedGeneration, deadline, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }
            finally
            {
                SynchronizeGenerationAfterLifecycleCall(client!, previousGeneration);
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
            await Controllers.ResumeForwardingAsync(afterSystemSleep ? "system wake" : "session unlock",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Replaces the cycle with a fresh one; see docs\device-integration.md, "Sleep".</summary>
    private async Task RestartCycleUnderGateAsync(bool allowUnverifiedTeardown, CancellationToken cancellationToken)
    {
        Log.Warn($"Device resume: starting a fresh cycle (state={State}, "
                 + $"plugin={_client?.LifecycleState.ToString() ?? "none"}).");
        var repair = await StopCycleUnderGateAsync(
            PluginStopReason.RuntimeFault,
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
    /// <param name="hasCycle">Whether a plugin cycle exists.</param>
    /// <param name="state">The coordinator's cycle state.</param>
    /// <param name="lifecycleState">The plugin runtime's last published state.</param>
    /// <param name="afterSystemSleep">Whether the machine slept rather than the session unlocking.</param>
    /// <param name="integrationWanted">Whether device integration is on and WSGM is not shutting down.</param>
    /// <returns>The action.</returns>
    /// <remarks>
    ///     Only a cleanly suspended plugin is resumed; the runtime refuses every other state, and a
    ///     suspend or a resume cut off by the freeze leaves exactly those. A cycle that is gone is
    ///     started again only after a sleep and only when it faulted, which is how the runtime ends
    ///     when the pad re-enumerates on wake.
    /// </remarks>
    internal static ResumeAction DecideResume(
        bool hasCycle,
        DeviceCycleState state,
        DeviceCycleState? lifecycleState,
        bool afterSystemSleep,
        bool integrationWanted)
    {
        if (!hasCycle)
        {
            return afterSystemSleep && integrationWanted && state is DeviceCycleState.Faulted
                ? ResumeAction.Restart
                : ResumeAction.Skip;
        }

        return lifecycleState is DeviceCycleState.Suspended
            ? ResumeAction.Resume
            : ResumeAction.Restart;
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

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                Log.Info($"Device plugin retry ignored because state is {State}.");
                return false;
            }

            _automaticRestartAttempts = 0;
            await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitionGate.Release();
        }
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
            try
            {
                _pluginSettings.Dispose();
            }
            finally
            {
                _lifetime.CancelAsync().ObserveFaults();
            }
        }
    }

    /// <summary>Stops the device cycle under the process exit path's single outer deadline.</summary>
    /// <param name="reason">Stop reason used by the first shutdown request.</param>
    /// <param name="deadline">Outer cleanup/wait budget; repeated calls share the first shutdown operation.</param>
    /// <returns>Completion after shutdown finishes or this wait's deadline expires; unfinished owners remain retained.</returns>
    internal ValueTask ShutdownAsync(PluginStopReason reason, Deadline deadline)
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

    /// <summary>Disables AutoTDP and waits for its restore before the device cycle stops.</summary>
    /// <param name="cancellationToken">The caller's token; only its cancellation propagates.</param>
    /// <remarks>
    ///     Bounded like a device stop. A restore still running at the bound keeps running and is never
    ///     issued a second time; the stop goes ahead and the log says the restore is unconfirmed.
    /// </remarks>
    private async Task RestoreAutoTdpBeforeStopAsync(CancellationToken cancellationToken)
    {
        using var bounded = NormalShutdownDeadline().CreateCancellationSource(cancellationToken);
        try
        {
            await AutoTdp.DisableAsync().WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("AutoTDP restoration is still running; the device stops without waiting for it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warn($"AutoTDP restoration was unverified before the device stop: {ex.Message}");
        }
    }

    /// <summary>Starts the power-assignment loop and the Windows power mode notification once.</summary>
    /// <remarks>
    ///     Only a device cycle needs them, so a session with integration off runs neither. Once started
    ///     they stay until shutdown; with integration switched off again the reconcile does nothing.
    /// </remarks>
    private void EnsurePowerLoops()
    {
        lock (_backgroundGate)
        {
            if (_disposed || _powerLoopsStarted)
            {
                return;
            }

            _powerLoopsStarted = true;
            _powerAssignmentTask = ObservePowerAssignmentsAsync();
            try
            {
                // A Windows power mode picked outside WSGM is adopted like an out-of-band watt change.
                _powerModeNotification = _registerPowerModeNotification(RequestPowerAssignmentReconcile);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Windows power mode notifications are unavailable: {ex.Message}");
            }
        }
    }

    private async Task ShutdownCoreAsync(PluginStopReason reason, Deadline deadline)
    {
        using var bounded = deadline.CreateCancellationSource();
        // First, while the power limit is still writable: AutoTDP hands back the limit it took over
        // from. Exiting with its last automatic wattage latched leaves the handheld on a value the
        // user never chose.
        var autoTdpStopped = false;
        try
        {
            await AutoTdp.StopAsync(deadline).ConfigureAwait(false);
            autoTdpStopped = AutoTdp.Completion.IsCompletedSuccessfully;
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
                [.. _backgroundTasks, _powerAssignmentTask, _oemActions.Completion, _pluginSettings.Completion];
        }

        var backgroundStopped =
            await FinishStepAsync("background work", Task.WhenAll(background)).ConfigureAwait(false);
        if (autoTdpStopped)
        {
            // AutoTDP no longer reads frames or writes trace rows; its owner releases both.
            await FinishStepAsync("AutoTDP trace", _autoTdpTrace.DisposeAsync().AsTask()).ConfigureAwait(false);
            CleanupProvider("AutoTDP frame times", _autoTdpFrametimes.Dispose);
        }

        if (!autoTdpStopped || !cycleStopped || !controllersStopped || !backgroundStopped
            || bounded.IsCancellationRequested)
        {
            Log.Warn("Device cleanup retained its remaining owners because work is still active or unverified.");
            return;
        }

        CleanupProvider("power notifications", () => _powerModeNotification?.Dispose());
        var diagnosticsStopped = await FinishStepAsync("diagnostics",
            _diagnostics?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
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
                // remaining owners alive once plugin code and its deferred disposal have retired.
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

    private Task StartCycleAsync(CancellationToken cancellationToken)
    {
        return RunUnderTransitionGateAsync(StartCycleUnderGateAsync, cancellationToken);
    }

    private async Task RunUnderTransitionGateAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
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

        EnsurePowerLoops();
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
        long cycleGeneration;
        HandheldDeviceRuntime client;
        try
        {
            _identity = _collectIdentity();
            DeviceDefinition = await _detectDevice(_identity, cancellationToken)
                .ConfigureAwait(false);
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
            Log.Info("Device cycle passive: unsupported machine identity.");
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        definition = detectedDefinition;
        if (!definition.HasController)
        {
            await Controllers.RecoverPhysicalControllerAsync("power-only device definition", cancellationToken)
                .ConfigureAwait(false);
        }

        cycleGeneration = Interlocked.Increment(ref _cycleGeneration);
        SetState(DeviceCycleState.Activating);
        try
        {
            client = await _loadRuntime(
                definition,
                cycleGeneration,
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
        try
        {
            Attach(client);
            Capabilities.Attach(client, cycleGeneration);
            UpdateCapabilityDesiredContext();
            _oemActions.Attach(client);
            UpdateOemConfiguration();
            var controllerManagement =
                _config.DeviceIntegration.ControllerManagementEnabled && definition.HasController;
            // Before the plugin starts, because the plugin's first job is to find the physical
            // controller and it cannot find one that HidHide is hiding from this process. Doing it
            // afterwards is too late for the cycle that needed it.
            await Controllers.EnsureHidHideReadableAsync(controllerManagement, cancellationToken)
                .ConfigureAwait(false);
            var activation = await client.StartAsync(_identity!, cycleGeneration, controllerManagement,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (activation.State is DeviceCycleState.Passive)
            {
                if (controllerManagement)
                {
                    await Controllers.RecoverPhysicalControllerAsync("device detection was passive", cancellationToken)
                        .ConfigureAwait(false);
                }

                await DetachAsync(client).ConfigureAwait(false);
                var passiveDeadline = NormalShutdownDeadline();
                await client.StopAsync(PluginStopReason.IntegrationDisabled, passiveDeadline, cancellationToken)
                    .ConfigureAwait(false);
                await DisposeRuntimeAsync(client, passiveDeadline).ConfigureAwait(false);
                _client = null;
                SetDeviceDefinitionId(null);
                SetState(DeviceCycleState.Passive);
                _automaticRestartAttempts = 0;
                Log.Info($"Device detection passive: package={definition.FamilyId}; runtime retired.");
                return;
            }

            // Before the profiles load: glyph selection is gated on the matched device definition,
            // and a catalog that arrives first would be selected against a null id and rejected.
            SetDeviceDefinitionId(activation.DeviceDefinitionId);

            // Attached after the definition is known, because stored values are keyed by it and by
            // the package: a value authored for one device must never be handed to another.
            _pluginSettings.Attach(
                client,
                activation.DeviceDefinitionId ?? string.Empty,
                definition.FamilyId,
                _config);
            LoadPhysicalGlyphProfiles(client);
            SetState(activation.State);
            _automaticRestartAttempts = 0;
            Log.Info(
                $"Device cycle active: package={definition.FamilyId}, "
                + $"cycleGeneration={cycleGeneration}, "
                + $"state={activation.State}.");
            Observe(ObserveRuntimeCompletionAsync(client), "plugin supervision");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // The runtime owns a bounded start deadline. If it expires after the plugin entered
            // StartAsync, hardware may already be acquired even though the caller token is live;
            // run the same bounded cleanup path used by caller cancellation.
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                PluginStopReason.StartCanceled,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                PluginStopReason.StartFailed,
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
            : new ValueTask(CleanupAbortedStartAsync(PluginStopReason.StartCanceled));
    }

    private async Task CleanupAbortedStartAsync(PluginStopReason reason)
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
        PluginStopReason reason,
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
                client, PluginStopReason.RuntimeFault, cleanupDeadline,
                cleanupCancellation.Token).ConfigureAwait(false);

            // An unverified step is logged, never a reason to stay down: HC's Close ignores its results,
            // and blocking the restart here left the Ally without its pad, fans and TDP until the next
            // sleep whenever a release write failed because the pad had already dropped.
            if (!cleanup.Verified)
            {
                Log.Warn($"Device plugin fault cleanup had unverified steps; restarting anyway: "
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
                $"Device plugin fault: generation={_cycleGeneration}, reason={exit.Reason}, "
                + $"detail={exit.Detail}.");
            ScheduleFaultRecovery();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error("Device plugin restart failed; cycle faulted", ex);
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
                "Device plugin start fault recovery suppressed: "
                + $"pending={_faultRecoveryPending}, disposed={_disposed}, "
                + $"integrationEnabled={_config.DeviceIntegration.Enabled}, "
                + $"failure={exception.Message}");
            return;
        }

        _faultRecoveryPending = true;
        Log.Error("Device plugin start failed", exception);
        Observe(HandleStartFaultAsync(), "plugin start fault recovery");
    }

    private async Task HandleStartFaultAsync()
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_faultRecoveryPending)
            {
                Log.Info("Device plugin start-fault worker stopped: recovery is no longer pending.");
                return;
            }

            _faultRecoveryPending = false;
            if (_disposed || !_config.DeviceIntegration.Enabled || _client is not null)
            {
                Log.Info(
                    "Device plugin start-fault worker stopped: "
                    + $"disposed={_disposed}, integrationEnabled={_config.DeviceIntegration.Enabled}, "
                    + $"runtimePresent={_client is not null}.");
                return;
            }

            ScheduleFaultRecovery();
        }
        finally
        {
            _transitionGate.Release();
        }
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
            $"Device plugin restart {_automaticRestartAttempts}/{AutomaticRestartBackoffs.Length} scheduled in "
            + $"{backoff.TotalSeconds:0.#} s.");
        Observe(RestartAfterDelayAsync(backoff), "delayed plugin restart");
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
            "Device plugin delayed restart skipped: "
            + $"disposed={_disposed}, integrationEnabled={_config.DeviceIntegration.Enabled}, "
            + $"runtimePresent={_client is not null}.");
    }

    private async Task<DeviceClientTeardownResult> StopCycleUnderGateAsync(
        PluginStopReason reason,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        _intentionalStop = true;
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
    ///     Attempts controller and plugin cleanup before detaching and disposing the runtime.
    ///     Every non-fatal unverified response or exception is retained while later phases continue.
    /// </summary>
    private async Task<DeviceClientTeardownResult> TeardownClientAsync(
        HandheldDeviceRuntime client,
        PluginStopReason reason,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        try
        {
            try
            {
                await ReleaseControllerAsync(client, HandoffScope.FullDeactivation, deadline,
                    cancellationToken, reason is PluginStopReason.RuntimeFault).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device controller release unverified; cleanup continues: {ex.Message}");
            }

            try
            {
                var stopped = await client.StopAsync(reason, deadline, cancellationToken)
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
                await DetachAsync(client).ConfigureAwait(false);
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

        var deadline = Deadline.After(TimeSpan.FromSeconds(6));
        if (!enabled)
        {
            await ReleaseControllerAsync(client, HandoffScope.ControllerOnly, deadline, cancellationToken)
                .ConfigureAwait(false);
            Log.Info("Controller management disabled.");

            // After the release, and never instead of it: the plugin remembers its acquisition policy
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
                    "The plugin did not acknowledge controller management being disabled; "
                    + $"restarting the plugin with the persisted policy: {ex.Message}");
                var teardown = await StopCycleUnderGateAsync(
                    PluginStopReason.RuntimeFault,
                    NormalShutdownDeadline(),
                    cancellationToken).ConfigureAwait(false);
                ReportDeviceTeardown(teardown, cancellationToken);
                await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Before the plugin is asked to acquire, exactly as at cycle start: it cannot discover an
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

    private void SynchronizeGenerationAfterLifecycleCall(
        HandheldDeviceRuntime client,
        long previousGeneration)
    {
        var activeGeneration = client.CycleGeneration;
        Interlocked.Exchange(ref _cycleGeneration, activeGeneration);
        if (activeGeneration == previousGeneration)
        {
            return;
        }

        Capabilities.MarkCycleGenerationChanged(activeGeneration);
        UpdateCapabilityDesiredContext();
        _oemActions.Reset();
    }

    /// <summary>Has the plugin let go after its virtual target was lost, and shows the physical pad.</summary>
    /// <param name="detail">Why the target was lost.</param>
    /// <remarks>
    ///     The plugin conversation runs only for the cycle that lost the target. When it is skipped (a newer
    ///     cycle, shutdown) or the transition gate is not reached in time, a manager still Faulted has no
    ///     virtual pad driving anything, so the physical pad is shown anyway rather than left hidden.
    /// </remarks>
    private void OnControllerTargetLost(string detail)
    {
        var client = _client;
        var generation = Interlocked.Read(ref _cycleGeneration);
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
                        || generation != Interlocked.Read(ref _cycleGeneration)
                        || Controllers.State is not ControllerManagementState.Faulted)
                    {
                        return;
                    }

                    await CancelControllerStartAsync().WaitAsync(budget.Token).ConfigureAwait(false);
                    if (Controllers.State is not ControllerManagementState.Faulted)
                    {
                        return;
                    }

                    // The release shows the physical pad in its own cleanup, whatever the plugin does, and
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
        client.ControllerSampleReceived += Controllers.Submit;
    }

    private ValueTask DetachAsync(HandheldDeviceRuntime client)
    {
        if (_lifecycleHandler is { } lifecycleHandler)
        {
            client.LifecycleStateReceived -= lifecycleHandler;
        }

        client.PhysicalIdentitiesReceived -= OnPhysicalIdentities;
        client.ControllerSampleReceived -= Controllers.Submit;
        // The plugin no longer owns the controller: no frame goes to it from here on.
        _hapticSink.Withdraw();
        Capabilities.Detach();
        _pluginSettings.Detach();
        _oemActions.Detach();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Starts WSGM-side controller management for the controller the plugin just took.
    /// </summary>
    /// <remarks>
    ///     Driven by the publication rather than by cycle start: WSGM may only hide a device and create
    ///     a virtual target once the plugin has actually acquired the physical one, and the plugin
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
    ///     raise a virtual target over a plugin that has let go, with the physical pad hidden. A start
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
            token => client.ReleaseControllerAsync(scope, deadline, token),
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
        UpdateCapabilityDesiredContext();
        RequestPowerAssignmentReconcile();
        UpdateOemConfiguration();
        if (!IntegrationEnabled)
        {
            return;
        }

        await ReconcileDesiredValuesAsync(
            $"profile generation {snapshot.Generation}",
            cancellationToken).ConfigureAwait(false);

        // Applied after the per-capability values so an explicitly selected fan profile wins for the
        // capability it covers, rather than the order deciding at random.
        await ApplyAuthoredProfilesAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await Controllers.ApplySelectionAsync(
            CurrentControllerSelection(),
            _runningApplicationId,
            _runningExecutable,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The controller selection from the device switches and the profile store.</summary>
    private ControllerSelection CurrentControllerSelection()
    {
        var selection = ControllerSelection.From(_config.DeviceIntegration, Profiles.Current.Config);
        return DeviceDefinition is { HasController: false }
            ? selection with
            {
                Enabled = false, DisabledDetail = "This device definition provides hardware controls only."
            }
            : selection;
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

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await PersistConfigurationAsync(
                config => config.DeviceIntegration.GlyphSelection = selection,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"Physical glyph presentation changed: {selection}.");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    /// <remarks>
    ///     Persisted rather than session-only, and applied by the ordinary configuration reload, so the
    ///     overlay switch and the Settings checkbox are the same setting reached two ways.
    /// </remarks>
    internal Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        return SetAutoTdpEnabledAsync(!_config.DeviceIntegration.AutoTdpEnabled, cancellationToken);
    }

    /// <summary>Sets AutoTDP to an explicit state and persists the choice.</summary>
    /// <param name="enabled">The state the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the setting is persisted.</returns>
    /// <remarks>
    ///     The comparison happens inside the transition gate so concurrent surfaces cannot invert a
    ///     newer persisted choice.
    /// </remarks>
    internal async Task SetAutoTdpEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled && AutoTdp.Availability is { Available: false } availability)
            {
                throw new InvalidOperationException(availability.Detail);
            }

            if (_config.DeviceIntegration.AutoTdpEnabled == enabled)
            {
                Log.Info($"AutoTDP is already {(enabled ? "on" : "off")}; nothing to persist.");
                return;
            }

            await PersistConfigurationAsync(
                config => config.DeviceIntegration.AutoTdpEnabled = enabled,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"AutoTDP switched {(enabled ? "on" : "off")} from the Device surface.");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Claims managed controller input for one visible WSGM surface.</summary>
    /// <param name="surfaceId">Nonblank identifier retained until this surface releases its claim.</param>
    /// <param name="cancellationToken">Cancels admission or target neutralization; coordinator shutdown also cancels it.</param>
    /// <returns>Completion after any required neutralization of the virtual target.</returns>
    internal async Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await Controllers.ClaimUiAsync(surfaceId, admission.Token).ConfigureAwait(false);
    }

    /// <summary>Releases one visible WSGM surface's managed controller claim.</summary>
    /// <param name="surfaceId">Identifier used by the matching UI claim.</param>
    /// <remarks>Forwarding resumes only after every captured control is released in a subsequent sample.</remarks>
    internal void ReleaseUi(string surfaceId)
    {
        Controllers.ReleaseUi(surfaceId);
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
        return await Controllers.PulseRearButtonAsync(button, admission.Token).ConfigureAwait(false);
    }

    /// <summary>Changes the managed-controller target in the layer in force and persists the choice.</summary>
    /// <param name="target">The target.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The controller state after the change was applied.</returns>
    /// <remarks>
    ///     The stored setting is changed and then the manager is asked to re-resolve, in that order, so
    ///     the persisted value and the running target cannot disagree if the apply fails — the setting
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
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Profiles.SetAsync(values => values.ControllerTarget = target, $"ControllerTarget={target}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Controllers.ApplySelectionAsync(
                CurrentControllerSelection(),
                _runningApplicationId,
                _runningExecutable,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
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

    /// <summary>Routes one semantic capability command through current validation and serialization.</summary>
    /// <param name="capabilityId">The capability being commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="origin">Who asked for it, which decides whether AutoTDP steps aside.</param>
    /// <param name="expectedCycle">Optional cycle captured by a restore operation.</param>
    /// <param name="expectedDescriptors">Optional descriptor generation captured by a restore.</param>
    /// <param name="applyPowerPair">Whether both limits of the declared sustained/boost pair move to this target.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The command result reported by the plugin.</returns>
    internal async Task<CapabilityCommandResult> ExecuteCapabilityAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CapabilityCommandOrigin origin = CapabilityCommandOrigin.User,
        long? expectedCycle = null,
        long? expectedDescriptors = null,
        bool applyPowerPair = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed && origin is not CapabilityCommandOrigin.AutoTdp)
        {
            return ClosedCapabilityAdmission();
        }

        using var admission = origin is CapabilityCommandOrigin.AutoTdp
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission?.Token ?? cancellationToken;
        var power = FindDescriptor(capabilityId, instanceId)?.Role is
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit or CapabilityRole.ScenarioMode;
        if (origin == CapabilityCommandOrigin.User
            && FindDescriptor(capabilityId, instanceId)?.Role == CapabilityRole.PowerSustainedLimit)
        {
            applyPowerPair |= Profiles.Current.Layers.ManualTdp()?.Unified == true;
        }

        if (power)
        {
            await _powerLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ExecuteCapabilityCoreAsync(capabilityId, instanceId, value, timeout, origin,
                    expectedCycle, expectedDescriptors, applyPowerPair, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (power)
            {
                _powerLane.Release();
            }
        }
    }

    /// <summary>
    ///     Restores the power limit the running application prefers, and takes back the one the outgoing
    ///     application imposed.
    /// </summary>
    /// <param name="manualProfile">The power limit the layers resolve to, or null when none is preferred.</param>
    /// <param name="applicationId">The running application, or null for the desktop.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <returns>A task completing once the decision was carried out.</returns>
    /// <remarks>
    ///     Runs whole inside the power lane, so the imposed flags it decides from are the ones the manual
    ///     funnel last wrote, and no preset, AutoTDP or user write lands between the decision and its
    ///     writes. A power-source preset assignment in force owns the limits instead. The decision is
    ///     pure and tested (<see cref="PerApplicationPowerPolicy" />).
    /// </remarks>
    internal async Task ReconcileApplicationPowerAsync(
        ManualTdpProfile? manualProfile,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _powerLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var power = Capabilities.Snapshot().FirstOrDefault(view => view.Descriptor is
            {
                Role: CapabilityRole.PowerSustainedLimit,
                SupportsWrite: true,
                ValueKind: CapabilityValueKind.Integer
            });
            if (power is null || PowerAssignments.HasCurrentAssignment)
            {
                return;
            }

            var (effective, paired) = manualProfile is null
                ? (null, false)
                : manualProfile.Unified
                    ? (manualProfile.UnifiedWatts, true)
                    : (manualProfile.SustainedWatts, false);
            var ceiling = power.Descriptor.Maximum ?? 0;
            var autoTdpEnabled = AutoTdpEnabled;
            var decision = PerApplicationPowerPolicy.DecideOnTargetChange(
                effective,
                _profilePowerImposed,
                autoTdpEnabled,
                ceiling);
            var target = applicationId ?? "the global profile";
            switch (decision.Action)
            {
                case PerAppPowerAction.Apply:
                    var boost = manualProfile is { Unified: false }
                                && power.Descriptor.PairedPowerLimitId is { } peerId
                        ? Capabilities.TryGetView(new DeviceCapabilityKey(peerId, null))?.Projection.DesiredValue
                            ?.IntegerValue
                        : null;
                    // A value the user just set by hand is already on the device; writing it again would
                    // only pause AutoTDP a second time.
                    var applied = boost is { } boostWatts
                        ? await RestoreSplitPowerAsync(power, decision.Watts, boostWatts, cancellationToken)
                            .ConfigureAwait(false)
                        : power.Projection.State.ObservedValue?.IntegerValue == decision.Watts
                          || await ApplyProfilePowerLimitAsync(power, decision.Watts, paired, cancellationToken)
                              .ConfigureAwait(false);
                    if (applied)
                    {
                        // An explicit limit overrides automatic control exactly as moving the slider
                        // does; pausing while it is applied keeps AutoTDP from writing over it next tick.
                        if (autoTdpEnabled)
                        {
                            AutoTdp.NoteManualChange(decision.Watts);
                        }

                        _profilePowerImposed = true;
                        _profilePowerPaired = paired || boost is not null;
                        Log.Info($"Per-application power limit applied: {decision.Watts} W for {target}.");
                    }

                    break;
                case PerAppPowerAction.ResumeAutomatic:
                    AutoTdp.ResumeAutomaticControl();
                    _profilePowerImposed = false;
                    _profilePowerPaired = false;
                    Log.Info($"Per-application power limit released; automatic control resumes for {target}.");
                    break;
                case PerAppPowerAction.ReleaseToCeiling:
                    if (ceiling > 0
                        && await ApplyProfilePowerLimitAsync(power, ceiling, _profilePowerPaired, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        _profilePowerImposed = false;
                        _profilePowerPaired = false;
                        Log.Info(
                            $"Per-application power limit released to the device ceiling {ceiling} W for {target}.");
                    }

                    break;
                case PerAppPowerAction.Leave:
                    break;
                default:
                    Log.Warn($"Per-application power decision {decision.Action} is unknown; the limit is left as is.");
                    break;
            }
        }
        finally
        {
            _powerLane.Release();
        }
    }

    /// <summary>Writes a stored per-application limit; called inside the power lane.</summary>
    /// <returns>Whether the write was dispatched; a readback is never required.</returns>
    private async Task<bool> ApplyProfilePowerLimitAsync(
        DeviceCapabilityView power,
        int watts,
        bool paired,
        CancellationToken cancellationToken)
    {
        var state = power.Projection.State;
        // Not a user action: the value is already the saved preference, so it must not re-enter the
        // manual funnel and be persisted again or re-resolved into the wrong layer.
        var result = await ExecuteCapabilityCoreAsync(power.Descriptor.CapabilityId, power.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
            TimeSpan.FromSeconds(5), CapabilityCommandOrigin.ProfileRestore,
            state.CycleGeneration, state.DescriptorGeneration, paired, cancellationToken).ConfigureAwait(false);
        var applied = result.Outcome.IsApplied();
        if (!applied)
        {
            Log.Warn($"Per-application power limit {watts} W was not applied: "
                     + (result.Reason?.Detail ?? result.Outcome.ToString()));
        }

        return applied;
    }

    /// <summary>Restores a split sustained/boost preference; called inside the power lane.</summary>
    /// <returns>Whether both writes were dispatched.</returns>
    private async Task<bool> RestoreSplitPowerAsync(DeviceCapabilityView primary, int sustained, int boost,
        CancellationToken cancellationToken)
    {
        var peerId = primary.Descriptor.PairedPowerLimitId;
        var peer = peerId is null ? null : FindCapability(peerId, null);
        if (peer is null || !ManualTdpPolicy.Accepts(peer.Descriptor.Minimum, peer.Descriptor.Maximum,
                peer.Descriptor.Step, boost))
        {
            return false;
        }

        var state = primary.Projection.State;
        // The unified write moves both limits to the sustained target before the independent boost
        // preference is restored, so the boost write never finds the sustained limit above it.
        var pair = await ExecuteCapabilityCoreAsync(primary.Descriptor.CapabilityId, primary.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = sustained },
            TimeSpan.FromSeconds(5), CapabilityCommandOrigin.ProfileRestore,
            state.CycleGeneration, state.DescriptorGeneration, true, cancellationToken).ConfigureAwait(false);
        if (!pair.Outcome.IsApplied())
        {
            return false;
        }

        AutoTdp.NoteManualChange(sustained);
        var result = await ExecuteCapabilityCoreAsync(peerId!, null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = boost },
            TimeSpan.FromSeconds(5), CapabilityCommandOrigin.ProfileRestore,
            state.CycleGeneration, state.DescriptorGeneration, false, cancellationToken).ConfigureAwait(false);
        return result.Outcome.IsApplied();
    }

    /// <summary>Persists a hand-set power limit to whichever profile layer is in force.</summary>
    /// <param name="watts">The limit the user just set, already applied to the device.</param>
    /// <remarks>
    ///     Runs from the manual funnel inside the power lane, so the value has already reached the device
    ///     and paused AutoTDP. This only records it in the layer in force, the game profile while it is on
    ///     and Global otherwise, so the next launch restores it instead of the value leaking onto whatever
    ///     runs next.
    /// </remarks>
    private void PersistManualPowerLimit(int watts)
    {
        var layers = Profiles.Current.Layers;
        var manual = layers.ManualTdp();
        var key = layers.PowerTargetKey;
        var current = manual is null ? null : manual.Unified ? manual.UnifiedWatts : manual.SustainedWatts;

        // A drag that ends on the stored value writes no config.
        _profilePowerImposed = true;
        _profilePowerPaired = manual?.Unified == true;
        if (current == watts)
        {
            return;
        }

        Observe(Profiles.SetAsync(key.Field, watts), $"save of the {watts} W power limit");
    }

    private async Task<CapabilityCommandResult> ExecuteCapabilityCoreAsync(
        string capabilityId, string? instanceId, CapabilityValue? value, TimeSpan timeout,
        CapabilityCommandOrigin origin, long? expectedCycle, long? expectedDescriptors, bool applyPowerPair,
        CancellationToken cancellationToken)
    {
        if (_disposed && origin is not CapabilityCommandOrigin.AutoTdp)
        {
            return ClosedCapabilityAdmission();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var user = origin is CapabilityCommandOrigin.User;
        if (user)
        {
            Interlocked.Increment(ref _userCapabilityCommands);
        }

        try
        {
            if (user && value is not null && _identity is not null
                && FindCapability(capabilityId, instanceId) is { } view)
            {
                var handled = await CapabilityUserWrites.HandleUserWriteAsync(Profiles,
                    DeviceMachineIdentity.StableKey(_identity), view, value, async () =>
                    {
                        var written = await Capabilities.ExecuteAsync(capabilityId, instanceId, value, timeout,
                            expectedCycle, expectedDescriptors, applyPowerPair,
                            cancellationToken).ConfigureAwait(false);
                        NotifyManualPowerChange(capabilityId, instanceId, value, written);
                        return written;
                    }, _manualVariableRefresh, cancellationToken).ConfigureAwait(false);
                UpdateCapabilityDesiredContext();
                return handled;
            }

            var result = await Capabilities.ExecuteAsync(
                capabilityId,
                instanceId,
                value,
                timeout,
                expectedCycle, expectedDescriptors, applyPowerPair, cancellationToken).ConfigureAwait(false);
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (origin)
            {
                case CapabilityCommandOrigin.User:
                    NotifyManualPowerChange(capabilityId, instanceId, value, result);
                    NotifyManualVariableRefreshChange(capabilityId, instanceId, value, result);
                    await PersistUserCapabilityValueAsync(
                        capabilityId,
                        instanceId,
                        value,
                        result,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case CapabilityCommandOrigin.DesiredStateRestore
                    when result.Outcome.IsApplied()
                         && FindDescriptor(capabilityId, instanceId)?.Role is CapabilityRole.PowerSustainedLimit
                         && value?.IntegerValue is { } watts:
                    AutoTdp.NoteManualChange(watts);
                    break;
            }

            return result;
        }
        finally
        {
            if (user)
            {
                Interlocked.Decrement(ref _userCapabilityCommands);
            }
        }
    }

    private static CapabilityCommandResult ClosedCapabilityAdmission()
    {
        return new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                "The device session is stopping."),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private void NotifyManualPowerChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (value?.IntegerValue is not { } watts
            || !result.Outcome.IsApplied())
        {
            return;
        }

        var views = Capabilities.Snapshot();
        var primaryPowerLimit = views.Any(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && string.Equals(view.Descriptor.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(view.Descriptor.InstanceId, instanceId, StringComparison.Ordinal));
        if (!primaryPowerLimit)
        {
            var primary = views.FirstOrDefault(view =>
                view.Descriptor.PairedPowerLimitId == capabilityId && instanceId is null);
            if (primary?.Projection.State.ObservedValue?.IntegerValue is { } sustained)
            {
                // A companion edit hands the runtime pair back without persisting observed
                // sustained wattage as a new primary preference.
                AutoTdp.NoteManualChange(sustained);
            }

            return;
        }

        // Permanent until the user resumes control, by specification: quietly taking the limit back
        // a few seconds after they set it by hand would make the manual control look broken.
        Log.Info($"AutoTDP paused: the sustained power limit was set to {watts} W by hand.");
        AutoTdp.NoteManualChange(watts);
        PersistManualPowerLimit(watts);
    }

    /// <summary>Hands a hand-set variable-refresh state to the performance profile that owns it.</summary>
    /// <remarks>
    ///     The Device row and Steam's own control are two ways to press the same switch, and only the
    ///     second used to reach the profile. Routing both through here keeps one stored answer for the
    ///     feature instead of giving the overlay a second one under device integration.
    /// </remarks>
    private void NotifyManualVariableRefreshChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (value?.BooleanValue is not { } enabled
            || !result.Outcome.IsApplied()
            || FindDescriptor(capabilityId, instanceId)?.Role
                is not CapabilityRole.VariableRefreshRate)
        {
            return;
        }

        _manualVariableRefresh(enabled);
    }

    /// <summary>Records a value the user just set as the desired state of the layer in force.</summary>
    /// <param name="capabilityId">The capability that was commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="result">What the plugin reported.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the value is stored, or immediately when it is not.</returns>
    /// <remarks>
    ///     Without this the Device surface commanded hardware and remembered nothing: every row went
    ///     back to whatever the firmware held on the next cycle or the next boot. The descriptor's profile
    ///     scope and the profile store decide the layer (<see cref="CapabilityUserWrites" />).
    ///     <para>
    ///         Applied after the device took the value, not before: recording a preference the hardware
    ///         refused would restore a value on the next launch that the device never accepted. An
    ///         unverified write still counts, because the desired state is what the user asked for and the
    ///         plugin reports that it wrote it.
    ///     </para>
    /// </remarks>
    private async Task PersistUserCapabilityValueAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result,
        CancellationToken cancellationToken)
    {
        if (value is null
            || !result.Outcome.IsApplied())
        {
            return;
        }

        var view = FindCapability(capabilityId, instanceId);
        if (view is null
            || !view.Descriptor.SupportsWrite
            || CapabilityUserWrites.PerformanceProfileOwnsRole(view.Descriptor.Role))
        {
            return;
        }

        if (_identity is null)
        {
            Log.Warn(
                $"Device value for {capabilityId}{Instance(instanceId)} was applied but not saved: "
                + "this machine has no resolved device identity to store it under.");
            return;
        }

        // Restores never reach persistence. A user control landing back on the desired value
        // also needs no configuration write.
        if (await CapabilityUserWrites.PersistAsync(Profiles, DeviceMachineIdentity.StableKey(_identity), view,
                value, cancellationToken).ConfigureAwait(false))
        {
            UpdateCapabilityDesiredContext();
        }
    }

    /// <summary>The published view of one capability instance, or null when none is published.</summary>
    private DeviceCapabilityView? FindCapability(string capabilityId, string? instanceId)
    {
        return Capabilities.TryGetView(new DeviceCapabilityKey(capabilityId, instanceId));
    }

    private CapabilityDescriptor? FindDescriptor(string capabilityId, string? instanceId)
    {
        return FindCapability(capabilityId, instanceId)?.Descriptor;
    }

    /// <summary>Attaches WSGM-owned UI and system actions after the shell surfaces exist.</summary>
    /// <param name="actions">Borrowed callbacks owned by the shell; these supply UI/system policy rather than device firmware.</param>
    internal void ConfigureOemActions(DeviceOemActionServices actions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _oemActions.ConfigureActions(actions);
    }

    /// <summary>The authored fan profiles for the active device, the choice in force, and its scope.</summary>
    /// <returns>Null when the device has no authored profiles at all.</returns>
    /// <remarks>
    ///     Read on every snapshot rather than cached: the answer follows both a configuration reload
    ///     and a change of running application, and it is a handful of list lookups against objects
    ///     already in memory.
    /// </remarks>
    internal (IReadOnlyList<DeviceAuthoredProfile> Profiles, Resolved<string?> Selected)?
        AuthoredProfileSelection()
    {
        var scope = ActivePluginScope(candidate => candidate.Profiles.Count > 0);
        if (scope is null)
        {
            return null;
        }

        var profiles = scope.Profiles
            .Where(profile => profile.CapabilityId == CapabilityIds.FanCurve)
            .ToArray();
        return profiles.Length == 0
            ? null
            : (profiles, Profiles.Current.Layers.Reference(values => values.FanCurveProfileId));
    }

    /// <summary>Advances the authored fan profile and applies the new choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the selection is persisted and applied.</returns>
    /// <remarks>
    ///     Scoped to the running application when there is one and global otherwise, because that is
    ///     what a user means by changing this row: mid-game they are changing it for what they are
    ///     playing, and on the desktop there is no per-game scope to mean.
    ///     <para>
    ///         Persisted first, then applied. The reverse order leaves the device running a profile the
    ///         configuration does not name if the save fails, which survives into the next session as a
    ///         device state nothing explains.
    ///     </para>
    /// </remarks>
    internal Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        var selection = AuthoredProfileSelection();
        return SelectAuthoredProfileAsync(selection is { } current
            ? ProfileEdits.NextAuthoredProfile(current.Profiles.Select(profile => profile.ProfileId).ToArray(),
                current.Selected.Value)
            : null, cancellationToken);
    }

    /// <summary>Persists and applies an explicit authored profile selection for the current application scope.</summary>
    /// <param name="next">Current device profile identifier, or null to clear the layer's selection.</param>
    /// <param name="cancellationToken">Cancels persistence or application; shutdown also cancels admission.</param>
    /// <returns>Completion after persistence and the application attempt; missing/unknown profiles are ignored.</returns>
    internal async Task SelectAuthoredProfileAsync(string? next, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = admission.Token;
        var current = ActivePluginScope(candidate => candidate.Profiles.Count > 0);
        if (current is null || (next is not null && !current.Profiles.Any(profile => profile.ProfileId == next)))
        {
            return;
        }

        var snapshot = await Profiles.SetAsync(values => values.FanCurveProfileId = next,
            $"fan profile {next ?? "(none)"}", cancellationToken: cancellationToken).ConfigureAwait(false);
        await ApplyAuthoredProfilesAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the authored profile in force for the running application.</summary>
    /// <param name="snapshot">The profiles and the application they resolve for.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <remarks>
    ///     Every failure here is contained. A profile that cannot be applied is a degraded feature, not
    ///     a reason to fault the session, and the applier already logs which step refused it.
    /// </remarks>
    private async Task ApplyAuthoredProfilesAsync(
        ProfileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        var selected = snapshot.Layers.Reference(values => values.FanCurveProfileId);
        var scope = ActivePluginScope(candidate => candidate.Profiles.Count > 0);
        if (selected.Value is null || scope is null)
        {
            return;
        }

        try
        {
            await DeviceProfileApplier.ApplyAsync(
                scope.Profiles.FirstOrDefault(profile => profile.ProfileId == selected.Value),
                selected,
                CapabilityIds.FanCurve,
                DescribeCapability,
                // A started write runs to its deadline, as in ReconcileDesiredValuesAsync.
                (capabilityId, value, _) => ExecuteCapabilityAsync(
                    capabilityId,
                    null,
                    value,
                    TimeSpan.FromSeconds(5),
                    CapabilityCommandOrigin.AutomaticControl,
                    cancellationToken: _lifetime.Token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Applying the fan profile failed: {ex.Message}");
        }
    }

    /// <summary>Reads the descriptor the device publishes right now for one capability.</summary>
    /// <remarks>
    ///     At apply time rather than cached: a plugin republishes its capabilities across a cycle, and a
    ///     curve checked against a stale descriptor is exactly the case the pre-apply check exists for.
    /// </remarks>
    private CapabilityDescriptor? DescribeCapability(string capabilityId)
    {
        return Capabilities.Snapshot().FirstOrDefault(view => string.Equals(
            view.Descriptor.CapabilityId,
            capabilityId,
            StringComparison.Ordinal))?.Descriptor;
    }

    /// <summary>The stored settings scope of the active device and plugin matching a predicate.</summary>
    private PluginSettingsScope? ActivePluginScope(Func<PluginSettingsScope, bool> predicate)
    {
        var device = ActiveDeviceDefinitionId;
        var plugin = DeviceDefinition?.FamilyId;
        if (device is null || plugin is null)
        {
            return null;
        }

        return _config.DeviceIntegration.PluginSettings.LastOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, device, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, plugin, StringComparison.Ordinal)
            && predicate(candidate));
    }

    /// <summary>Writes every persistent desired value the hardware does not already hold.</summary>
    /// <param name="reason">What asked for the reconciliation, for the log.</param>
    /// <param name="cancellationToken">Cancels the remaining commands.</param>
    /// <param name="lightingOnly">Limits readiness-triggered restoration to lighting.</param>
    /// <returns>A task completing once every affected capability has been attempted.</returns>
    /// <remarks>
    ///     Per-capability and independent: one refusal must not stop the rest, because a profile that
    ///     applied its fan curve but not its power limit is still better than one that applied nothing.
    ///     A value the device already reports is skipped, so reselecting the active profile is free.
    /// </remarks>
    private async Task ReconcileDesiredValuesAsync(
        string reason, CancellationToken cancellationToken, bool lightingOnly = false)
    {
        await _profileReconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await CapabilityDesiredReconciler.RunAsync(
                new CapabilityReconcilePass(
                    "Device",
                    Capabilities.Snapshot,
                    (view, desired, token) => RestoreDesiredValueAsync(view, desired, reason, token))
                {
                    ReadCurrent = Capabilities.TryGetView,
                    Priority = ReconciliationPriority,
                    Include = view => (!lightingOnly || DeviceLightingRestore.IsLighting(view.Descriptor.Role))
                                      && !(view.Descriptor.Role is CapabilityRole.PowerSlowLimit
                                           && (ManualTdpUnified || PowerAssignments.HasCurrentAssignment)),
                    Abandon = () => lightingOnly && Volatile.Read(ref _userCapabilityCommands) != 0
                },
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileReconcileGate.Release();
        }
    }

    /// <summary>Writes one admitted desired value, with the lighting attempt budget around it.</summary>
    /// <returns>The result, or null when a lighting zone has no attempt left.</returns>
    private async Task<CapabilityCommandResult?> RestoreDesiredValueAsync(
        DeviceCapabilityView view,
        CapabilityValue desired,
        string reason,
        CancellationToken cancellationToken)
    {
        var lighting = DeviceLightingRestore.IsLighting(view.Descriptor.Role);
        var attempt = lighting ? _lightingRestore.TryBegin(view) : 0;
        if (lighting && attempt == 0)
        {
            return null;
        }

        var outcome = CommandOutcome.Indeterminate;
        try
        {
            // The pass is cancelled between writes, never inside one: a write cancelled in flight
            // ends Indeterminate, and a device without readback can never settle it. A profile
            // change 700 ms after a game started left the Ally's scenario that way for the rest of
            // the session (2026-09-29). The command's own deadline still bounds the wait.
            var result = await ExecuteCapabilityAsync(
                view.Descriptor.CapabilityId,
                view.Descriptor.InstanceId,
                desired,
                TimeSpan.FromSeconds(5),
                CapabilityCommandOrigin.DesiredStateRestore,
                view.Projection.State.CycleGeneration,
                view.Projection.State.DescriptorGeneration,
                cancellationToken: _lifetime.Token).ConfigureAwait(false);
            outcome = result.Outcome;
            return result;
        }
        finally
        {
            if (lighting)
            {
                _lightingRestore.Complete(view, outcome);
                Log.Change(
                    $"device-restore/{view.Descriptor.CapabilityId}{Instance(view.Descriptor.InstanceId)}",
                    $"Device restore {view.Descriptor.CapabilityId}{Instance(view.Descriptor.InstanceId)} "
                    + $"from {view.Projection.DesiredSource} ({reason}): outcome={outcome}, "
                    + $"attempt {attempt} of {DeviceLightingRestore.MaxAttempts}.",
                    outcome.IsApplied() ? LogLevel.Info : LogLevel.Warn);
            }
        }
    }

    /// <summary>Orders coupled power writes so their transient pair remains valid.</summary>
    /// <param name="view">Capability and its observed and desired values.</param>
    /// <returns>Lower values are written first.</returns>
    private static int ReconciliationPriority(DeviceCapabilityView view)
    {
        var observed = view.Projection.State.ObservedValue?.IntegerValue;
        var desired = view.Projection.DesiredValue?.IntegerValue;
        return view.Descriptor.Role switch
        {
            // Lower PL1 before lowering PL2, otherwise the new PL2 can fall below the old PL1.
            CapabilityRole.PowerSustainedLimit when desired < observed => 0,
            // Raise PL2 before raising PL1, otherwise the new PL1 can exceed the old PL2.
            CapabilityRole.PowerSlowLimit when desired > observed => 0,
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit => 1,
            _ => 2
        };
    }

    private static string Instance(string? instanceId)
    {
        return instanceId is { Length: > 0 } ? $"/{instanceId}" : string.Empty;
    }

    private void UpdateCapabilityDesiredContext()
    {
        // Unknown power reads as AC here: only a confirmed battery state selects battery values.
        Capabilities.UpdateDesiredContext(DeviceIdentityKey, Profiles.Current.Layers, _readOnAcPower() ?? true);
    }

    private void UpdateOemConfiguration()
    {
        _oemActions.UpdateConfiguration(
            _config.DeviceIntegration.OemAssignments,
            ControllerManagementEnabled,
            ControllerTargetSelection.Resolve(Profiles.Current.Config, _runningApplicationId,
                _runningExecutable).Target);
    }

    private void LoadPhysicalGlyphProfiles(HandheldDeviceRuntime runtime)
    {
        try
        {
            var imported = runtime.Device is { } device
                ? GlyphPackageImporter.Import(new EmbeddedGlyphSource(device))
                : new GlyphPackageImportResult([], []);
            PhysicalGlyphCatalog.ReplacePackageProfiles(imported.Profiles);
            foreach (var error in imported.Errors)
            {
                Log.Warn(
                    $"Device glyph profile rejected: profile={error.ProfileId}, code={error.Code}, path={error.Path}, detail={error.Message}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            PhysicalGlyphCatalog.ReplacePackageProfiles([]);
            Log.Warn($"Device glyph catalog unavailable: {exception.Message}");
        }
    }

    private static Task<HandheldDefinition?> DetectDeviceAsync(DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(HandheldDeviceAdapter.Detect(identity));
    }

    private DeviceCoordinatorDiagnosticsSnapshot DiagnosticsSnapshot()
    {
        var capabilities = Capabilities.Snapshot();
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = State,
            InstalledPackage = DeviceDefinition is { } definition
                ? new DeviceInstalledPackageDiagnostic(definition.FamilyId,
                    typeof(HandheldDevice).Assembly.GetName().Version?.ToString() ?? "unknown")
                : null,
            CycleGeneration = Interlocked.Read(ref _cycleGeneration),
            CapabilityCount = capabilities.Count,
            HealthyCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State is
                {
                    Available: true,
                    Quality: HardwareStateQuality.Observed or HardwareStateQuality.Verified
                }),
            FaultedCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Quality is HardwareStateQuality.Faulted),
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private void OnLifecycleState(HandheldDeviceRuntime client, DevicePluginState state)
    {
        // Every stop and fault teardown clears the current client before it starts and sets the
        // deactivating and disabled states itself; a late notification from that client must not
        // start a restore pass. Dropped, not queued.
        if (!ReferenceEquals(client, Volatile.Read(ref _client)))
        {
            return;
        }

        if (state.CycleGeneration != Interlocked.Read(ref _cycleGeneration))
        {
            Log.Warn(
                $"Device lifecycle notification rejected as stale: "
                + $"cycle={state.CycleGeneration}, current={_cycleGeneration}.");
            return;
        }

        SetDeviceDefinitionId(state.DeviceDefinitionId);
        SetState(state.State);
    }

    /// <summary>Records which device definition the plugin matched.</summary>
    /// <param name="deviceDefinitionId">The matched definition, or null when detection did not match.</param>
    /// <remarks>
    ///     Every glyph surface — the Steam Input page, the overlay's glyph rows, and the navigation
    ///     hints — resolves through <see cref="PhysicalGlyphSelectionSnapshot" />, which will only return
    ///     a profile that names the active device. The plugin publishes it with lifecycle state;
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
        Log.Info($"Device cycle: state={state}, cycleGeneration={_cycleGeneration}.");
        StateChanged?.Invoke(state);
        if (!wasRunning && state is DeviceCycleState.Active or DeviceCycleState.Degraded && IntegrationEnabled)
        {
            // A new cycle, including one after sleep, can start with firmware defaults, so every
            // desired value is restored once, whichever profile layer it comes from. The power preset
            // waits for this pass instead of competing with it for the plugin's command lane, and is
            // reconciled once the pass has completed.
            var restore = Task.Run(async () =>
            {
                try
                {
                    await ReconcileDesiredValuesAsync($"device {state}", _lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    RequestPowerAssignmentReconcile();
                }
            });
            Volatile.Write(ref _resumeRestore, restore);
            Observe(restore, "cycle restore");
        }

        RequestPowerAssignmentReconcile();

        OnLightingStateChanged(Capabilities.Snapshot());
    }

    private void OnLightingStateChanged(IReadOnlyList<DeviceCapabilityView> views)
    {
        if (_disposed || !IntegrationEnabled || State is not (DeviceCycleState.Active or DeviceCycleState.Degraded)
            || Volatile.Read(ref _userCapabilityCommands) != 0
            || !views.Any(_lightingRestore.CanApply)
            || Interlocked.CompareExchange(ref _lightingRestoreScheduled, 1, 0) != 0)
        {
            return;
        }

        Observe(Task.Run(async () =>
        {
            try
            {
                await ReconcileDesiredValuesAsync("lighting ready", _lifetime.Token, true)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _lightingRestoreScheduled, 0);
            }
        }), "lighting restore");
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

    private sealed class EmbeddedGlyphSource(HandheldDeviceAdapter device) : IGlyphPackageSource
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

    /// <summary>What a resume does with the cycle it finds.</summary>
    internal enum ResumeAction
    {
        /// <summary>Nothing to resume.</summary>
        Skip,

        /// <summary>Resume the suspended plugin in place.</summary>
        Resume,

        /// <summary>Stop whatever is there and start a fresh cycle.</summary>
        Restart
    }

    /// <summary>What the power-preset projection reads from one power control.</summary>
    /// <param name="Cycle">The cycle generation of its state.</param>
    /// <param name="Descriptors">The descriptor generation of its state, which also versions the declared presets.</param>
    /// <param name="Commandable">Whether it takes commands, which decides preset availability.</param>
    /// <param name="Observed">Its observed value.</param>
    private readonly record struct PowerControlReading(
        long Cycle,
        long Descriptors,
        bool Commandable,
        CapabilityValue? Observed)
    {
        internal static PowerControlReading Of(DeviceCapabilityView view)
        {
            var state = view.Projection.State;
            return new PowerControlReading(state.CycleGeneration, state.DescriptorGeneration,
                DeviceCapabilityRouter.CanCommand(state), state.ObservedValue);
        }
    }
}

/// <summary>Complete retained outcome of controller handoff, plugin stop, detach, and disposal.</summary>
/// <param name="Failures">All retained cleanup failures; an empty list means no stage reported an unverified outcome.</param>
internal sealed record DeviceClientTeardownResult(IReadOnlyList<Exception> Failures)
{
    /// <summary>Shared result for cleanup with no reported failures.</summary>
    internal static DeviceClientTeardownResult Clean { get; } = new([]);

    /// <summary>Whether no cleanup stage reported failure; this does not add independent hardware evidence.</summary>
    internal bool Verified => Failures.Count == 0;

    /// <summary>Combines retained cleanup failures for reporting without rerunning their operations.</summary>
    /// <returns>The sole failure, or a combined exception describing multiple failures.</returns>
    internal Exception ToException()
    {
        return Failures.Combine("Multiple device teardown steps were unverified.");
    }
}
