using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
using WSGM.Plugin.Sdk;

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
public sealed class DeviceCoordinator : IAsyncDisposable
{
    internal const string ProductionOwnerName = WSGM.Shared.SessionProtocolNames.DeviceOwner;

    /// <summary>The delay before each automatic restart of a faulted plugin; its length is the restart budget.</summary>
    private static readonly TimeSpan[] AutomaticRestartBackoffs = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    private static readonly TimeSpan CanceledStartCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly Lock _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly DeviceCoordinatorDiagnosticsServer _diagnostics;
    private readonly PluginHapticSink _hapticSink;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeviceLightingRestore _lightingRestore = new();
    private readonly DeviceOemActionRouter _oemActions = new();
    private readonly Mutex _ownerMutex;
    private readonly ConfigStore _store;
    private readonly PluginSettingsCoordinator _pluginSettings;

    /// <summary>
    ///     Wakes the power-assignment reconcile. One pending signal stands for any number of changes,
    ///     because the reconcile reads everything it needs afresh.
    /// </summary>
    private readonly Channel<bool> _powerAssignmentChanges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly Task _powerAssignmentTask;

    private readonly EffectivePowerModeNotification? _powerModeNotification;
    private readonly SemaphoreSlim _profileReconcileGate = new(1, 1);
    private readonly uint _sessionId;
    private Task? _shutdownTask;
    private Task _runtimeRetirement = Task.CompletedTask;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private Action<int>? _assignedPowerOverride;
    private Func<AutoTdpAvailability>? _autoTdpAvailability;
    private Action<int>? _autoTdpManualOverride;
    private int _automaticRestartAttempts;
    private DevicePluginRuntime? _client;
    private AppConfig _config;
    private Task _controllerPublication = Task.CompletedTask;

    /// <summary>Cancels the controller-management start the last publication began.</summary>
    private CancellationTokenSource _controllerStartCancellation = new();

    private long _cycleGeneration;
    private bool _disposed;
    private bool _faultRecoveryPending;
    private DeviceIdentitySnapshot? _identity;
    private bool _intentionalStop;
    private int _lightingRestoreScheduled;
    private Action<bool>? _manualVariableRefreshOverride;

    /// <summary>The power controls' last reading, so only a change to them wakes the assignment reconcile.</summary>
    /// <remarks>Read and written on the UI thread, where the router raises its change event.</remarks>
    private (PowerControlReading Sustained, PowerControlReading Slow, PowerControlReading Scenario) _powerControls;

    private Task _resumeRestore = Task.CompletedTask;
    private string? _runningApplicationId;
    private string? _runningExecutable;
    private int _userCapabilityCommands;

    private DeviceCoordinator(
        AppConfig config,
        ConfigStore store,
        uint sessionId,
        Mutex ownerMutex,
        Action<Action> postToUi,
        ProfileService profiles)
    {
        _config = config;
        _store = store;
        Profiles = profiles;
        _sessionId = sessionId;
        _ownerMutex = ownerMutex;
        Capabilities = new DeviceCapabilityRouter(postToUi);
        Capabilities.Changed += OnLightingStateChanged;
        Capabilities.Changed += OnPowerControlsChanged;
        Capabilities.DescriptorsAccepted += OnPowerDescriptorsAccepted;
        // Scenario targets are one-shot preset steps. Persist only the watt controls through the
        // manual funnel; saving an AC scenario as desired state would replay it on battery later.
        PowerPresets = new DevicePowerPresets(() => IntegrationEnabled ? Capabilities.Snapshot() : [],
            ExecutePresetCapabilityAsync, WindowsPowerModes.Windows, ReadOnAcPower);
        PowerAssignments = new DevicePowerAssignments(PowerPresets,
            () => new DevicePowerAssignmentContext(Profiles.Current,
                InstalledPackage?.Manifest?.Id,
                _cycleGeneration, IntegrationEnabled, ReadOnAcPower()),
            SavePowerAssignmentAsync,
            () => Volatile.Read(ref _resumeRestore));
        _pluginSettings = new PluginSettingsCoordinator(_store);
        _diagnostics = new DeviceCoordinatorDiagnosticsServer(sessionId, DiagnosticsSnapshot);
        _hapticSink = new PluginHapticSink(ApplyHapticOutputAsync);
        Controllers = ControllerManager.CreateProduction(_store.Context.Root, _hapticSink);
        Controllers.TargetLost += OnControllerTargetLost;
        _powerAssignmentTask = ObservePowerAssignmentsAsync();
        try
        {
            // A Windows power mode picked outside WSGM is adopted like an out-of-band watt change.
            _powerModeNotification = EffectivePowerModeNotification.Register(RequestPowerAssignmentReconcile);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Windows power mode notifications are unavailable: {ex.Message}");
        }
    }

    /// <summary>The stable key device values are stored under, or null before the machine is identified.</summary>
    internal string? DeviceIdentityKey => _identity is null ? null : DeviceMachineIdentity.StableKey(_identity);

    /// <summary>The profile owner every per-game value is read from and written to.</summary>
    internal ProfileService Profiles { get; }

    /// <summary>Current process-long lifecycle state.</summary>
    public DeviceCycleState State { get; private set; } = DeviceCycleState.Disabled;

    /// <summary>Whether the persisted master switch currently exposes the Device surface.</summary>
    internal bool IntegrationEnabled => _config.DeviceIntegration.Enabled;

    /// <summary>The sole installed package, including its validation result.</summary>
    internal InstalledDevicePackage? InstalledPackage => PackageDiscovery.InstalledPackage;

    /// <summary>The device definition matched by the active plugin cycle.</summary>
    private string? ActiveDeviceDefinitionId { get; set; }

    /// <summary>The latest device package discovery result.</summary>
    internal DevicePackageDiscovery PackageDiscovery { get; private set; } = new()
    {
        Inventory = new DevicePackageInventory { PackageFiles = [] }
    };

    /// <summary>The capability router, for snapshots and change subscriptions.</summary>
    /// <remarks>
    ///     Reads and events only. Writes go through <see cref="ExecuteCapabilityAsync" />, which is the
    ///     one path that lets a manual power change pause AutoTDP.
    /// </remarks>
    internal DeviceCapabilityRouter Capabilities { get; }

    internal DevicePowerPresets PowerPresets { get; }

    internal DevicePowerAssignments PowerAssignments { get; }

    internal (bool Available, bool Unified) ManualTdpMode =>
        (IntegrationEnabled && Capabilities.Snapshot().Any(view =>
                view.Descriptor is { Role: CapabilityRole.PowerSustainedLimit, PairedPowerLimitId: not null }),
            ManualTdpUnified);

    /// <summary>
    ///     Whether manual TDP is unified for the running application. Unlike
    ///     <see cref="ManualTdpMode" />, this builds no capability snapshot.
    /// </summary>
    internal bool ManualTdpUnified
    {
        get { return Profiles.Current.Layers.Value(values => values.TdpUnified).Value == true; }
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

    /// <summary>Whether AutoTDP is switched on in the persisted configuration.</summary>
    internal bool AutoTdpEnabled => _config.DeviceIntegration.AutoTdpEnabled;

    /// <summary>Whether controller management may run in this configuration.</summary>
    internal bool ControllerManagementEnabled =>
        _config.DeviceIntegration is { ControllerManagementEnabled: true, Enabled: true };

    /// <summary>The catalog holding the installed package's glyph profiles.</summary>
    /// <remarks>
    ///     Exposed so one <c>PhysicalGlyphService</c> can be built over it and share its invalidation.
    ///     The catalog is immutable data plus a change event; handing it out does not let a consumer
    ///     load, replace or reach past a profile.
    /// </remarks>
    internal PhysicalGlyphCatalog PhysicalGlyphCatalog { get; } = new();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ShutdownAsync(
            PluginStopReason.WsgmExiting,
            NormalShutdownDeadline());
    }

    private Task ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        var client = _client;
        return client is null
            ? Task.CompletedTask
            : client.ApplyHapticOutputAsync(frame, cancellationToken);
    }

    /// <summary>Raised after the authoritative lifecycle state changes.</summary>
    public event Action<DeviceCycleState>? StateChanged;

    /// <summary>Raised when settings change overlay visibility or desired presentation.</summary>
    /// <remarks>
    ///     Glyph-profile selection also changes with configuration; consumers of the active profile
    ///     subscribe to this and to <see cref="PhysicalGlyphCatalog" />'s change event.
    /// </remarks>
    internal event Action? ConfigurationChanged;

    internal async Task SetManualTdpModeAsync(bool unified)
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
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
    internal static bool? ReadOnAcPower()
    {
        return WindowsPower.TryGetStatus(out var power) && power.ACLineStatus is 0 or 1
            ? power.ACLineStatus == 1
            : null;
    }

    private async Task<CapabilityCommandResult> ExecutePresetCapabilityAsync(
        string id, CapabilityValue value, long cycle, long generation, bool persist, CancellationToken token)
    {
        var result = await ExecuteCapabilityCoreAsync(id, null, value, TimeSpan.FromSeconds(5),
            persist && value.Kind == CapabilityValueKind.Integer
                ? CapabilityCommandOrigin.User
                : CapabilityCommandOrigin.AutomaticControl,
            cycle, generation, false, token).ConfigureAwait(false);
        if (!persist && value.IntegerValue is { } watts && result.Outcome.IsApplied()
            && FindDescriptor(id, null)?.Role == CapabilityRole.PowerSustainedLimit)
        {
            _assignedPowerOverride?.Invoke(watts);
        }

        return result;
    }

    private async Task SavePowerAssignmentAsync(DevicePowerAssignmentContext selection, bool ac,
        DevicePowerPresetReference? reference)
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (Profiles.Current.Generation != selection.Profiles.Generation
                || InstalledPackage?.Manifest?.Id != selection.PluginId
                || IntegrationEnabled != selection.Enabled
                || Interlocked.Read(ref _cycleGeneration) != selection.Cycle
                || selection.OnAc != ReadOnAcPower())
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
    /// <param name="profiles">The profile owner every per-game value is read from and written to.</param>
    /// <param name="cancellationToken">Cancels admission before the coordinator is created.</param>
    /// <returns>The coordinator, or null when the process-wide device owner is already reserved.</returns>
    internal static Task<DeviceCoordinator?> TryStartAsync(
        AppConfig config, ConfigStore store, ProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
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
                UiThread.Post, profiles);
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        if (config.DeviceIntegration.Enabled)
        {
            coordinator.Observe(coordinator.StartCycleAsync(coordinator._lifetime.Token), "initial start");
        }
        else
        {
            coordinator.Observe(coordinator.Controllers.RecoverPhysicalControllerAsync(
                    "integration disabled after an interrupted device cycle", coordinator._lifetime.Token),
                "controller recovery");
            Log.Info(
                $"Device cycle: coordinator ready for session {coordinator._sessionId}; integration disabled.");
        }

        return Task.FromResult<DeviceCoordinator?>(coordinator);
    }

    /// <summary>
    ///     Creates one handle-owned machine marker. It is deliberately never mutex-owned, so
    ///     coordinator disposal may close it from any continuation thread.
    /// </summary>
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
    public async Task ApplyConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousConfig = _config;
            var wasEnabled = _config.DeviceIntegration.Enabled;
            var controllerWasEnabled = _config.DeviceIntegration.ControllerManagementEnabled;
            _config = config;
            var controllerIsEnabled = config.DeviceIntegration.ControllerManagementEnabled;
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
                        RestoreConfigAfterCanceledStart(previousConfig);
                        throw;
                    }

                    return;
                case true when !config.DeviceIntegration.Enabled:
                {
                    var teardown = await StopCycleUnderGateAsync(
                        PluginStopReason.IntegrationDisabled,
                        NormalShutdownDeadline(),
                        cancellationToken).ConfigureAwait(false);
                    PhysicalGlyphCatalog.ReplacePackageProfiles([]);
                    ReportDeviceTeardown(teardown, cancellationToken);
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
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
    ///     faulted missing cycle; a failure during the resume itself restarts for either trigger.
    /// </param>
    /// <param name="cancellationToken">Cancels the resume.</param>
    public async Task ResumeAsync(bool afterSystemSleep, CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            _identity = DeviceMachineIdentity.Collect();
            var deadline = Deadline.After(TimeSpan.FromSeconds(5));
            var previousGeneration = Interlocked.Read(ref _cycleGeneration);
            var requestedGeneration = Interlocked.Increment(ref _cycleGeneration);
            var resumed = await RunResumeOrRestartAsync(
                () => client!.ResumeAsync(requestedGeneration, deadline, cancellationToken),
                () => SynchronizeGenerationAfterLifecycleCall(client!, previousGeneration),
                failure =>
                {
                    Log.Warn($"Device resume failed after {(afterSystemSleep ? "a sleep" : "session unlock")} "
                             + $"({failure.Message}); starting a fresh cycle.");
                    return RestartCycleUnderGateAsync(true, cancellationToken);
                }).ConfigureAwait(false);
            if (!resumed)
            {
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

    internal static async Task<bool> RunResumeOrRestartAsync(
        Func<Task> resumeAsync,
        Action synchronizeGeneration,
        Func<Exception, Task> restartAsync)
    {
        Exception? failure = null;
        try
        {
            await resumeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            failure = ex;
        }
        finally
        {
            synchronizeGeneration();
        }

        if (failure is null)
        {
            return true;
        }

        await restartAsync(failure).ConfigureAwait(false);
        return false;
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
            // Wake and failed-resume recovery attempt all cleanup, then replace the cycle. A completed
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
    public async Task<bool> RetryAfterFaultAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    /// <summary>Stops the device cycle under the process exit path's single outer deadline.</summary>
    internal ValueTask ShutdownAsync(PluginStopReason reason, Deadline deadline)
    {
        Task shutdown;
        lock (_backgroundGate)
        {
            if (_shutdownTask is null)
            {
                _disposed = true;
                Capabilities.CloseCommandAdmission();
                _oemActions.Dispose();
                _pluginSettings.Dispose();
                Log.Observe(_lifetime.CancelAsync(), "Device lifetime cancellation", true);
                _shutdownTask = Task.Run(() => ShutdownCoreAsync(reason, deadline));
                Log.Observe(_shutdownTask, "Device shutdown", true);
            }

            shutdown = _shutdownTask;
        }

        return new ValueTask(WaitForShutdownAsync(shutdown, deadline));
    }

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

    private async Task ShutdownCoreAsync(PluginStopReason reason, Deadline deadline)
    {
        using var bounded = deadline.CreateCancellationSource();
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
            background = [.. _backgroundTasks, _powerAssignmentTask, _oemActions.Completion, _pluginSettings.Completion];
        }

        var backgroundStopped = await FinishStepAsync("background work", Task.WhenAll(background)).ConfigureAwait(false);
        if (!cycleStopped || !controllersStopped || !backgroundStopped || bounded.IsCancellationRequested)
        {
            Log.Warn("Device cleanup retained its remaining owners because work is still active or unverified.");
            return;
        }

        CleanupProvider("power notifications", () => _powerModeNotification?.Dispose());
        var diagnosticsStopped = await FinishStepAsync("diagnostics", _diagnostics.DisposeAsync().AsTask()).ConfigureAwait(false);
        var capabilitiesStopped = await FinishStepAsync("capabilities", Capabilities.DisposeAsync().AsTask()).ConfigureAwait(false);
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
                var teardown = await StopCycleUnderGateAsync(reason, deadline, CancellationToken.None).ConfigureAwait(false);
                foreach (var failure in teardown.Failures)
                {
                    Log.Warn($"Device teardown step was unverified: {failure.Message}");
                }

                return teardown.Verified;
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

        var retryState = State;
        using var startLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await RunCancellationSafeStartAsync(
            StartCycleCoreUnderGateAsync,
            CleanupCanceledStartAsync,
            () => SetState(retryState),
            startLifetime.Token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs one start attempt while guaranteeing that linked cancellation applies its
    ///     ownership policy, restores the state from which the attempt may be retried, and is rethrown.
    /// </summary>
    internal static async Task RunCancellationSafeStartAsync(
        Func<CancellationToken, Task> operation,
        Func<ValueTask> cleanup,
        Action restoreRetryState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(restoreRetryState);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Device cycle cancellation cleanup failed", ex);
            }

            try
            {
                restoreRetryState();
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
        InstalledDevicePackage package;
        long cycleGeneration;
        DevicePluginRuntime client;
        try
        {
            _identity = DeviceMachineIdentity.Collect();
            PackageDiscovery = await DiscoverPackageAsync(cancellationToken)
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
                "The installed plugin packages could not be discovered.",
                ex));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var discoveredPackage = InstalledPackage;
        PhysicalGlyphCatalog.ReplacePackageProfiles([]);
        if (discoveredPackage is null || !discoveredPackage.Valid)
        {
            if (_config.DeviceIntegration.ControllerManagementEnabled)
            {
                await Controllers.RecoverPhysicalControllerAsync("no usable device package", cancellationToken)
                    .ConfigureAwait(false);
            }

            SetState(DeviceCycleState.Passive);
            var refusal = PackageDiscovery.ErrorCode
                          ?? discoveredPackage?.RejectionCode
                          ?? "no-package-installed";
            Log.Warn(
                $"Device cycle passive: {refusal}; devicePackages={PackageDiscovery.Inventory.PackageFiles.Count}.");
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        package = discoveredPackage;

        cycleGeneration = Interlocked.Increment(ref _cycleGeneration);
        SetState(DeviceCycleState.Activating);
        try
        {
            client = await DevicePluginRuntime.StartAsync(
                package,
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
            var controllerManagement = _config.DeviceIntegration.ControllerManagementEnabled;
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
                Log.Info($"Device detection passive: package={package.Manifest?.Id}; runtime retired.");
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
                package.Manifest?.Id ?? string.Empty,
                _config);
            LoadPhysicalGlyphProfiles(package);
            SetState(activation.State);
            _automaticRestartAttempts = 0;
            Log.Info(
                $"Device cycle active: package={package.Manifest?.Id}, "
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

        return new ValueTask(RunCanceledStartCleanupPolicyAsync(
            _lifetime.IsCancellationRequested,
            () => CleanupAbortedStartAsync(PluginStopReason.StartCanceled)));
    }

    /// <summary>
    ///     Preserves a possibly active runtime when shutdown canceled startup, because the
    ///     shutdown owner must perform the bounded handoff. An independent caller cancellation runs
    ///     its own fresh bounded teardown before the runtime can be disposed.
    /// </summary>
    internal static Task RunCanceledStartCleanupPolicyAsync(
        bool lifetimeCancellationRequested,
        Func<Task> callerCleanupAsync)
    {
        ArgumentNullException.ThrowIfNull(callerCleanupAsync);
        return lifetimeCancellationRequested
            ? Task.CompletedTask
            : callerCleanupAsync();
    }

    /// <summary>
    ///     Closes a coordinator lifetime before waiting for its serialized transition. The
    ///     ordering lets cancellation unwind an in-flight start that currently owns the gate.
    /// </summary>
    internal static Task CancelLifetimeAndWaitForTransitionAsync(
        CancellationTokenSource lifetime,
        SemaphoreSlim transitionGate)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(transitionGate);
        lifetime.Cancel();
        return transitionGate.WaitAsync(CancellationToken.None);
    }

    private async Task CleanupAbortedStartAsync(PluginStopReason reason)
    {
        var teardownVerified = false;
        try
        {
            await RunFreshBoundedCleanupAsync(
                CanceledStartCleanupBudget,
                async (deadline, cancellationToken) =>
                {
                    var teardown = await StopCycleUnderGateAsync(
                        reason,
                        deadline,
                        cancellationToken).ConfigureAwait(false);
                    teardownVerified = teardown.Verified;
                    ReportDeviceTeardown(teardown, cancellationToken);
                }).ConfigureAwait(false);
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

    /// <summary>Creates a cleanup budget independent from the already-canceled start caller.</summary>
    internal static async Task RunFreshBoundedCleanupAsync(
        TimeSpan budget,
        Func<Deadline, CancellationToken, Task> cleanupAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        var deadline = Deadline.After(budget);
        using var cleanupCancellation = deadline.CreateCancellationSource();
        await cleanupAsync(deadline, cleanupCancellation.Token).ConfigureAwait(false);
    }

    private async Task ObserveRuntimeCompletionAsync(DevicePluginRuntime client)
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
            var cleanup = await RunClientTeardownAsync(
                token => Controllers.ReleaseAsync(
                    HandoffScope.FullDeactivation,
                    inner => client.ReleaseControllerAsync(
                        HandoffScope.FullDeactivation,
                        cleanupDeadline,
                        inner),
                    cleanupDeadline,
                    token,
                    true),
                token => client.StopAsync(
                    PluginStopReason.RuntimeFault,
                    cleanupDeadline,
                    token),
                () => DetachAsync(client),
                () => DisposeRuntimeAsync(client, cleanupDeadline),
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
                $"Device cycle faulted after restart exhaustion: package={InstalledPackage?.Manifest?.Id}, "
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
        await Task.Delay(backoff, _lifetime.Token).ConfigureAwait(false);
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

        var teardown = await RunClientTeardownWithStateNotificationsAsync(
            Capabilities.CloseCommandAdmission,
            () => SetState(DeviceCycleState.Deactivating),
            TeardownOwnerAsync,
            () => SetState(DeviceCycleState.Disabled)).ConfigureAwait(false);

        return teardown;

        async Task<DeviceClientTeardownResult> TeardownOwnerAsync()
        {
            var result = await RunClientTeardownAsync(
                token => Controllers.ReleaseAsync(
                    HandoffScope.FullDeactivation,
                    inner => client.ReleaseControllerAsync(
                        HandoffScope.FullDeactivation,
                        deadline,
                        inner),
                    deadline,
                    token,
                    // A fault restart takes the controller again at once; every other stop leaves.
                    reason is PluginStopReason.RuntimeFault),
                token => client.StopAsync(
                    reason,
                    deadline,
                    token),
                () => DetachAsync(client),
                () => DisposeRuntimeAsync(client, deadline),
                cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    private async ValueTask DisposeRuntimeAsync(DevicePluginRuntime client, Deadline deadline)
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

    internal static async Task<DeviceClientTeardownResult> RunClientTeardownWithStateNotificationsAsync(
        Action closeCommandAdmission,
        Action setDeactivating,
        Func<Task<DeviceClientTeardownResult>> teardownAsync,
        Action setDisabled)
    {
        ArgumentNullException.ThrowIfNull(closeCommandAdmission);
        ArgumentNullException.ThrowIfNull(setDeactivating);
        ArgumentNullException.ThrowIfNull(teardownAsync);
        ArgumentNullException.ThrowIfNull(setDisabled);
        List<Exception> failures = [];
        try
        {
            closeCommandAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device command admission closure failed; cleanup continues: {ex.Message}");
        }

        try
        {
            setDeactivating();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device deactivation state notification failed; cleanup continues: {ex.Message}");
        }

        try
        {
            var teardown = await teardownAsync().ConfigureAwait(false);
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
                setDisabled();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device disabled-state notification failed after cleanup: {ex.Message}");
            }
        }

        return new DeviceClientTeardownResult([.. failures]);
    }

    /// <summary>
    ///     Attempts controller and plugin cleanup before detaching and disposing the runtime.
    ///     Every non-fatal unverified response or exception is retained while later phases continue.
    /// </summary>
    internal static async Task<DeviceClientTeardownResult> RunClientTeardownAsync(
        Func<CancellationToken, Task> releaseControllerAsync,
        Func<CancellationToken, Task<DevicePluginState>> stopAsync,
        Func<ValueTask> detachAsync,
        Func<ValueTask> disposeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releaseControllerAsync);
        ArgumentNullException.ThrowIfNull(stopAsync);
        ArgumentNullException.ThrowIfNull(detachAsync);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        List<Exception> failures = [];
        try
        {
            try
            {
                await releaseControllerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device controller release unverified; cleanup continues: {ex.Message}");
            }

            try
            {
                var stopped = await stopAsync(cancellationToken)
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
                await detachAsync().ConfigureAwait(false);
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
                    await disposeAsync().ConfigureAwait(false);
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
    /// <remarks>
    ///     HC's <c>Close</c> writes the device back and ignores the result. Treating an unverified release
    ///     as a failure blocked restarts on devices that cannot read their state back.
    /// </remarks>
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
            await Controllers.ReleaseAsync(
                HandoffScope.ControllerOnly,
                token => client.ReleaseControllerAsync(
                    HandoffScope.ControllerOnly,
                    deadline,
                    token),
                deadline,
                cancellationToken).ConfigureAwait(false);
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
        DevicePluginRuntime client,
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

    private void OnControllerTargetLost(string detail)
    {
        var client = _client;
        var generation = Interlocked.Read(ref _cycleGeneration);
        Observe(Task.Run(async () =>
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

                var cleanupNeeded = true;
                try
                {
                    await CancelControllerStartAsync().WaitAsync(budget.Token).ConfigureAwait(false);
                    if (Controllers.State is not ControllerManagementState.Faulted)
                    {
                        cleanupNeeded = false;
                        return;
                    }

                    var releaseDeadline = Deadline.After(TimeSpan.FromSeconds(6));
                    await Controllers.ReleaseAsync(HandoffScope.ControllerOnly,
                        token => client.ReleaseControllerAsync(HandoffScope.ControllerOnly,
                            releaseDeadline, token), releaseDeadline, budget.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (cleanupNeeded)
                    {
                        using var cleanup = Deadline.After(TimeSpan.FromSeconds(6)).CreateCancellationSource();
                        try
                        {
                            await Controllers.ShowPhysicalControllerAsync("virtual target lost", cleanup.Token)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            Controllers.ReportTargetFault(detail);
                        }
                    }
                }
            }
            finally
            {
                _transitionGate.Release();
            }
        }), "Controller target-loss recovery");
    }

    private void Attach(DevicePluginRuntime client)
    {
        client.LifecycleStateReceived += OnLifecycleState;
        client.PhysicalIdentitiesReceived += OnPhysicalIdentities;
        client.ControllerSampleReceived += Controllers.Submit;
    }

    private async ValueTask DetachAsync(DevicePluginRuntime client)
    {
        client.LifecycleStateReceived -= OnLifecycleState;
        client.PhysicalIdentitiesReceived -= OnPhysicalIdentities;
        client.ControllerSampleReceived -= Controllers.Submit;
        // The plugin no longer owns the controller: no frame goes to it from here on.
        _hapticSink.Withdraw();
        Capabilities.Detach();
        _pluginSettings.Detach();
        _oemActions.Detach();
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

    /// <summary>Applies a running-application change from the one shared monitor.</summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>A task completing after the controller target is reconciled.</returns>
    internal async Task ApplyRunningApplicationAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _runningApplicationId = snapshot.ApplicationId;
        _runningExecutable = snapshot.RtssProfileName;
        await Controllers.ApplyRunningApplicationAsync(snapshot, cancellationToken)
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
        return ControllerSelection.From(_config.DeviceIntegration, Profiles.Current.Config);
    }

    /// <summary>The target chosen for the running application, whether or not one is live.</summary>
    /// <remarks>
    ///     Selectors show this rather than the live target. With no live target, after a fault or before
    ///     the cycle is up, showing the live one left the selector blank, and a choice made there
    ///     snapped back to blank because nothing came up to report it (Xbox Ally X, 2026-09-28).
    /// </remarks>
    internal ManagedControllerTarget ChosenControllerTarget()
    {
        return ControllerTargetSelection.Resolve(
            Profiles.Current.Config,
            _runningApplicationId,
            _runningExecutable).Target;
    }

    /// <summary>Resolves the current persisted mode against only the active package's safe profiles.</summary>
    internal PhysicalGlyphSelectionResult PhysicalGlyphSelectionSnapshot()
    {
        return PhysicalGlyphCatalog.SelectProfile(
            _config.DeviceIntegration.Enabled,
            _config.DeviceIntegration.GlyphSelection,
            _config.DeviceIntegration.ManualGlyphProfileId);
    }

    internal PhysicalGlyphSelectionResult PhysicalControlSelectionSnapshot()
    {
        return PhysicalGlyphCatalog.SelectProfile(_config.DeviceIntegration.Enabled, DeviceGlyphSelection.Automatic,
            null);
    }

    /// <summary>Sets the physical presentation policy without changing device ownership.</summary>
    internal async Task SetPhysicalGlyphSelectionAsync(DeviceGlyphSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(selection))
        {
            throw new ArgumentOutOfRangeException(nameof(selection));
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    /// <summary>
    ///     Attaches the hook that pauses AutoTDP after a user-originated power-limit write.
    /// </summary>
    /// <param name="note">Receives the accepted wattage, or null when AutoTDP is not running.</param>
    /// <param name="assigned">Pauses AutoTDP for an applied assignment without persisting its wattage.</param>
    /// <remarks>
    ///     Attached here because this is the one path every surface's power write already goes through:
    ///     the overlay row and the native-QAM TDP control both call <see cref="ExecuteCapabilityAsync" />,
    ///     so this is the one place that sees every manual change.
    /// </remarks>
    internal void AttachAutoTdpManualOverride(Action<int>? note, Action<int>? assigned = null)
    {
        _autoTdpManualOverride = note;
        _assignedPowerOverride = assigned;
    }

    /// <summary>
    ///     Attaches the hook that saves a user-originated variable-refresh write to the performance
    ///     profile.
    /// </summary>
    /// <param name="note">Receives the accepted state, or null when no profile owner exists.</param>
    /// <remarks>
    ///     Attached for the same reason as the power-limit hook: the overlay's Device row and Steam's
    ///     own variable-refresh control both reach the device through
    ///     <see cref="ExecuteCapabilityAsync" />, so this is the one place that sees every manual change.
    /// </remarks>
    internal void AttachManualVariableRefreshOverride(Action<bool>? note)
    {
        _manualVariableRefreshOverride = note;
    }

    internal void AttachAutoTdpAvailability(Func<AutoTdpAvailability>? read)
    {
        _autoTdpAvailability = read;
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
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var availability = enabled ? _autoTdpAvailability?.Invoke() : null;
            if (enabled && availability is not { Available: true })
            {
                throw new InvalidOperationException(
                    availability?.Detail ?? "AutoTDP is unavailable.");
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
    internal Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken = default)
    {
        return Controllers.ClaimUiAsync(surfaceId, cancellationToken);
    }

    /// <summary>Releases one visible WSGM surface's managed controller claim.</summary>
    internal void ReleaseUi(string surfaceId)
    {
        Controllers.ReleaseUi(surfaceId);
    }

    /// <summary>Sends a bounded rear-button pulse through the managed virtual target.</summary>
    internal Task<bool> PulseRearButtonAsync(
        int button,
        CancellationToken cancellationToken = default)
    {
        return Controllers.PulseRearButtonAsync(button, cancellationToken);
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
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Profiles.SetAsync(values => values.ControllerTarget = target, $"ControllerTarget={target}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
            () => _store.Update(updatedConfig => { mutate(updatedConfig); return true; }),
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
        var power = FindDescriptor(capabilityId, instanceId)?.Role is
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit or CapabilityRole.ScenarioMode;
        if (origin == CapabilityCommandOrigin.User
            && FindDescriptor(capabilityId, instanceId)?.Role == CapabilityRole.PowerSustainedLimit)
        {
            applyPowerPair |= Profiles.Current.Layers.ManualTdp()?.Unified == true;
        }

        if (power)
        {
            await PowerPresets.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                PowerPresets.MutationGate.Release();
            }
        }
    }

    internal async Task<bool> RestoreSplitPowerAsync(DeviceCapabilityView primary, int sustained, int boost,
        CancellationToken cancellationToken)
    {
        var peerId = primary.Descriptor.PairedPowerLimitId;
        if (peerId is null)
        {
            return false;
        }

        await PowerPresets.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var peer = FindCapability(peerId, null);
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
            if (!pair.Applied(sustained))
            {
                return false;
            }

            _assignedPowerOverride?.Invoke(sustained);
            var result = await ExecuteCapabilityCoreAsync(peerId, null,
                new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = boost },
                TimeSpan.FromSeconds(5), CapabilityCommandOrigin.ProfileRestore,
                state.CycleGeneration, state.DescriptorGeneration, false, cancellationToken).ConfigureAwait(false);
            return result.Applied(boost);
        }
        finally
        {
            PowerPresets.MutationGate.Release();
        }
    }

    private async Task<CapabilityCommandResult> ExecuteCapabilityCoreAsync(
        string capabilityId, string? instanceId, CapabilityValue? value, TimeSpan timeout,
        CapabilityCommandOrigin origin, long? expectedCycle, long? expectedDescriptors, bool applyPowerPair,
        CancellationToken cancellationToken)
    {
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
                            expectedCycle, expectedDescriptors, applyPowerPair, cancellationToken).ConfigureAwait(false);
                        NotifyManualPowerChange(capabilityId, instanceId, value, written);
                        return written;
                    }, _manualVariableRefreshOverride, cancellationToken).ConfigureAwait(false);
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
                    _assignedPowerOverride?.Invoke(watts);
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

    private void NotifyManualPowerChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (_autoTdpManualOverride is not { } note
            || value?.IntegerValue is not { } watts
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
                _assignedPowerOverride?.Invoke(sustained);
            }

            return;
        }

        // Permanent until the user resumes control, by specification: quietly taking the limit back
        // a few seconds after they set it by hand would make the manual control look broken.
        Log.Info($"AutoTDP paused: the sustained power limit was set to {watts} W by hand.");
        note(watts);
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
        if (_manualVariableRefreshOverride is not { } note
            || value?.BooleanValue is not { } enabled
            || !result.Outcome.IsApplied()
            || FindDescriptor(capabilityId, instanceId)?.Role
                is not CapabilityRole.VariableRefreshRate)
        {
            return;
        }

        note(enabled);
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
    internal void ConfigureOemActions(DeviceOemActionServices actions)
    {
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
            .Where(profile => profile.CapabilityId == DeviceAuthoredProfileCapabilities.FanCurve)
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
    internal async Task SelectAuthoredProfileAsync(string? next, CancellationToken cancellationToken = default)
    {
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
                DeviceAuthoredProfileCapabilities.FanCurve,
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
        var plugin = InstalledPackage?.Manifest?.Id;
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
        Capabilities.UpdateDesiredContext(DeviceIdentityKey, Profiles.Current.Layers, ReadOnAcPower() ?? true);
    }

    private void UpdateOemConfiguration()
    {
        _oemActions.UpdateConfiguration(
            _config.DeviceIntegration.OemAssignments,
            _config.DeviceIntegration.ControllerManagementEnabled,
            ControllerTargetSelection.Resolve(Profiles.Current.Config, _runningApplicationId,
                _runningExecutable).Target);
    }

    private void LoadPhysicalGlyphProfiles(InstalledDevicePackage package)
    {
        try
        {
            GlyphPackageImportResult imported;
            using (var file = PluginPackageFile.Open(package.PackagePath))
            {
                imported = GlyphPackageImporter.Import(file);
            }

            PhysicalGlyphCatalog.ReplacePackageProfiles(imported.Profiles);
            foreach (var error in imported.Errors)
            {
                Log.Warn(
                    $"Device glyph profile rejected: profile={error.ProfileId}, code={error.Code}, "
                    + $"path={error.Path}, detail={error.Message}");
            }

            Log.Info(
                $"Device glyph catalog: package={package.Manifest?.Id}, "
                + $"profiles={imported.Profiles.Count}, rejected={imported.Errors.Count}.");
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            PhysicalGlyphCatalog.ReplacePackageProfiles([]);
            Log.Warn($"Device glyph catalog unavailable: {exception.Message}");
        }
    }

    private static Task<DevicePackageDiscovery> DiscoverPackageAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => PluginPackageCatalog.DiscoverInstalled().Device,
            cancellationToken);
    }

    private DeviceCoordinatorDiagnosticsSnapshot DiagnosticsSnapshot()
    {
        var capabilities = Capabilities.Snapshot();
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = State,
            InstalledPackage = InstalledPackage?.Manifest is { } manifest
                ? new DeviceInstalledPackageDiagnostic(manifest.Id, manifest.Version)
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

    private void OnLifecycleState(DevicePluginState state)
    {
        if (state.CycleGeneration != _cycleGeneration)
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
        State = state;
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
internal sealed record DeviceClientTeardownResult(IReadOnlyList<Exception> Failures)
{
    internal static DeviceClientTeardownResult Clean { get; } = new([]);

    internal bool Verified => Failures.Count == 0;

    internal Exception ToException()
    {
        return Failures.Combine("Multiple device teardown steps were unverified.");
    }
}
