using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Who asked for a capability command.</summary>
/// <remarks>
/// The only thing this decides is whether AutoTDP steps aside. A power limit the user moved is an
/// instruction; the one AutoTDP wrote itself is the controller's own output, and treating it as a
/// manual override would pause the feature on its first tick.
/// </remarks>
internal enum CapabilityCommandOrigin
{
    /// <summary>A person moved this control on a WSGM surface.</summary>
    User,

    /// <summary>An automatic controller inside WSGM wrote it.</summary>
    AutomaticControl,
}

/// <summary>Authoritative process-long owner of the machine-wide hardware cycle.</summary>
public sealed class DeviceCoordinator : IAsyncDisposable
{
    internal const string ProductionOwnerName = @"Global\WSGM.DeviceOwner";
    private static readonly TimeSpan CanceledStartCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly uint _sessionId;
    private readonly Mutex _ownerMutex;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly PluginSettingsCoordinator _pluginSettings;
    private readonly DeviceOemActionRouter _oemActions = new();
    private readonly DeviceCoordinatorDiagnosticsServer _diagnostics;
    private readonly DeviceProfileStore _profiles = new();
    private readonly PhysicalGlyphCatalog _physicalGlyphs = new();
    private readonly DeviceTeardownFailureTracker _teardownFailures = new();
    private readonly DeviceHostHapticSink _hapticSink;
    private readonly ControllerManager _controllers;
    private DevicePackageDiscovery _packageDiscovery = new()
    {
        Inventory = new DevicePackageInventory { PackageRoots = [] },
    };
    private AppConfig _config;
    private DeviceIdentitySnapshot? _identity;
    private string? _deviceDefinitionId;
    private DeviceHostClient? _client;
    private long _cycleGeneration;
    private string? _runningApplicationId;
    private Func<AutoTdpStatus>? _autoTdpStatus;
    private Action<int>? _autoTdpManualOverride;
    private bool _intentionalStop;
    private bool _faultRecoveryPending;
    private int _automaticRestartAttempts;
    private bool _disposed;

    private DeviceCoordinator(
        AppConfig config,
        uint sessionId,
        Mutex ownerMutex,
        Action<Action> postToUi)
    {
        _config = config;
        _sessionId = sessionId;
        _ownerMutex = ownerMutex;
        _capabilities = new DeviceCapabilityRouter(0, postToUi);
        _pluginSettings = new PluginSettingsCoordinator();
        _diagnostics = new DeviceCoordinatorDiagnosticsServer(sessionId, DiagnosticsSnapshot);
        _hapticSink = new DeviceHostHapticSink(ApplyHapticOutputAsync);
        _controllers = new ControllerManager(
            new ViiperControllerBackend(),
            _hapticSink,
            new HidHideOwnedDeltaManager(
                new WindowsHidHideAdapter(),
                new FileHidHideOwnershipStore(
                    Path.Combine(Log.Directory, "hidhide-ownership.json"))),
            Path.Combine(DeviceInstallationPaths.DeviceHostRoot, "WSGM.DeviceHost.exe"));
    }

    private Task ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        DeviceHostClient? client = _client;
        return client is null
            ? Task.CompletedTask
            : client.ApplyHapticOutputAsync(frame, cancellationToken);
    }

    /// <summary>Current process-long lifecycle state.</summary>
    public DeviceCycleState State { get; private set; } = DeviceCycleState.Disabled;

    /// <summary>Whether the persisted master switch currently exposes the Device surface.</summary>
    internal bool IntegrationEnabled => _config.DeviceIntegration.Enabled;

    /// <summary>When a faulted package may be retried manually.</summary>
    internal DateTimeOffset? ManualRetryAvailableAt => State is DeviceCycleState.Faulted
        ? DateTimeOffset.MinValue
        : null;

    /// <summary>The sole installed package, including its validation result.</summary>
    internal InstalledDevicePackage? InstalledPackage => _packageDiscovery.InstalledPackage;

    /// <summary>The latest one-slot discovery result.</summary>
    internal DevicePackageDiscovery PackageDiscovery => _packageDiscovery;

    /// <summary>Raised after the authoritative lifecycle state changes.</summary>
    public event Action<DeviceCycleState>? StateChanged;

    /// <summary>Raised with a complete semantic capability projection on the UI dispatcher.</summary>
    internal event Action<IReadOnlyList<DeviceCapabilityView>>? CapabilityViewsChanged
    {
        add => _capabilities.Changed += value;
        remove => _capabilities.Changed -= value;
    }

    /// <summary>Raised when settings change overlay visibility or desired presentation.</summary>
    internal event Action? ConfigurationChanged;

    /// <summary>Raised when the installed package's glyph profiles are replaced.</summary>
    /// <remarks>
    /// Selection also changes with configuration, which <see cref="ConfigurationChanged"/> already
    /// reports; consumers of the active profile subscribe to both.
    /// </remarks>
    internal event Action? PhysicalGlyphProfilesChanged
    {
        add => _physicalGlyphs.Changed += value;
        remove => _physicalGlyphs.Changed -= value;
    }

    /// <summary>
    /// Creates the one coordinator allowed to own hardware on this machine without blocking the UI.
    /// </summary>
    /// <param name="config">Initial normalized application configuration.</param>
    /// <param name="cancellationToken">Cancels admission before the coordinator is created.</param>
    /// <returns>The coordinator, or null when ownership is reserved or DeviceHost absence is unverified.</returns>
    public static async Task<DeviceCoordinator?> TryStartAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        Mutex? owner = await TryReserveOwnerForStartAsync(
            ProductionOwnerName,
            static token => DeviceHostProcess.IsAnyRunningAsync(token),
            cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            Log.Warn(
                "Device cycle: safe machine-wide admission could not be established; no host started.");
            return null;
        }

        DeviceCoordinator coordinator;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint sessionId = (uint)Process.GetCurrentProcess().SessionId;
            coordinator = new DeviceCoordinator(
                config,
                sessionId,
                owner,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
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
            Log.Info(
                $"Device cycle: coordinator ready for session {coordinator._sessionId}; integration disabled.");
        }

        return coordinator;
    }

    /// <summary>Reserves the machine marker and admits startup only after a global DeviceHost
    /// snapshot proves that no earlier host remains alive.</summary>
    internal static async Task<Mutex?> TryReserveOwnerForStartAsync(
        string name,
        Func<CancellationToken, Task<bool?>> inspectDeviceHostAsync,
        CancellationToken cancellationToken = default,
        Func<string, (Mutex Owner, bool CreatedNew)>? create = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inspectDeviceHostAsync);
        cancellationToken.ThrowIfCancellationRequested();
        Mutex? owner = TryCreateOwnerMutex(name, create);
        if (owner is null)
        {
            return null;
        }

        try
        {
            bool? running = await inspectDeviceHostAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (running is false)
            {
                return owner;
            }

            Log.Warn(running is true
                ? "Device cycle: a DeviceHost process is already running after ownership was reserved; no host started."
                : "Device cycle: DeviceHost process state could not be verified after ownership was reserved; no host started.");
            owner.Dispose();
            return null;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>Creates one handle-owned machine marker. It is deliberately never mutex-owned, so
    /// coordinator disposal may close it from any continuation thread.</summary>
    internal static Mutex? TryCreateOwnerMutex(
        string name,
        Func<string, (Mutex Owner, bool CreatedNew)>? create = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            Func<string, (Mutex Owner, bool CreatedNew)> factory = create ?? CreateOwnerMutex;
            (Mutex owner, bool createdNew) = factory(name);
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

    /// <summary>Retains an existing or newly created unowned owner marker for process lifetime.
    /// Installer rollback uses this to take a second handle before setup closes its reservation;
    /// it never waits on or releases the mutex, so the handoff has no thread affinity.</summary>
    internal static Mutex? TryRetainOwnerMutex(
        string name,
        Func<string, (Mutex Owner, bool CreatedNew)>? create = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            Func<string, (Mutex Owner, bool CreatedNew)> factory = create ?? CreateOwnerMutex;
            var (owner, _) = factory(name);
            return owner;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or WaitHandleCannotBeOpenedException)
        {
            Log.Warn($"Device cycle: owner marker '{name}' could not be retained: {ex.Message}");
            return null;
        }
    }

    private static (Mutex Owner, bool CreatedNew) CreateOwnerMutex(string name)
    {
        var owner = new Mutex(initiallyOwned: false, name, out bool createdNew);
        return (owner, createdNew);
    }

    /// <summary>Applies a saved ownership configuration to this authoritative process.</summary>
    public async Task ApplyConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppConfig previousConfig = _config;
            bool wasEnabled = _config.DeviceIntegration.Enabled;
            bool controllerWasEnabled = EffectiveControllerManagement(_config);
            _config = config;
            bool controllerIsEnabled = EffectiveControllerManagement(config);
            if (config.DeviceIntegration.ControllerManagementEnabled && !controllerIsEnabled)
            {
                Log.Warn(DeviceFeatureAvailability.ControllerManagementDetail);
            }
            ConfigurationChanged?.Invoke();

            // Stored settings live in the configuration, so a reload can change what the plugin
            // should be running with even though the plugin itself never changed.
            _pluginSettings.ApplyConfig(config);
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
            await _controllers.ApplySelectionAsync(
                ControllerSelection.From(config.DeviceIntegration),
                _runningApplicationId,
                cancellationToken).ConfigureAwait(false);
            if (!wasEnabled && config.DeviceIntegration.Enabled)
            {
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
            }

            if (wasEnabled && !config.DeviceIntegration.Enabled)
            {
                DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                    DeviceStopReason.IntegrationDisabled,
                    NormalShutdownDeadline(),
                    cancellationToken).ConfigureAwait(false);
                _physicalGlyphs.ReplacePackageProfiles([]);
                ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
                return;
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
            if (_client is null)
            {
                return;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            DeviceLifecycleNotification state = await _client.SuspendAsync(deadline, cancellationToken)
                .ConfigureAwait(false);
            _oemActions.Reset();
            SetState(state.State);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Revalidates and resumes into a fresh device generation.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                return;
            }

            _identity = DeviceMachineIdentity.Collect();
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            DeviceLifecycleNotification state = await _client.ResumeAsync(
                Interlocked.Increment(ref _cycleGeneration),
                deadline,
                cancellationToken).ConfigureAwait(false);
            _capabilities.MarkCycleGenerationChanged(_cycleGeneration);
            UpdateCapabilityDesiredContext();
            _oemActions.Reset(_cycleGeneration);
            SetState(state.State);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Starts one user-requested attempt after automatic recovery was exhausted.</summary>
    public async Task<bool> RetryAfterFaultAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not DeviceCycleState.Faulted)
            {
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

    /// <summary>Stops the cycle under one caller-owned full-deactivation deadline.</summary>
    public async Task StopAsync(
        DeviceStopReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                reason,
                deadline,
                cancellationToken).ConfigureAwait(false);
            ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ShutdownAsync(
        DeviceStopReason.WsgmExiting,
        NormalShutdownDeadline());

    /// <summary>Stops the device cycle under the process exit path's single outer deadline.</summary>
    internal async ValueTask ShutdownAsync(
        DeviceStopReason reason,
        DateTimeOffset deadline)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception> shutdownFailures = [];
        try
        {
            await CancelLifetimeAndWaitForTransitionAsync(_lifetime, _transitionGate)
                .ConfigureAwait(false);
            try
            {
                DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                    reason,
                    deadline,
                    CancellationToken.None).ConfigureAwait(false);
                ThrowIfDeviceTeardownIncomplete(teardown, CancellationToken.None);
            }
            finally
            {
                shutdownFailures.AddRange(_teardownFailures.Drain());
                _transitionGate.Release();
            }
        }
        catch (Exception ex)
        {
            shutdownFailures.Add(ex);
            Log.Warn($"Device cycle shutdown was unverified: {ex.Message}");
        }

        Task[] background;
        lock (_backgroundGate)
        {
            background = _backgroundTasks.ToArray();
        }
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "background task completion",
            () => new ValueTask(Task.WhenAll(background))).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "diagnostics disposal",
            _diagnostics.DisposeAsync).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "profile disposal",
            _profiles.DisposeAsync).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "capability disposal",
            _capabilities.DisposeAsync).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "controller management disposal",
            _controllers.DisposeAsync).ConfigureAwait(false);
        RetainDeviceShutdownFailure(shutdownFailures, "OEM action disposal", _oemActions.Dispose);
        RetainDeviceShutdownFailure(
            shutdownFailures,
            "plugin settings disposal",
            _pluginSettings.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "glyph disposal", _physicalGlyphs.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "lifetime disposal", _lifetime.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "transition gate disposal", _transitionGate.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "owner marker disposal", _ownerMutex.Dispose);
        if (shutdownFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "Device cycle shutdown completed teardown, but hardware release was unverified.",
                shutdownFailures.Count == 1
                    ? shutdownFailures[0]
                    : new AggregateException(shutdownFailures));
        }
    }

    private static async ValueTask RetainDeviceShutdownFailureAsync(
        List<Exception> failures,
        string operation,
        Func<ValueTask> cleanupAsync)
    {
        try
        {
            await cleanupAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device cycle {operation} was incomplete: {ex.Message}");
        }
    }

    private static void RetainDeviceShutdownFailure(
        List<Exception> failures,
        string operation,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device cycle {operation} was incomplete: {ex.Message}");
        }
    }

    /// <summary>Closes a coordinator lifetime before waiting for its serialized transition. The
    /// ordering lets cancellation unwind an in-flight start that currently owns the gate.</summary>
    internal static Task CancelLifetimeAndWaitForTransitionAsync(
        CancellationTokenSource lifetime,
        SemaphoreSlim transitionGate)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(transitionGate);
        lifetime.Cancel();
        return transitionGate.WaitAsync(CancellationToken.None);
    }

    private Task StartCycleAsync(CancellationToken cancellationToken) =>
        RunUnderTransitionGateAsync(StartCycleUnderGateAsync, cancellationToken);

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
        if (_client is not null || !_config.DeviceIntegration.Enabled)
        {
            return;
        }

        DeviceCycleState retryState = State;
        using CancellationTokenSource startLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await RunCancellationSafeStartAsync(
            StartCycleCoreUnderGateAsync,
            CleanupCanceledStartAsync,
            () => SetState(retryState),
            startLifetime.Token).ConfigureAwait(false);
    }

    /// <summary>Runs one start attempt while guaranteeing that linked cancellation applies its
    /// ownership policy, restores the state from which the attempt may be retried, and is rethrown.</summary>
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
        _intentionalStop = false;
        SetState(DeviceCycleState.Detected);
        DevicePackageSlotGate? slotGate;
        try
        {
            slotGate = await DevicePackageSlotGate.TryAcquireAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleStartFault(new InvalidOperationException(
                "The protected Device Plugin slot could not be locked for startup.",
                ex));
            return;
        }
        if (slotGate is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleStartFault(new TimeoutException(
                "The protected Device Plugin slot remained busy during startup."));
            return;
        }

        InstalledDevicePackage package;
        long cycleGeneration;
        DeviceHostClient client;
        await using (slotGate)
        {
            try
            {
                // Maintenance and host startup share this gate. Reconcile the fixed recovery
                // sibling before discovery so a process death between the two atomic moves cannot
                // make the previously installed package disappear permanently.
                DevicePackageStager.ReconcileInstalledPackage(
                    DeviceInstallationPaths.InstalledPackageRoot);
                _identity = DeviceMachineIdentity.Collect();
                _packageDiscovery = await DiscoverPackageAsync(_identity, cancellationToken)
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
                    "The protected Device Plugin slot could not be reconciled or discovered.",
                    ex));
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            InstalledDevicePackage? discoveredPackage = InstalledPackage;
            _physicalGlyphs.ReplacePackageProfiles([]);
            if (discoveredPackage is null || !discoveredPackage.Valid)
            {
                SetState(DeviceCycleState.Passive);
                string refusal = _packageDiscovery.ErrorCode
                    ?? discoveredPackage?.RejectionCode
                    ?? "no-package-installed";
                Log.Warn(
                    $"Device cycle passive: {refusal}; packageRoots={_packageDiscovery.Inventory.PackageRoots.Count}.");
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            package = discoveredPackage;

            cycleGeneration = Interlocked.Increment(ref _cycleGeneration);
            SetState(DeviceCycleState.Activating);
            try
            {
                client = await DeviceHostClient.StartAsync(
                    package,
                    _sessionId,
                    cycleGeneration,
                    cancellationToken).ConfigureAwait(false);
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
        }
        _client = client;
        try
        {
            Attach(client);
            _capabilities.Attach(client, cycleGeneration);
            UpdateCapabilityDesiredContext();
            _oemActions.Attach(client, cycleGeneration);
            UpdateOemConfiguration();
            bool controllerManagement = EffectiveControllerManagement(_config);
            if (_config.DeviceIntegration.ControllerManagementEnabled && !controllerManagement)
            {
                Log.Warn(DeviceFeatureAvailability.ControllerManagementDetail);
            }
            // Before the plugin starts, because the plugin's first job is to find the physical
            // controller and it cannot find one that HidHide is hiding from this process. Doing it
            // afterwards is too late for the cycle that needed it.
            await _controllers.EnsureHidHideReadableAsync(controllerManagement, cancellationToken)
                .ConfigureAwait(false);
            DeviceLifecycleNotification activation = await client.StartAsync(
                _identity,
                cycleGeneration,
                controllerManagement,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

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
            Log.Info(
                $"Device cycle active: package={package.Manifest?.Id}, "
                    + $"cycleGeneration={cycleGeneration}, "
                    + $"state={activation.State}.");
            Observe(ObserveHostExitAsync(client), "host supervision");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // DeviceHost owns its own start deadline. If that deadline expires after the plugin
            // entered StartAsync, the caller token is still live even though hardware may already
            // be acquired. Give the host the same fresh bounded handoff used by caller cancellation
            // before its kill-on-close job can be disposed.
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                DeviceStopReason.StartCanceled,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                DeviceStopReason.StartFailed,
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
            () => CleanupAbortedStartAsync(DeviceStopReason.StartCanceled)));
    }

    /// <summary>Preserves a possibly active client when shutdown canceled startup, because the
    /// shutdown owner must perform the bounded handoff. An independent caller cancellation runs
    /// its own fresh bounded teardown before the client can be disposed.</summary>
    internal static Task RunCanceledStartCleanupPolicyAsync(
        bool lifetimeCancellationRequested,
        Func<Task> callerCleanupAsync)
    {
        ArgumentNullException.ThrowIfNull(callerCleanupAsync);
        return lifetimeCancellationRequested
            ? Task.CompletedTask
            : callerCleanupAsync();
    }

    private async Task CleanupAbortedStartAsync(DeviceStopReason reason)
    {
        bool teardownVerified = false;
        try
        {
            await RunFreshBoundedCleanupAsync(
                CanceledStartCleanupBudget,
                async (deadline, cancellationToken) =>
                {
                    DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                        reason,
                        deadline,
                        cancellationToken).ConfigureAwait(false);
                    teardownVerified = teardown.Verified;
                    ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
                }).ConfigureAwait(false);
        }
        catch (Exception ex) when (!teardownVerified && ex is not OutOfMemoryException)
        {
            _teardownFailures.Retain(ex);
            throw;
        }
    }

    private async Task ScheduleStartFaultAfterCleanupAsync(
        Exception startFailure,
        DeviceStopReason reason,
        CancellationToken startCancellationToken)
    {
        Exception failure = startFailure;
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
        Func<DateTimeOffset, CancellationToken, Task> cleanupAsync,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        utcNow ??= static () => DateTimeOffset.UtcNow;
        using var cleanupCancellation = new CancellationTokenSource(budget);
        await cleanupAsync(
            utcNow().Add(budget),
            cleanupCancellation.Token).ConfigureAwait(false);
    }

    private async Task ObserveHostExitAsync(DeviceHostClient client)
    {
        DeviceHostExit exit = await NormalizeHostCompletionAsync(client.Completion)
            .ConfigureAwait(false);
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            Exception? hostExitFailure = UnverifiedHostExitFailure(_intentionalStop, exit);
            if (hostExitFailure is not null)
            {
                _teardownFailures.Retain(hostExitFailure);
            }
            _client = null;
            IReadOnlyList<Exception> cleanupFailures = await RunHostExitOwnerCleanupAsync(
                () => DetachAsync(client),
                client.DisposeAsync).ConfigureAwait(false);
            foreach (Exception cleanupFailure in cleanupFailures)
            {
                _teardownFailures.Retain(cleanupFailure);
            }
            if (cleanupFailures.Count > 0)
            {
                SetState(DeviceCycleState.Faulted);
                Log.Error(
                    "DeviceHost exit cleanup was incomplete; automatic restart is blocked",
                    cleanupFailures.Count == 1
                        ? cleanupFailures[0]
                        : new AggregateException(cleanupFailures));
                return;
            }

            if (!ShouldRestartAfterHostExit(
                _intentionalStop,
                _disposed,
                _config.DeviceIntegration.Enabled,
                exit,
                cleanupVerified: true))
            {
                SetState(DeviceCycleState.Disabled);
                return;
            }

            Log.Warn(
                $"DeviceHost fault: generation={_cycleGeneration}, reason={exit.Reason}, "
                    + $"exit={exit.ExitCode}, detail={exit.Detail}.");
            ScheduleFaultRecovery();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error("DeviceHost restart failed; cycle faulted", ex);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    internal static async Task<DeviceHostExit> NormalizeHostCompletionAsync(
        Task<DeviceHostExit> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        try
        {
            return await completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new DeviceHostExit(
                71,
                DeviceHostExitReason.ProcessFault,
                $"DeviceHost supervision failed ({ex.GetType().Name}): {ex.Message}",
                TimeSpan.Zero);
        }
    }

    internal static async ValueTask<IReadOnlyList<Exception>> RunHostExitOwnerCleanupAsync(
        Func<ValueTask> detachAsync,
        Func<ValueTask> disposeAsync)
    {
        ArgumentNullException.ThrowIfNull(detachAsync);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        List<Exception> failures = [];
        try
        {
            try
            {
                await detachAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
            }
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
            }
        }

        return failures;
    }

    internal static bool ShouldRestartAfterHostExit(
        bool intentionalStop,
        bool coordinatorDisposed,
        bool integrationEnabled,
        DeviceHostExit exit,
        bool cleanupVerified)
    {
        ArgumentNullException.ThrowIfNull(exit);
        return cleanupVerified
            && !intentionalStop
            && !coordinatorDisposed
            && integrationEnabled
            && exit.Reason is not DeviceHostExitReason.Intentional;
    }

    internal static Exception? UnverifiedHostExitFailure(
        bool intentionalStop,
        DeviceHostExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        return intentionalStop || exit.Reason is DeviceHostExitReason.Intentional
            ? null
            : new InvalidOperationException(
                $"DeviceHost exited before verified teardown: reason={exit.Reason}, "
                    + $"exit={exit.ExitCode}, detail={exit.Detail}.");
    }

    private void ScheduleStartFault(Exception exception)
    {
        if (_faultRecoveryPending || _disposed || !_config.DeviceIntegration.Enabled)
        {
            return;
        }

        _faultRecoveryPending = true;
        Log.Error("DeviceHost start or handshake failed", exception);
        Observe(HandleStartFaultAsync(), "host start fault recovery");
    }

    private async Task HandleStartFaultAsync()
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_faultRecoveryPending)
            {
                return;
            }

            _faultRecoveryPending = false;
            if (_disposed || !_config.DeviceIntegration.Enabled || _client is not null)
            {
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
        if (_automaticRestartAttempts >= 2)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error(
                $"Device cycle faulted after restart exhaustion: package={InstalledPackage?.Manifest?.Id}, "
                    + "the two automatic restart attempts were exhausted.");
            return;
        }

        TimeSpan backoff = _automaticRestartAttempts++ == 0
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(4);
        SetState(DeviceCycleState.Activating);
        Log.Warn(
            $"DeviceHost restart {_automaticRestartAttempts}/2 scheduled in "
                + $"{backoff.TotalSeconds:0.#} s.");
        Observe(RestartAfterDelayAsync(backoff), "delayed host restart");
    }

    private async Task RestartAfterDelayAsync(TimeSpan backoff)
    {
        await Task.Delay(backoff, _lifetime.Token).ConfigureAwait(false);
        if (!_disposed && _config.DeviceIntegration.Enabled && _client is null)
        {
            await StartCycleAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task<DeviceClientTeardownResult> StopCycleUnderGateAsync(
        DeviceStopReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        DeviceHostClient? client = _client;
        _client = null;
        if (client is null)
        {
            SetState(DeviceCycleState.Disabled);
            return DeviceClientTeardownResult.Clean;
        }

        DeviceClientTeardownResult? ownerTeardown = null;
        async Task<DeviceClientTeardownResult> TeardownOwnerAsync()
        {
            DeviceClientTeardownResult result = await RunClientTeardownAsync(
                token => _controllers.MakeSafeAsync(
                    HandoffScope.FullDeactivation,
                    inner => client.ReleaseControllerAsync(
                        HandoffScope.FullDeactivation,
                        deadline,
                        inner),
                    token),
                token => client.StopAsync(
                    reason,
                    deadline,
                    token),
                () => DetachAsync(client),
                client.DisposeAsync,
                cancellationToken).ConfigureAwait(false);
            ownerTeardown = result;
            return result;
        }

        DeviceClientTeardownResult teardown = await RunClientTeardownWithStateNotificationsAsync(
            _capabilities.CloseCommandAdmission,
            () => SetState(DeviceCycleState.Deactivating),
            TeardownOwnerAsync,
            () => SetState(DeviceCycleState.Disabled)).ConfigureAwait(false);
        if (ownerTeardown?.Verified is true
            && reason is not (DeviceStopReason.StartCanceled or DeviceStopReason.StartFailed))
        {
            _teardownFailures.ResolveAfterVerifiedOwnerTeardown();
        }
        return teardown;
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
            DeviceClientTeardownResult teardown = await teardownAsync().ConfigureAwait(false);
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

        return new DeviceClientTeardownResult(failures.ToArray());
    }

    /// <summary>Attempts both protocol cleanup phases before detaching and disposing the client.
    /// Every non-fatal unverified response or exception is retained while later phases continue.</summary>
    internal static async Task<DeviceClientTeardownResult> RunClientTeardownAsync(
        Func<CancellationToken, Task<DeviceControllerHandoffResponse>> releaseControllerAsync,
        Func<CancellationToken, Task<DeviceLifecycleNotification>> stopAsync,
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
                DeviceControllerHandoffResponse handoff = await releaseControllerAsync(
                    cancellationToken).ConfigureAwait(false);
                if (handoff.Result is ControllerHandoffResult.ReleasedVerified
                    && handoff.Step is (ControllerHandoffStep.TopologyVerified
                        or ControllerHandoffStep.WsgmStateRemoved))
                {
                    Log.Info($"Device controller release: {handoff.Step}, {handoff.Result}.");
                }
                else
                {
                    var failure = new InvalidOperationException(
                        $"Device controller release was unverified: {handoff.Step}, {handoff.Result}.");
                    failures.Add(failure);
                    Log.Warn(failure.Message);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device controller release unverified; cleanup continues: {ex.Message}");
            }

            try
            {
                DeviceLifecycleNotification stopped = await stopAsync(cancellationToken)
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
                try
                {
                    await detachAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add(ex);
                    Log.Warn($"Device client detach was incomplete: {ex.Message}");
                }
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

        return new DeviceClientTeardownResult(failures.ToArray());
    }

    internal static void ThrowIfDeviceTeardownIncomplete(
        DeviceClientTeardownResult teardown,
        CancellationToken cancellationToken)
    {
        if (teardown.Verified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Device teardown observed caller cancellation after retaining unverified release results.",
                teardown.ToException(),
                cancellationToken);
        }

        throw new InvalidOperationException(
            "Device hardware teardown completed, but one or more release steps were unverified.",
            teardown.ToException());
    }

    private static DateTimeOffset NormalShutdownDeadline() =>
        DateTimeOffset.UtcNow.AddSeconds(15);

    private async Task SetControllerManagementUnderGateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        DeviceHostClient? client = _client;
        if (client is null)
        {
            return;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        if (!enabled)
        {
            DeviceControllerHandoffResponse handoff = await _controllers.MakeSafeAsync(
                HandoffScope.ControllerOnly,
                token => client.ReleaseControllerAsync(
                    HandoffScope.ControllerOnly,
                    deadline,
                    token),
                cancellationToken).ConfigureAwait(false);
            Log.Info($"Controller management disabled: {handoff.Step}, {handoff.Result}.");

            // After the verified handoff, and never instead of it: the plugin remembers its
            // acquisition policy. Releasing the controller without telling it the feature is off
            // left ControllerService.Enabled true, so the next suspend/resume of the same cycle
            // reacquired and switched the physical controller against the persisted setting —
            // with no WSGM target left to receive it.
            try
            {
                await client.SetControllerManagementAsync(
                    enabled: false,
                    Interlocked.Read(ref _cycleGeneration),
                    DateTimeOffset.UtcNow.AddSeconds(6),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn(
                    "The plugin was not told that controller management is off; it may reacquire "
                    + $"on resume: {ex.Message}");
            }

            return;
        }

        // Before the plugin is asked to acquire, exactly as at cycle start: it cannot discover an
        // interface another application's HidHide allowlist is hiding from DeviceHost, and adding
        // the allowance afterwards does nothing for the acquisition that already failed.
        await _controllers.EnsureHidHideReadableAsync(true, cancellationToken).ConfigureAwait(false);
        long generation = Interlocked.Increment(ref _cycleGeneration);
        // The consumers move to the new cycle with the host, the same way resume does. DeviceHost
        // advances the plugin adapter to this generation, which resets the descriptor generation it
        // accepts; leaving the capability router and the OEM router on the previous one made the
        // plugin's first state after acquisition arrive against a cycle nothing was listening for.
        _capabilities.MarkCycleGenerationChanged(generation);
        UpdateCapabilityDesiredContext();
        _oemActions.Reset(generation);
        await client.SetControllerManagementAsync(
            enabled: true,
            generation,
            deadline,
            cancellationToken).ConfigureAwait(false);
        Log.Info($"Controller management enabled: cycleGeneration={generation}.");
    }

    private void Attach(DeviceHostClient client)
    {
        client.LifecycleStateReceived += OnLifecycleState;
        client.PhysicalIdentitiesReceived += OnPhysicalIdentities;
        client.ControllerSampleReceived += _controllers.Submit;
    }

    private async ValueTask DetachAsync(DeviceHostClient client)
    {
        client.LifecycleStateReceived -= OnLifecycleState;
        client.PhysicalIdentitiesReceived -= OnPhysicalIdentities;
        client.ControllerSampleReceived -= _controllers.Submit;
        // The plugin no longer owns the controller, so no further output frame may be written to
        // it. Withdrawing here closes that window before the routers are torn down, and the await
        // covers the frames that were already admitted: the write is asynchronous, so closing
        // admission alone left one in flight toward a controller that had been handed back.
        await _hapticSink.WithdrawAsync().ConfigureAwait(false);
        _capabilities.Detach();
        _pluginSettings.Detach();
        _oemActions.Detach();
    }

    /// <summary>
    /// Starts WSGM-side controller management for the controller the plugin just took.
    /// </summary>
    /// <remarks>
    /// Driven by the publication rather than by cycle start: WSGM may only hide a device and create
    /// a virtual target once the plugin has actually acquired the physical one, and the plugin
    /// republishes after a controller-management re-enable and after resume.
    /// </remarks>
    private void OnPhysicalIdentities(DevicePhysicalIdentitiesNotification notification)
    {
        long generation = Interlocked.Read(ref _cycleGeneration);
        _hapticSink.Publish(notification.Output, generation);
        Observe(
            StartControllerManagementAsync(notification.Devices, generation),
            "controller management start");
    }

    private async Task StartControllerManagementAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        long generation)
    {
        ControllerManagerStatus status = await _controllers.StartAsync(
            ControllerSelection.From(_config.DeviceIntegration),
            devices,
            _runningApplicationId,
            generation,
            _lifetime.Token).ConfigureAwait(false);
        Log.Info(
            $"Controller management: state={status.State}, target={status.Target}, "
            + $"source={status.TargetSource}, uiSource={status.UiSource}, detail={status.Detail}");
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
        await _controllers.ApplyRunningApplicationAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns the current capability projection for diagnostics and overlay clients.</summary>
    internal IReadOnlyList<DeviceCapabilityView> CapabilitySnapshot() =>
        _capabilities.Snapshot(DateTimeOffset.UtcNow);

    /// <summary>Current persisted physical-glyph presentation mode.</summary>
    internal DeviceGlyphSelection PhysicalGlyphSelection =>
        _config.DeviceIntegration.GlyphSelection;

    /// <summary>Resolves the current persisted mode against only the active package's safe profiles.</summary>
    internal PhysicalGlyphSelectionResult PhysicalGlyphSelectionSnapshot() =>
        _physicalGlyphs.SelectProfile(
            _config.DeviceIntegration.Enabled,
            MapGlyphSelection(_config.DeviceIntegration.GlyphSelection),
            _config.DeviceIntegration.ManualGlyphProfileId);

    /// <summary>Cycles the physical presentation policy and persists it without changing device ownership.</summary>
    internal async Task CyclePhysicalGlyphSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceGlyphSelection next = _config.DeviceIntegration.GlyphSelection switch
            {
                DeviceGlyphSelection.Automatic => DeviceGlyphSelection.NativeSteam,
                DeviceGlyphSelection.NativeSteam => DeviceGlyphSelection.ManualReviewedProfile,
                _ => DeviceGlyphSelection.Automatic,
            };
            AppConfig persisted = await Task.Run(() => ConfigStore.Mutate(config =>
            {
                config.DeviceIntegration.GlyphSelection = next;
            }), cancellationToken).ConfigureAwait(false);
            _config = persisted;
            ConfigurationChanged?.Invoke();
            Log.Info($"Physical glyph presentation changed: {next}.");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>
    /// Reports AutoTDP's live state for the Device surface.
    /// </summary>
    /// <param name="status">Provider owned by the session, or null when AutoTDP is not running.</param>
    /// <remarks>
    /// A provider rather than a reference because AutoTDP is composed later than this coordinator
    /// and depends on the performance service; the coordinator only needs to read its state, never
    /// to own its lifetime.
    /// </remarks>
    internal void AttachAutoTdpStatus(Func<AutoTdpStatus>? status) => _autoTdpStatus = status;

    /// <summary>
    /// Attaches the hook that pauses AutoTDP after a user-originated power-limit write.
    /// </summary>
    /// <param name="note">Receives the accepted wattage, or null when AutoTDP is not running.</param>
    /// <remarks>
    /// Attached here because this is the one path every surface's power write already goes through.
    /// The overlay row and the native-QAM TDP control each called <see cref="ExecuteCapabilityAsync"/>
    /// directly, so a manual change reached AutoTDP as ordinary telemetry and the next tick
    /// overwrote it — the documented permanent-until-resume override existed with nothing invoking
    /// it.
    /// </remarks>
    internal void AttachAutoTdpManualOverride(Action<int>? note) => _autoTdpManualOverride = note;

    /// <summary>Current AutoTDP state, or null when the service is not running.</summary>
    internal AutoTdpStatus? AutoTdpStatus => _autoTdpStatus?.Invoke();

    /// <summary>Raised when AutoTDP's own projection changed, rather than the device's.</summary>
    /// <remarks>
    /// Separate from <see cref="CapabilityViewsChanged"/>: AutoTDP moves between idle, controlling
    /// and paused, and its frametime detail changes, without any capability view changing at all.
    /// Both consumers render that state, so both need the transition.
    /// </remarks>
    internal event Action? AutoTdpStatusChanged;

    /// <summary>Reports that the session's AutoTDP service published a new projection.</summary>
    /// <remarks>Called by the session, which owns the service; the coordinator only reads it.</remarks>
    internal void NoteAutoTdpStatusChanged() => AutoTdpStatusChanged?.Invoke();

    /// <summary>Whether AutoTDP is switched on in the persisted configuration.</summary>
    internal bool AutoTdpEnabled => _config.DeviceIntegration.AutoTdpEnabled;

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    /// <remarks>
    /// Persisted rather than session-only, and applied by the ordinary configuration reload, so the
    /// overlay switch and the Settings checkbox are the same setting reached two ways.
    /// </remarks>
    internal Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default) =>
        SetAutoTdpEnabledAsync(!_config.DeviceIntegration.AutoTdpEnabled, cancellationToken);

    /// <summary>Sets AutoTDP to an explicit state and persists the choice.</summary>
    /// <param name="enabled">The state the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the setting is persisted.</returns>
    /// <remarks>
    /// The comparison happens inside the transition gate, so a command carrying an explicit value
    /// cannot land as its inverse. A toggle read outside the gate — which is what the native-QAM
    /// switch used to send — inverts whatever another surface persisted in between and still
    /// reports success.
    /// </remarks>
    internal async Task SetAutoTdpEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config.DeviceIntegration.AutoTdpEnabled == enabled)
            {
                Log.Info($"AutoTDP is already {(enabled ? "on" : "off")}; nothing to persist.");
                return;
            }

            AppConfig persisted = await Task.Run(
                () => ConfigStore.Mutate(config => config.DeviceIntegration.AutoTdpEnabled = enabled),
                cancellationToken).ConfigureAwait(false);
            _config = persisted;
            ConfigurationChanged?.Invoke();
            Log.Info($"AutoTDP switched {(enabled ? "on" : "off")} from the Device surface.");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Raised whenever controller management's truthful state changes.</summary>
    /// <remarks>
    /// Forwarded rather than exposing <see cref="ControllerManager"/> itself: the manager is the one
    /// owner of WSGM's controller half, and a consumer that could reach it directly would be able to
    /// order its steps out of sequence.
    /// </remarks>
    internal event Action<ControllerManagerStatus>? ControllerStatusChanged
    {
        add => _controllers.StatusChanged += value;
        remove => _controllers.StatusChanged -= value;
    }

    /// <summary>Every physical sample, unfiltered, for diagnostics only.</summary>
    /// <remarks>
    /// Forwarded from <see cref="ControllerManager"/> for the glyph input test. Never a way to drive
    /// input: a subscriber sees what the plugin reported and cannot change what is routed.
    /// </remarks>
    internal event Action<CanonicalControllerSample>? PhysicalSampleObserved
    {
        add => _controllers.PhysicalSampleObserved += value;
        remove => _controllers.PhysicalSampleObserved -= value;
    }

    /// <summary>Samples the UI may act on while a WSGM surface holds capture.</summary>
    /// <remarks>
    /// Filtered by the manager: the controls a surface is already using are removed, so the chord
    /// that opened the overlay cannot also activate whatever now has focus underneath it. This is
    /// the stream WSGM's own navigation runs on, and it is the reason the UI can be driven by rear
    /// paddles and a Quick Access button that SDL cannot see at all.
    /// </remarks>
    internal event Action<CanonicalControllerSample>? UiSampleReceived
    {
        add => _controllers.UiSampleReceived += value;
        remove => _controllers.UiSampleReceived -= value;
    }

    /// <summary>The current controller-management projection.</summary>
    internal ControllerManagerStatus ControllerStatus => _controllers.Snapshot();

    /// <summary>Controller targets the backend on this machine can actually create.</summary>
    internal IReadOnlyList<ManagedControllerTarget> SupportedControllerTargets =>
        _controllers.SupportedTargets;

    /// <summary>Whether controller management may run in this build and configuration.</summary>
    internal bool ControllerManagementEnabled =>
        EffectiveControllerManagement(_config) && _config.DeviceIntegration.Enabled;

    /// <summary>Changes the global default managed-controller target and persists the choice.</summary>
    /// <param name="target">The target to make the global default.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The controller state after the change was applied.</returns>
    /// <remarks>
    /// The stored setting is changed and then the manager is asked to re-resolve, in that order, so
    /// the persisted value and the running target cannot disagree if the apply fails — the setting
    /// is what the next reload and the Settings checkbox both read. Per-application overrides are
    /// deliberately untouched: this is the global default, and silently clearing an override the
    /// user set for one game would be a surprising side effect of changing the default.
    /// </remarks>
    internal async Task<ControllerManagerStatus> SetControllerTargetAsync(
        ManagedControllerTarget target,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppConfig persisted = await Task.Run(
                () => ConfigStore.Mutate(config => config.DeviceIntegration.ControllerTarget = target),
                cancellationToken).ConfigureAwait(false);
            _config = persisted;
            ConfigurationChanged?.Invoke();
            Log.Info($"Controller target set to {target} from the Device surface.");
            return await _controllers.ApplySelectionAsync(
                ControllerSelection.From(persisted.DeviceIntegration),
                _runningApplicationId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Routes one semantic capability command through current validation and serialization.</summary>
    /// <param name="capabilityId">The capability being commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="origin">Who asked for it, which decides whether AutoTDP steps aside.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The command result reported by the plugin.</returns>
    internal async Task<CapabilityCommandResult> ExecuteCapabilityAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CapabilityCommandOrigin origin = CapabilityCommandOrigin.User,
        CancellationToken cancellationToken = default)
    {
        CapabilityCommandResult result = await _capabilities.ExecuteAsync(
            capabilityId,
            instanceId,
            value,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (origin is CapabilityCommandOrigin.User)
        {
            NotifyManualPowerChange(capabilityId, instanceId, value, result);
        }

        return result;
    }

    private void NotifyManualPowerChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (_autoTdpManualOverride is not { } note
            || value?.IntegerValue is not { } watts
            || result.Outcome is not (CommandOutcome.AppliedVerified
                or CommandOutcome.AppliedUnverified))
        {
            return;
        }

        bool primaryPowerLimit = CapabilitySnapshot().Any(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && string.Equals(view.Descriptor.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(view.Descriptor.InstanceId, instanceId, StringComparison.Ordinal));
        if (!primaryPowerLimit)
        {
            return;
        }

        // Permanent until the user resumes control, by specification: quietly taking the limit back
        // a few seconds after they set it by hand would make the manual control look broken.
        Log.Info($"AutoTDP paused: the sustained power limit was set to {watts} W by hand.");
        note(watts);
    }

    /// <summary>Sets or clears a session-only desired value.</summary>
    internal void SetTemporaryDesired(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value) =>
        _capabilities.SetTemporaryDesired(capabilityId, instanceId, value);

    /// <summary>Attaches WSGM-owned UI and system actions after the shell surfaces exist.</summary>
    internal void ConfigureOemActions(DeviceOemActionServices actions) =>
        _oemActions.ConfigureActions(actions);

    /// <summary>Queues one whole per-device desired profile through the coalescing store.</summary>
    internal void QueueDesiredProfile(DeviceDesiredProfile profile) => _profiles.Queue(profile);

    /// <summary>The stored profile for the device this session is talking to, when there is one.</summary>
    /// <remarks>
    /// Keyed by the machine identity rather than by the package, so a user who swaps plugins keeps
    /// the values they set for this machine. Null before an identity is known, which is why every
    /// caller has to tolerate a missing profile rather than creating one eagerly.
    /// </remarks>
    private DeviceDesiredProfile? CurrentProfile
    {
        get
        {
            if (_identity is null)
            {
                return null;
            }

            string identityKey = DeviceMachineIdentity.StableKey(_identity);
            return _config.DeviceIntegration.Profiles.FirstOrDefault(item => string.Equals(
                item.DeviceIdentityKey,
                identityKey,
                StringComparison.Ordinal));
        }
    }

    /// <summary>The catalog holding the installed package's glyph profiles.</summary>
    /// <remarks>
    /// Exposed so one <c>PhysicalGlyphService</c> can be built over it and share its invalidation.
    /// The catalog is immutable data plus a change event; handing it out does not let a consumer
    /// load, replace or reach past a profile.
    /// </remarks>
    internal PhysicalGlyphCatalog PhysicalGlyphCatalog => _physicalGlyphs;

    /// <summary>The named hardware profiles this machine's stored values actually define.</summary>
    /// <remarks>
    /// Derived rather than declared. A profile exists exactly when some capability stores a value
    /// under its name, so there is no separate catalog to keep in step with the values — and a
    /// profile cannot be offered for selection while it would change nothing.
    /// </remarks>
    internal IReadOnlyList<string> HardwareProfileIds
    {
        get
        {
            DeviceDesiredProfile? profile = CurrentProfile;
            if (profile is null)
            {
                return [];
            }

            return profile.Capabilities
                .SelectMany(capability => capability.HardwareProfiles)
                .Select(value => value.ProfileId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(32)
                .ToArray();
        }
    }

    /// <summary>The named hardware profile currently selected, or null for none.</summary>
    internal string? SelectedHardwareProfileId => CurrentProfile?.SelectedHardwareProfileId;

    /// <summary>Selects a named hardware profile, or none, and persists the choice.</summary>
    /// <param name="profileId">The profile to select, or null to select none.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the choice is persisted and applied.</returns>
    /// <remarks>
    /// The stored profile is created if this machine has none, because selecting is the first thing
    /// a user can do and refusing until some other write happened first would be arbitrary. Applying
    /// is `UpdateCapabilityDesiredContext`, which is the same path a configuration reload takes.
    /// </remarks>
    internal async Task SelectHardwareProfileAsync(
        string? profileId,
        CancellationToken cancellationToken = default)
    {
        if (_identity is null)
        {
            return;
        }

        string identityKey = DeviceMachineIdentity.StableKey(_identity);
        string? normalized = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppConfig persisted = await Task.Run(
                () => ConfigStore.Mutate(config =>
                {
                    DeviceDesiredProfile? stored = config.DeviceIntegration.Profiles
                        .FirstOrDefault(item => string.Equals(
                            item.DeviceIdentityKey,
                            identityKey,
                            StringComparison.Ordinal));
                    if (stored is null)
                    {
                        stored = new DeviceDesiredProfile { DeviceIdentityKey = identityKey };
                        config.DeviceIntegration.Profiles.Add(stored);
                    }

                    stored.SelectedHardwareProfileId = normalized;
                }),
                cancellationToken).ConfigureAwait(false);
            _config = persisted;
            ConfigurationChanged?.Invoke();
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
            Log.Info($"Hardware profile selected: {normalized ?? "(none)"}.");
        }
        finally
        {
            _transitionGate.Release();
        }

        // Outside the transition gate on purpose: this writes to hardware, one bounded command per
        // capability, and holding the gate across them would block every other transition for as
        // long as the device takes. Selecting a profile used to end at the projection above, so the
        // Profiles page reported the profile active while the hardware kept its previous values.
        await ReconcileDesiredValuesAsync(
            $"hardware profile {normalized ?? "(none)"}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes every persistent desired value the hardware does not already hold.</summary>
    /// <param name="reason">What asked for the reconciliation, for the log.</param>
    /// <param name="cancellationToken">Cancels the remaining commands.</param>
    /// <returns>A task completing once every affected capability has been attempted.</returns>
    /// <remarks>
    /// Per-capability and independent: one refusal must not stop the rest, because a profile that
    /// applied its fan curve but not its power limit is still better than one that applied nothing.
    /// A temporary session value is skipped — it is already what the user asked for right now — and
    /// so is a value the device already reports, so reselecting the active profile is free.
    /// </remarks>
    private async Task ReconcileDesiredValuesAsync(string reason, CancellationToken cancellationToken)
    {
        int applied = 0;
        int unchanged = 0;
        int refused = 0;
        int skipped = 0;
        foreach (DeviceCapabilityView view in CapabilitySnapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!view.Descriptor.SupportsWrite
                || view.Projection.DesiredValue is not { } desired
                || view.Projection.DesiredSource is DesiredValueSource.None
                    or DesiredValueSource.TemporaryRequest)
            {
                continue;
            }

            if (!view.Projection.State.Available || view.Projection.DesiredValueOutOfRange)
            {
                skipped++;
                Log.Warn(
                    $"Desired value not applied for {view.Descriptor.CapabilityId}"
                    + $"{Instance(view.Descriptor.InstanceId)} ({reason}): available="
                    + $"{view.Projection.State.Available}, outOfRange="
                    + $"{view.Projection.DesiredValueOutOfRange}.");
                continue;
            }

            if (view.Projection.State.ObservedValue is { } observed && SameValue(observed, desired))
            {
                unchanged++;
                continue;
            }

            CapabilityCommandResult result = await ExecuteCapabilityAsync(
                view.Descriptor.CapabilityId,
                view.Descriptor.InstanceId,
                desired,
                TimeSpan.FromSeconds(5),
                // The user chose this profile, so its values are theirs: a power limit it carries
                // overrides automatic control exactly as moving the slider would.
                CapabilityCommandOrigin.User,
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified)
            {
                applied++;
                continue;
            }

            refused++;
            Log.Warn(
                $"Desired value refused for {view.Descriptor.CapabilityId}"
                + $"{Instance(view.Descriptor.InstanceId)} ({reason}): outcome={result.Outcome}, "
                + $"{result.Reason?.Detail ?? "no detail"}.");
        }

        Log.Info(
            $"Desired-value reconciliation ({reason}): applied={applied}, unchanged={unchanged}, "
            + $"refused={refused}, skipped={skipped}.");
    }

    private static string Instance(string? instanceId) =>
        instanceId is { Length: > 0 } id ? $"/{id}" : string.Empty;

    /// <summary>Compares two capability values, including curves, by content.</summary>
    /// <param name="observed">What the device reports.</param>
    /// <param name="desired">What WSGM wants.</param>
    /// <returns><see langword="true"/> when a write would change nothing.</returns>
    private static bool SameValue(CapabilityValue observed, CapabilityValue desired)
    {
        if (observed.Kind != desired.Kind)
        {
            return false;
        }

        // Field by field rather than record equality: CurveValue is compared by reference there,
        // which would report every curve as different and rewrite a fan table on each pass.
        return observed.Kind switch
        {
            CapabilityValueKind.Boolean => observed.BooleanValue == desired.BooleanValue,
            CapabilityValueKind.Integer => observed.IntegerValue == desired.IntegerValue,
            CapabilityValueKind.Choice => string.Equals(
                observed.ChoiceValue,
                desired.ChoiceValue,
                StringComparison.Ordinal),
            CapabilityValueKind.Color => observed.ColorValue == desired.ColorValue,
            CapabilityValueKind.Curve => observed.CurveValue.SequenceEqual(desired.CurveValue),
            _ => false,
        };
    }

    private void UpdateCapabilityDesiredContext()
    {
        DeviceDesiredProfile? profile = CurrentProfile;
        bool onAcPower = !NativeMethods.GetSystemPowerStatus(out NativeMethods.SystemPowerStatus power)
            || power.ACLineStatus != 0;
        _capabilities.UpdateDesiredContext(
            profile,
            onAcPower,
            profile?.SelectedHardwareProfileId,
            applicationId: null);
    }

    private void UpdateOemConfiguration()
    {
        DeviceDesiredProfile? profile = CurrentProfile;
        _oemActions.UpdateConfiguration(
            profile,
            EffectiveControllerManagement(_config),
            _config.DeviceIntegration.ControllerTarget);
    }

    private static bool EffectiveControllerManagement(AppConfig config)
        => config.DeviceIntegration.ControllerManagementEnabled
            && DeviceFeatureAvailability.ControllerManagement;

    private void LoadPhysicalGlyphProfiles(InstalledDevicePackage package)
    {
        try
        {
            GlyphPackageImportResult imported = DeviceGlyphPackageLoader.Load(package);
            _physicalGlyphs.ReplacePackageProfiles(imported.Profiles);
            foreach (GlyphPackageImportError error in imported.Errors)
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
            _physicalGlyphs.ReplacePackageProfiles([]);
            Log.Warn($"Device glyph catalog unavailable: {exception.Message}");
        }
    }

    internal static PhysicalGlyphSelectionMode MapGlyphSelection(DeviceGlyphSelection selection) =>
        selection switch
        {
            DeviceGlyphSelection.Automatic => PhysicalGlyphSelectionMode.Automatic,
            DeviceGlyphSelection.NativeSteam => PhysicalGlyphSelectionMode.NativeSteam,
            DeviceGlyphSelection.ManualReviewedProfile => PhysicalGlyphSelectionMode.ManualReviewed,
            _ => PhysicalGlyphSelectionMode.Automatic,
        };

    private Task<DevicePackageDiscovery> DiscoverPackageAsync(
        DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken)
    {
        _ = identity;
        DevicePackageDiscoveryOptions options = DevicePackageDiscoveryOptions.Production();
        return Task.Run(
            () => DevicePackagePolicy.Discover(options),
            cancellationToken);
    }

    private DeviceCoordinatorDiagnosticsSnapshot DiagnosticsSnapshot()
    {
        IReadOnlyList<DeviceCapabilityView> capabilities = CapabilitySnapshot();
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = State,
            InstalledPackage = InstalledPackage?.Manifest is { } manifest
                ? new DeviceInstalledPackageDiagnostic(manifest.Id, manifest.Version)
                : null,
            CycleGeneration = Interlocked.Read(ref _cycleGeneration),
            CapabilityCount = capabilities.Count,
            HealthyCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Available
                && capability.Projection.State.Quality is HardwareStateQuality.Observed
                    or HardwareStateQuality.Verified),
            FaultedCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Quality is HardwareStateQuality.Faulted),
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private void OnLifecycleState(DeviceLifecycleNotification state)
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
    /// Every glyph surface — the Steam Input page, the overlay's glyph rows, and the navigation
    /// hints — resolves through <see cref="PhysicalGlyphSelectionSnapshot"/>, which will only return
    /// a profile that names the active device. DeviceHost has always sent this on the lifecycle
    /// notification and WSGM never read it, so the selector was asked to match against null and
    /// refused every profile. The package's artwork was unreachable no matter what it contained.
    /// </remarks>
    private void SetDeviceDefinitionId(string? deviceDefinitionId)
    {
        if (string.IsNullOrWhiteSpace(deviceDefinitionId)
            || string.Equals(_deviceDefinitionId, deviceDefinitionId, StringComparison.Ordinal))
        {
            return;
        }

        _deviceDefinitionId = deviceDefinitionId;
        Log.Info($"Device definition matched: {deviceDefinitionId}.");
        _physicalGlyphs.SetActiveDevice(deviceDefinitionId);
    }

    private void SetState(DeviceCycleState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        Log.Info($"Device cycle: state={state}, cycleGeneration={_cycleGeneration}.");
        StateChanged?.Invoke(state);
    }

    private void Observe(Task task, string operation)
    {
        Task observed = CompleteObservedAsync(task, operation);
        lock (_backgroundGate)
        {
            _backgroundTasks.Add(observed);
        }
        _ = RemoveObservedAsync(observed);
    }

    private async Task CompleteObservedAsync(Task task, string operation)
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
}

/// <summary>Complete retained outcome of controller handoff, plugin stop, detach, and disposal.</summary>
internal sealed record DeviceClientTeardownResult(IReadOnlyList<Exception> Failures)
{
    internal static DeviceClientTeardownResult Clean { get; } = new([]);

    internal bool Verified => Failures.Count == 0;

    internal Exception ToException() => Failures.Count == 1
        ? Failures[0]
        : new AggregateException("Multiple device teardown steps were unverified.", Failures);
}

internal sealed class DeviceTeardownFailureTracker
{
    private readonly object _gate = new();
    private readonly List<Exception> _failures = [];

    internal void Retain(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            _failures.Add(failure);
        }
    }

    internal void ResolveAfterVerifiedOwnerTeardown()
    {
        lock (_gate)
        {
            _failures.Clear();
        }
    }

    internal IReadOnlyList<Exception> Drain()
    {
        lock (_gate)
        {
            Exception[] retained = _failures.ToArray();
            _failures.Clear();
            return retained;
        }
    }
}
