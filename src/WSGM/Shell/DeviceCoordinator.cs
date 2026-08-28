using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Ipc;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Authoritative per-user, per-session owner of the process-long device cycle.</summary>
public sealed class DeviceCoordinator : IAsyncDisposable
{
    private readonly uint _sessionId;
    private readonly Mutex _ownerMutex;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly SemaphoreSlim _packageGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<DateTimeOffset> _hostFaults = [];
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly DeviceOemActionRouter _oemActions = new();
    private readonly DeviceCoordinatorDiagnosticsServer _diagnostics;
    private readonly DeviceProfileStore _profiles = new();
    private readonly PhysicalGlyphCatalog _physicalGlyphs = new();
    private AppConfig _config;
    private DeviceIdentitySnapshot? _identity;
    private DeviceHostClient? _client;
    private long _hostGeneration;
    private long _deviceGeneration;
    private bool _intentionalStop;
    private bool _faultRecoveryPending;
    private DateTimeOffset _lastManualRetry;
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
        _diagnostics = new DeviceCoordinatorDiagnosticsServer(sessionId, DiagnosticsSnapshot);
    }

    /// <summary>Current process-long lifecycle state.</summary>
    public DeviceCycleState State { get; private set; } = DeviceCycleState.Disabled;

    /// <summary>Whether the persisted master switch currently exposes the Device surface.</summary>
    internal bool IntegrationEnabled => _config.DeviceIntegration.Enabled;

    /// <summary>A verified newer package that will not affect the active process-long cycle.</summary>
    internal DevicePackageCandidate? StagedPackageUpdate => FindAdjacentPackageVersion(newer: true);

    /// <summary>The retained prior package version offered after a failed replacement activation.</summary>
    internal DevicePackageCandidate? RollbackPackage => FindAdjacentPackageVersion(newer: false);

    /// <summary>When a quarantined package may be retried under the frozen cooldown policy.</summary>
    internal DateTimeOffset? ManualRetryAvailableAt => State is DeviceCycleState.Quarantined
        ? _lastManualRetry + RestartPolicy.Default.ManualRetryCooldown
        : null;

    /// <summary>Current selected package, including any diagnostic rejection.</summary>
    public DevicePackageCandidate? SelectedPackage { get; private set; }

    /// <summary>Every package considered at the latest discovery.</summary>
    public IReadOnlyList<DevicePackageCandidate> Candidates { get; private set; } = [];

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

    /// <summary>
    /// Creates the one coordinator allowed in this interactive session, without blocking startup.
    /// </summary>
    /// <param name="config">Initial normalized application configuration.</param>
    /// <returns>The owner, or null when another WSGM process already owns this session.</returns>
    public static DeviceCoordinator? TryStart(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        uint sessionId = (uint)Process.GetCurrentProcess().SessionId;
        string ownerName = $"Local\\WSGM.DeviceOwner.{sessionId}";
        Mutex owner = new(initiallyOwned: true, ownerName, out bool createdNew);
        if (!createdNew)
        {
            owner.Dispose();
            Log.Warn($"Device cycle: another coordinator owns session {sessionId}; no host started.");
            return null;
        }

        DeviceCoordinator coordinator = new(
            config,
            sessionId,
            owner,
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        if (config.DeviceIntegration.Enabled)
        {
            coordinator.Observe(coordinator.StartCycleAsync(coordinator._lifetime.Token), "initial start");
        }
        else
        {
            Log.Info($"Device cycle: coordinator ready for session {sessionId}; integration disabled.");
        }

        return coordinator;
    }

    /// <summary>Applies a saved ownership configuration to this authoritative process.</summary>
    public async Task ApplyConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasEnabled = _config.DeviceIntegration.Enabled;
            bool controllerWasEnabled = EffectiveControllerManagement(_config);
            _config = config;
            bool controllerIsEnabled = EffectiveControllerManagement(config);
            if (config.DeviceIntegration.ControllerManagementEnabled && !controllerIsEnabled)
            {
                Log.Warn(DeviceFeatureAvailability.ControllerManagementDetail);
            }
            ConfigurationChanged?.Invoke();
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
            if (!wasEnabled && config.DeviceIntegration.Enabled)
            {
                _hostFaults.Clear();
                await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (wasEnabled && !config.DeviceIntegration.Enabled)
            {
                await StopCycleUnderGateAsync(
                    DeviceDeactivationReason.Updating,
                    DeactivationBudget.Normal,
                    cancellationToken).ConfigureAwait(false);
                _physicalGlyphs.ReplacePackageProfiles([]);
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
                Interlocked.Increment(ref _deviceGeneration),
                deadline,
                cancellationToken).ConfigureAwait(false);
            _capabilities.MarkDeviceGenerationChanged(_deviceGeneration);
            UpdateCapabilityDesiredContext();
            _oemActions.Reset(_deviceGeneration);
            SetState(state.State);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Retries a quarantined package after the frozen cooldown.</summary>
    public async Task<bool> RetryAfterQuarantineAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not DeviceCycleState.Quarantined
                || DateTimeOffset.UtcNow - _lastManualRetry < RestartPolicy.Default.ManualRetryCooldown)
            {
                return false;
            }

            _lastManualRetry = DateTimeOffset.UtcNow;
            _hostFaults.Clear();
            await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>
    /// Verifies and stages an expanded offline package without changing the active device cycle.
    /// </summary>
    internal async Task<DevicePackageCandidate> StagePackageAsync(
        string sourceDirectory,
        DevicePluginTrustTier trustTier,
        IDevicePackageSignatureVerifier signatureVerifier,
        CancellationToken cancellationToken = default)
    {
        await _packageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceIdentitySnapshot identity = _identity ?? DeviceMachineIdentity.Collect();
            DevicePackageDiscoveryOptions options = DevicePackageDiscoveryOptions.Production(
                _config.DeviceIntegration.DeveloperMode);
            string destinationRoot = trustTier switch
            {
                DevicePluginTrustTier.SignedExternal => options.SignedExternalRoot,
                DevicePluginTrustTier.SideloadedCommunity => options.CommunityRoot,
                DevicePluginTrustTier.Developer => options.DeveloperRoot,
                _ => throw new InvalidOperationException(
                    "WSGM-reviewed packages are installed only by the release installer."),
            };
            DevicePackageCandidate staged = await DevicePackageStager.StageAsync(
                sourceDirectory,
                destinationRoot,
                trustTier,
                identity,
                signatureVerifier,
                cancellationToken).ConfigureAwait(false);

            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Candidates = await DiscoverPackagesAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _transitionGate.Release();
            }

            ConfigurationChanged?.Invoke();
            Log.Info($"Device package staged: id={staged.Manifest?.Id}, "
                + $"version={staged.Manifest?.Version}; applies at next device-cycle start.");
            return staged;
        }
        finally
        {
            _packageGate.Release();
        }
    }

    /// <summary>Runs full deactivation and explicitly activates the staged newer package.</summary>
    internal async Task<bool> ApplyStagedPackageNowAsync(
        CancellationToken cancellationToken = default)
    {
        await _packageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DevicePackageCandidate? staged = FindAdjacentPackageVersion(newer: true);
                if (staged?.Manifest is null || _identity is null)
                {
                    return false;
                }

                await StopCycleUnderGateAsync(
                    DeviceDeactivationReason.Updating,
                    DeactivationBudget.Normal,
                    cancellationToken).ConfigureAwait(false);
                PinPackageVersion(
                    DeviceMachineIdentity.StableKey(_identity),
                    staged.Manifest.Id,
                    staged.Manifest.Version);
                _intentionalStop = false;
                await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
                return SelectedPackage?.Manifest is { } selected
                    && string.Equals(selected.Id, staged.Manifest.Id, StringComparison.Ordinal)
                    && string.Equals(selected.Version, staged.Manifest.Version, StringComparison.Ordinal);
            }
            finally
            {
                _transitionGate.Release();
            }
        }
        finally
        {
            _packageGate.Release();
        }
    }

    /// <summary>Runs full deactivation and pins the retained previous immutable package version.</summary>
    internal async Task<bool> RollbackPackageAsync(CancellationToken cancellationToken = default)
    {
        await _packageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DevicePackageCandidate? rollback = FindAdjacentPackageVersion(newer: false);
                if (rollback?.Manifest is null || _identity is null)
                {
                    return false;
                }

                await StopCycleUnderGateAsync(
                    DeviceDeactivationReason.IntegrationDisabled,
                    DeactivationBudget.Normal,
                    cancellationToken).ConfigureAwait(false);
                PinPackageVersion(
                    DeviceMachineIdentity.StableKey(_identity),
                    rollback.Manifest.Id,
                    rollback.Manifest.Version);
                _intentionalStop = false;
                await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
                return SelectedPackage?.Manifest is { } selected
                    && string.Equals(selected.Id, rollback.Manifest.Id, StringComparison.Ordinal)
                    && string.Equals(selected.Version, rollback.Manifest.Version, StringComparison.Ordinal);
            }
            finally
            {
                _transitionGate.Release();
            }
        }
        finally
        {
            _packageGate.Release();
        }
    }

    /// <summary>Stops the cycle under the correct full-deactivation budget.</summary>
    public async Task StopAsync(
        DeviceDeactivationReason reason,
        DeactivationBudget budget,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCycleUnderGateAsync(reason, budget, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync(
                DeviceDeactivationReason.WsgmExiting,
                DeactivationBudget.Normal,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Device cycle shutdown was unverified: {ex.Message}");
        }

        _lifetime.Cancel();
        Task[] background;
        lock (_backgroundGate)
        {
            background = _backgroundTasks.ToArray();
        }
        await Task.WhenAll(background).ConfigureAwait(false);
        await _diagnostics.DisposeAsync().ConfigureAwait(false);
        await _profiles.DisposeAsync().ConfigureAwait(false);
        await _capabilities.DisposeAsync().ConfigureAwait(false);
        _oemActions.Dispose();
        _physicalGlyphs.Dispose();
        _lifetime.Dispose();
        _transitionGate.Dispose();
        _packageGate.Dispose();
        try
        {
            _ownerMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _ownerMutex.Dispose();
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

        _intentionalStop = false;
        SetState(DeviceCycleState.Detected);
        _identity = DeviceMachineIdentity.Collect();
        string identityKey = DeviceMachineIdentity.StableKey(_identity);
        Candidates = await DiscoverPackagesAsync(_identity, cancellationToken).ConfigureAwait(false);
        DevicePackageSelection? explicitPackage = _config.DeviceIntegration.PackageSelections.FirstOrDefault(
            selection => string.Equals(
                selection.DeviceIdentityKey,
                identityKey,
                StringComparison.Ordinal));
        SelectedPackage = DevicePackagePolicy.Select(Candidates, explicitPackage, out string? refusal);
        _physicalGlyphs.ReplacePackageProfiles([]);
        if (SelectedPackage is null)
        {
            SetState(DeviceCycleState.Passive);
            Log.Warn($"Device cycle passive: {refusal}; candidates={Candidates.Count}.");
            return;
        }

        long hostGeneration = Interlocked.Increment(ref _hostGeneration);
        long deviceGeneration = Interlocked.Increment(ref _deviceGeneration);
        SetState(DeviceCycleState.Activating);
        DeviceHostClient client;
        try
        {
            client = await DeviceHostClient.StartAsync(
                SelectedPackage,
                _sessionId,
                hostGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ScheduleStartFault(ex);
            return;
        }
        Attach(client);
        _client = client;
        _capabilities.Attach(client, hostGeneration, deviceGeneration);
        UpdateCapabilityDesiredContext();
        _oemActions.Attach(client, deviceGeneration);
        UpdateOemConfiguration();
        try
        {
            bool controllerManagement = EffectiveControllerManagement(_config);
            if (_config.DeviceIntegration.ControllerManagementEnabled && !controllerManagement)
            {
                Log.Warn(DeviceFeatureAvailability.ControllerManagementDetail);
            }
            DeviceLifecycleNotification activation = await client.ActivateAsync(
                _identity,
                deviceGeneration,
                controllerManagement,
                [],
                cancellationToken).ConfigureAwait(false);
            LoadPhysicalGlyphProfiles(SelectedPackage);
            SetState(activation.State);
            Log.Info(
                $"Device cycle active: package={SelectedPackage.Manifest?.Id}, "
                    + $"hostGeneration={hostGeneration}, deviceGeneration={deviceGeneration}, "
                    + $"state={activation.State}.");
            Observe(ObserveHostExitAsync(client), "host supervision");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _client = null;
            Detach(client);
            await client.DisposeAsync().ConfigureAwait(false);
            ScheduleStartFault(ex);
        }
    }

    private async Task ObserveHostExitAsync(DeviceHostClient client)
    {
        DeviceHostExit exit = await client.Completion.ConfigureAwait(false);
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            _client = null;
            Detach(client);
            await client.DisposeAsync().ConfigureAwait(false);
            if (_intentionalStop || _disposed || !_config.DeviceIntegration.Enabled
                || exit.Reason is DeviceHostExitReason.Intentional)
            {
                SetState(DeviceCycleState.Disabled);
                return;
            }

            Log.Warn(
                $"DeviceHost fault: generation={_hostGeneration}, reason={exit.Reason}, "
                    + $"exit={exit.ExitCode}, detail={exit.Detail}.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _hostFaults.RemoveAll(fault => now - fault > RestartPolicy.Default.Window);
            int faultsAlreadyInWindow = _hostFaults.Count;
            _hostFaults.Add(now);
            ScheduleFaultRecovery(faultsAlreadyInWindow);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetState(DeviceCycleState.Quarantined);
            Log.Error("DeviceHost restart failed; cycle quarantined", ex);
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
            _faultRecoveryPending = false;
            if (_disposed || !_config.DeviceIntegration.Enabled || _client is not null)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            _hostFaults.RemoveAll(fault => now - fault > RestartPolicy.Default.Window);
            int faultsAlreadyInWindow = _hostFaults.Count;
            _hostFaults.Add(now);
            ScheduleFaultRecovery(faultsAlreadyInWindow);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void ScheduleFaultRecovery(int faultsAlreadyInWindow)
    {
        FaultResponse response = RestartPolicy.Default.Evaluate(
            faultsAlreadyInWindow,
            out TimeSpan backoff);
        if (response is FaultResponse.Quarantine)
        {
            SetState(DeviceCycleState.Quarantined);
            Log.Error(
                $"Device cycle quarantined: package={SelectedPackage?.Manifest?.Id}, "
                    + $"faults={_hostFaults.Count}, window={RestartPolicy.Default.Window}.");
            return;
        }

        SetState(DeviceCycleState.Activating);
        Log.Warn($"DeviceHost restart scheduled in {backoff.TotalSeconds:0.#} s.");
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

    private async Task StopCycleUnderGateAsync(
        DeviceDeactivationReason reason,
        DeactivationBudget budget,
        CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        DeviceHostClient? client = _client;
        _client = null;
        if (client is null)
        {
            SetState(DeviceCycleState.Disabled);
            return;
        }

        SetState(DeviceCycleState.Deactivating);
        try
        {
            DateTimeOffset controllerDeadline = DateTimeOffset.UtcNow.Add(budget.ReleaseController);
            try
            {
                DeviceControllerHandoffResponse handoff = await client.ReleaseControllerAsync(
                    HandoffScope.FullDeactivation,
                    controllerDeadline,
                    cancellationToken).ConfigureAwait(false);
                Log.Info($"Device controller release: {handoff.Step}, {handoff.Result}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Device controller release unverified; cleanup continues: {ex.Message}");
            }

            DateTimeOffset hardwareDeadline = DateTimeOffset.UtcNow.Add(budget.RestoreHardware);
            try
            {
                DeviceLifecycleNotification stopped = await client.DeactivateAsync(
                    reason,
                    hardwareDeadline,
                    cancellationToken).ConfigureAwait(false);
                Log.Info($"Device hardware release: {stopped.State}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Device hardware release unverified; host will be terminated: {ex.Message}");
            }
        }
        finally
        {
            Detach(client);
            await client.DisposeAsync().ConfigureAwait(false);
            SetState(DeviceCycleState.Disabled);
        }
    }

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
            DeviceControllerHandoffResponse handoff = await client.ReleaseControllerAsync(
                HandoffScope.ControllerOnly,
                deadline,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"Controller management disabled: {handoff.Step}, {handoff.Result}.");
            return;
        }

        long generation = Interlocked.Increment(ref _deviceGeneration);
        await client.SetControllerManagementAsync(
            enabled: true,
            generation,
            deadline,
            cancellationToken).ConfigureAwait(false);
        Log.Info($"Controller management enabled: deviceGeneration={generation}.");
    }

    private void Attach(DeviceHostClient client)
    {
        client.LifecycleStateReceived += OnLifecycleState;
        client.ResourceStateReceived += OnResourceState;
    }

    private void Detach(DeviceHostClient client)
    {
        client.LifecycleStateReceived -= OnLifecycleState;
        client.ResourceStateReceived -= OnResourceState;
        _capabilities.Detach();
        _oemActions.Detach();
    }

    /// <summary>Returns the current capability projection for diagnostics and overlay clients.</summary>
    internal IReadOnlyList<DeviceCapabilityView> CapabilitySnapshot() =>
        _capabilities.Snapshot(DateTimeOffset.UtcNow);

    /// <summary>Resolves the current persisted mode against only the active package's safe profiles.</summary>
    internal PhysicalGlyphSelectionResult PhysicalGlyphSelectionSnapshot() =>
        _physicalGlyphs.SelectProfile(
            _config.DeviceIntegration.Enabled,
            MapGlyphSelection(_config.DeviceIntegration.GlyphSelection),
            SelectedPackage?.MatchedDevice?.Id,
            SelectedPackage?.MatchedDevice?.GlyphProfileId,
            _config.DeviceIntegration.ManualGlyphProfileId);

    /// <summary>Routes one semantic capability command through current validation and serialization.</summary>
    internal Task<CapabilityCommandResult> ExecuteCapabilityAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _capabilities.ExecuteAsync(
            capabilityId,
            instanceId,
            value,
            timeout,
            cancellationToken);

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

    private void UpdateCapabilityDesiredContext()
    {
        DeviceDesiredProfile? profile = null;
        if (_identity is not null)
        {
            string identityKey = DeviceMachineIdentity.StableKey(_identity);
            profile = _config.DeviceIntegration.Profiles.FirstOrDefault(item => string.Equals(
                item.DeviceIdentityKey,
                identityKey,
                StringComparison.Ordinal));
        }

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
        DeviceDesiredProfile? profile = null;
        if (_identity is not null)
        {
            string identityKey = DeviceMachineIdentity.StableKey(_identity);
            profile = _config.DeviceIntegration.Profiles.FirstOrDefault(item => string.Equals(
                item.DeviceIdentityKey,
                identityKey,
                StringComparison.Ordinal));
        }

        _oemActions.UpdateConfiguration(
            profile,
            EffectiveControllerManagement(_config),
            _config.DeviceIntegration.ControllerTarget);
    }

    private static bool EffectiveControllerManagement(AppConfig config)
        => config.DeviceIntegration.ControllerManagementEnabled
            && DeviceFeatureAvailability.ControllerManagement;

    private void LoadPhysicalGlyphProfiles(DevicePackageCandidate package)
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

    private Task<IReadOnlyList<DevicePackageCandidate>> DiscoverPackagesAsync(
        DeviceIdentitySnapshot identity,
        CancellationToken cancellationToken)
    {
        DevicePackageDiscoveryOptions options = DevicePackageDiscoveryOptions.Production(
            _config.DeviceIntegration.DeveloperMode);
        return Task.Run<IReadOnlyList<DevicePackageCandidate>>(
            () => DevicePackagePolicy.Discover(options, identity),
            cancellationToken);
    }

    private DevicePackageCandidate? FindAdjacentPackageVersion(bool newer)
    {
        if (SelectedPackage?.Manifest is not { } active
            || !Version.TryParse(active.Version, out Version? activeVersion))
        {
            return null;
        }

        IEnumerable<DevicePackageCandidate> matching = Candidates.Where(candidate =>
            candidate.Eligible
            && candidate.Manifest is { } manifest
            && string.Equals(manifest.Id, active.Id, StringComparison.Ordinal)
            && Version.TryParse(manifest.Version, out Version? candidateVersion)
            && (newer ? candidateVersion > activeVersion : candidateVersion < activeVersion));
        return newer
            ? matching.OrderByDescending(candidate => Version.Parse(candidate.Manifest!.Version))
                .FirstOrDefault()
            : matching.OrderByDescending(candidate => Version.Parse(candidate.Manifest!.Version))
                .FirstOrDefault();
    }

    private void PinPackageVersion(string identityKey, string packageId, string version)
    {
        AppConfig persisted = ConfigStore.Mutate(config =>
        {
            DevicePackageSelection? selection = config.DeviceIntegration.PackageSelections
                .FirstOrDefault(item => string.Equals(
                    item.DeviceIdentityKey,
                    identityKey,
                    StringComparison.Ordinal));
            if (selection is null)
            {
                config.DeviceIntegration.PackageSelections.Add(new DevicePackageSelection
                {
                    DeviceIdentityKey = identityKey,
                    PackageId = packageId,
                    Version = version,
                });
            }
            else
            {
                selection.PackageId = packageId;
                selection.Version = version;
            }
        });
        _config = persisted;
        ConfigurationChanged?.Invoke();
    }

    private DeviceCoordinatorDiagnosticsSnapshot DiagnosticsSnapshot()
    {
        IReadOnlyList<DeviceCapabilityView> capabilities = CapabilitySnapshot();
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = State,
            PackageId = SelectedPackage?.Manifest?.Id,
            PackageVersion = SelectedPackage?.Manifest?.Version,
            TrustTier = SelectedPackage?.TrustTier,
            HostGeneration = Interlocked.Read(ref _hostGeneration),
            DeviceGeneration = Interlocked.Read(ref _deviceGeneration),
            CapabilityCount = capabilities.Count,
            HealthyCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Available
                && capability.Projection.State.Quality is HardwareStateQuality.Observed
                    or HardwareStateQuality.Verified),
            FaultedCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Quality is HardwareStateQuality.Faulted),
            Packages = Candidates.Take(64).Select(candidate => new DevicePackageDiagnostic(
                candidate.Manifest?.Id,
                candidate.Manifest?.Version,
                candidate.TrustTier,
                candidate.Eligible,
                candidate.RejectionCode)).ToArray(),
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private void OnLifecycleState(DeviceLifecycleNotification state)
    {
        if (state.HostGeneration != _hostGeneration || state.DeviceGeneration < _deviceGeneration)
        {
            Log.Warn(
                $"Device lifecycle notification rejected as stale: host={state.HostGeneration}, "
                    + $"device={state.DeviceGeneration}.");
            return;
        }

        SetState(state.State);
    }

    private static void OnResourceState(DeviceResourceStateNotification state) =>
        Log.Info(
            $"Device resource: id={state.ResourceId}, generation={state.DeviceGeneration}, "
                + $"state={state.State}, reason={state.Reason?.Code.ToString() ?? "none"}.");

    private void SetState(DeviceCycleState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        Log.Info($"Device cycle: state={state}, hostGeneration={_hostGeneration}, "
            + $"deviceGeneration={_deviceGeneration}.");
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
