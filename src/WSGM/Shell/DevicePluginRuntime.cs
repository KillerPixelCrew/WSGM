using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>Owns the sole in-process device plugin and its process-long lifecycle.</summary>
internal sealed class DevicePluginRuntime : IAsyncDisposable, ICapabilityPublisher
{
    private static readonly TimeSpan EmergencyCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly DirectPluginHostAdapter _adapter;
    private readonly Lock _commandGate = new();
    private readonly Dictionary<Guid, CommandOperation> _commands = [];

    private readonly TaskCompletionSource<DeviceRuntimeExit> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LoadedPluginPackage<IDevicePlugin> _package;
    private readonly string _pluginStateRoot;
    private readonly CancellationTokenSource _startCancellation = new();
    private bool _commandAdmissionClosed;
    private volatile DeviceCycleState _cycleState = DeviceCycleState.Disabled;
    private string? _deviceDefinitionId;
    private int _disposeStarted;
    private volatile bool _disposed;
    private bool _pluginStartAttempted;
    private bool _pluginStopAttempted;
    private PluginSettingsManifest? _settingsManifest;
    private volatile bool _stopped;

    private DevicePluginRuntime(
        LoadedPluginPackage<IDevicePlugin> package,
        long cycleGeneration,
        string pluginStateRoot)
    {
        _package = package;
        CycleGeneration = cycleGeneration;
        _pluginStateRoot = pluginStateRoot;
        _adapter = new DirectPluginHostAdapter(this, cycleGeneration);
    }

    internal string PackageId => Plugin.PackageId;

    internal string StateDirectory => Path.Combine(_pluginStateRoot, PackageId);

    internal Task<DeviceRuntimeExit> Completion => _completion.Task;
    internal Task LateCleanup { get; private set; } = Task.CompletedTask;
    internal DeviceCycleState LifecycleState => _cycleState;

    private IDevicePlugin Plugin => _package.Plugin;
    internal PluginSettingsManifest? SettingsManifest => Volatile.Read(ref _settingsManifest);

    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(Deadline.After(EmergencyCleanupBudget)).ConfigureAwait(false);
    }

    public long CycleGeneration { get; private set; }

    /// <summary>The capability roles the package manifest declares; the router refuses any other.</summary>
    public IReadOnlyList<CapabilityRole> DeclaredCapabilities =>
        _package.Package?.DeviceManifest?.Capabilities ?? [];

    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;
    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    public async Task<DeviceCommandDispatch> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_cycleState is not (DeviceCycleState.Active or DeviceCycleState.Degraded))
        {
            return new DeviceCommandDispatch(Rejected(command, $"Device state is {_cycleState}."));
        }

        CommandOperation operation = new(command, cancellationToken, _lifetime.Token);
        lock (_commandGate)
        {
            if (_commandAdmissionClosed)
            {
                operation.Dispose();
                return new DeviceCommandDispatch(Rejected(command, "The device plugin is quiescing."));
            }

            if (!_commands.TryAdd(command.CommandId, operation))
            {
                operation.Dispose();
                return new DeviceCommandDispatch(Rejected(command, "The command ID is already in flight."));
            }
        }

        try
        {
            try
            {
                operation.Start(operation.Token.IsCancellationRequested
                    ? Task.FromResult(Rejected(command, "The command was canceled before dispatch."))
                    : Plugin.ExecuteCommandAsync(command, operation.Token).AsTask());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                operation.Fail(ex);
            }

            var result = await operation.Task.WaitAsync(operation.Token)
                .ConfigureAwait(false);
            return new DeviceCommandDispatch(result);
        }
        catch (OperationCanceledException)
        {
            if (operation.Task.IsCompleted)
            {
                return new DeviceCommandDispatch(await operation.Task.ConfigureAwait(false));
            }

            Log.Observe(RemoveCommandWhenCompleteAsync(operation), "Late device command cleanup", true);
            return new DeviceCommandDispatch(
                CanceledCommand(command, operation.DeadlinePassed),
                operation.Task);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new DeviceCommandDispatch(FailedCommand(command, ex));
        }
        finally
        {
            if (operation.Task.IsCompleted)
            {
                RemoveCommand(operation);
            }
        }
    }

    internal async ValueTask DisposeAsync(Deadline deadline)
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await DisposeCoreAsync(deadline).ConfigureAwait(false);
    }

    private async ValueTask DisposeCoreAsync(Deadline deadline)
    {
        CloseCommandAdmission();
        TryCancel(_startCancellation);
        CancelCommands();
        List<Exception> failures = [];
        using var cleanup = deadline.CreateCancellationSource();
        try
        {
            await _lifecycleGate.WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _disposed = true;
            LateCleanup = Task.Run(async () => await DisposeCoreAsync(Deadline.Never).ConfigureAwait(false));
            Log.Observe(LateCleanup, "Deferred device disposal", true);
            Complete(DeviceRuntimeExitReason.Intentional, "Device disposal is waiting for a lifecycle operation.");
            throw new AggregateException(
                "Device plugin disposal was blocked by a lifecycle operation that did not quiesce.",
                new TimeoutException("The device plugin lifecycle gate exceeded the cleanup budget."));
        }

        var canUnload = true;
        Task? pendingLifecycle = null;
        try
        {
            _disposed = true;
            var commandFailures = await QuiesceCommandsAsync(
                deadline,
                cleanup.Token).ConfigureAwait(false);
            failures.AddRange(commandFailures);
            canUnload = commandFailures.Count == 0;
            if (_pluginStartAttempted && !_stopped && !_pluginStopAttempted)
            {
                try
                {
                    _pluginStopAttempted = true;
                    var stopToken = cleanup.Token;
                    var stop = Task.Run(async () =>
                    {
                        stopToken.ThrowIfCancellationRequested();
                        return await Plugin.StopAsync(
                            new PluginStopContext(PluginStopReason.WsgmExiting, deadline),
                            stopToken).ConfigureAwait(false);
                    });
                    pendingLifecycle = stop;
                    var result = await stop.WaitAsync(cleanup.Token).ConfigureAwait(false);
                    pendingLifecycle = null;
                    if (result.Status is not PluginStopStatus.Clean)
                    {
                        canUnload = false;
                        failures.Add(new InvalidOperationException(
                            $"Emergency plugin cleanup was {result.Status}: "
                            + (result.Reason?.Detail ?? "no detail")));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    canUnload = false;
                    failures.Add(new InvalidOperationException(
                        "Emergency plugin cleanup failed.",
                        ex));
                }
            }

            try
            {
                if (pendingLifecycle is null || pendingLifecycle.IsCompleted)
                {
                    var dispose = Plugin.DisposeAsync().AsTask();
                    pendingLifecycle = dispose;
                    await dispose.WaitAsync(cleanup.Token).ConfigureAwait(false);
                    pendingLifecycle = null;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                canUnload = false;
                failures.Add(new InvalidOperationException("Plugin disposal failed.", ex));
            }
        }
        finally
        {
            if (canUnload)
            {
                PluginTrace.Install(null);
                _adapter.Dispose();
                try
                {
                    _package.Unload();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add(new InvalidOperationException("Plugin unload failed.", ex));
                }
            }

            TryCancel(_lifetime);
            _lifetime.Dispose();
            _startCancellation.Dispose();
            Complete(DeviceRuntimeExitReason.Intentional, "Device plugin disposed.");
            if (pendingLifecycle is not null)
            {
                LateCleanup = FinishLateDisposalAsync(pendingLifecycle);
            }
            else
            {
                _lifecycleGate.Release();
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Device plugin disposal was incomplete.", failures);
        }
    }

    internal event Action<DevicePluginState>? LifecycleStateReceived;

    internal event Action<(IReadOnlyList<PhysicalDeviceIdentity> Devices, HapticCapabilities? Output)>?
        PhysicalIdentitiesReceived;

    internal event Action<IReadOnlyList<OemControlDescriptor>>? OemControlsReceived;
    internal event Action<OemControlEvent>? OemEventReceived;
    internal event Action<CanonicalControllerSample>? ControllerSampleReceived;
    internal event Action<PluginSettingsManifest>? SettingsManifestReceived;

    internal static Task<DevicePluginRuntime> StartAsync(
        InstalledDevicePackage package,
        long cycleGeneration,
        CancellationToken cancellationToken,
        string pluginStateRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginStateRoot);
        return LoadAsync(package, cycleGeneration, Path.GetFullPath(pluginStateRoot), cancellationToken);

        static Task<DevicePluginRuntime> LoadAsync(
            InstalledDevicePackage package,
            long cycleGeneration,
            string stateRoot,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                () => new DevicePluginRuntime(
                    PluginLoader.LoadDevice(package),
                    cycleGeneration,
                    stateRoot),
                cancellationToken);
        }
    }

    internal Task<DevicePluginState> StartAsync(DeviceIdentitySnapshot identity, long cycleGeneration,
        bool controllerManagementEnabled, CancellationToken cancellationToken)
    {
        return RunLifecycleAsync(() => StartCoreAsync(identity, cycleGeneration, controllerManagementEnabled,
            cancellationToken), Deadline.After(TimeSpan.FromSeconds(15)), cancellationToken);
    }

    internal Task<DevicePluginState> SuspendAsync(Deadline deadline, CancellationToken cancellationToken)
    {
        return RunLifecycleAsync(() => SuspendCoreAsync(deadline, cancellationToken), deadline, cancellationToken);
    }

    internal Task<DevicePluginState> ResumeAsync(long cycleGeneration, Deadline deadline,
        CancellationToken cancellationToken)
    {
        return RunLifecycleAsync(() => ResumeCoreAsync(cycleGeneration, deadline, cancellationToken), deadline,
            cancellationToken);
    }

    internal Task<DevicePluginState> StopAsync(PluginStopReason reason, Deadline deadline,
        CancellationToken cancellationToken)
    {
        CloseCommandAdmission();
        return RunLifecycleAsync(() => StopCoreAsync(reason, deadline, cancellationToken), deadline, cancellationToken);
    }

    private static async Task<DevicePluginState> RunLifecycleAsync(Func<Task<DevicePluginState>> operation,
        Deadline deadline, CancellationToken cancellationToken)
    {
        using var bounded = deadline.CreateCancellationSource(cancellationToken);
        var work = Task.Run(operation);
        try
        {
            return await work.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            Log.Observe(work, "Late device lifecycle operation", true);
            throw;
        }
    }

    private async Task<DevicePluginState> StartCoreAsync(
        DeviceIdentitySnapshot identity,
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Global
        long cycleGeneration,
        bool controllerManagementEnabled,
        CancellationToken cancellationToken)
    {
        if (cycleGeneration != CycleGeneration)
        {
            throw new InvalidOperationException("Plugin start used a stale device generation.");
        }

        using var bounded = Deadline.After(TimeSpan.FromSeconds(15)).CreateCancellationSource(
            cancellationToken,
            _startCancellation.Token,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cycleState is not DeviceCycleState.Disabled)
            {
                throw new InvalidOperationException("The device plugin is already active.");
            }

            var detection = await Plugin.DetectAsync(
                new PluginDetectionContext { Identity = identity },
                bounded.Token).ConfigureAwait(false);
            if (!detection.Matched || string.IsNullOrWhiteSpace(detection.DeviceDefinitionId))
            {
                return PublishLifecycle(DeviceCycleState.Passive, detection.Reason);
            }

            _deviceDefinitionId = detection.DeviceDefinitionId;
            PublishLifecycle(DeviceCycleState.Detected, null);
            PublishLifecycle(DeviceCycleState.Activating, null);
            _pluginStartAttempted = true;
            var result = await Plugin.StartAsync(new PluginStartContext
            {
                Host = _adapter,
                CycleGeneration = CycleGeneration,
                DeviceDefinitionId = _deviceDefinitionId,
                StateDirectory = CreatePluginStateDirectory(Plugin.PackageId, _pluginStateRoot),
                ControllerManagementEnabled = controllerManagementEnabled
            }, bounded.Token).ConfigureAwait(false);
            return PublishLifecycle(MapOperationalState(result.State), result.Reason);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PublishLifecycle(DeviceCycleState.Degraded, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                DescribePluginFailure("start", ex),
                true));
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<DevicePluginState> SuspendCoreAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        using var bounded = deadline.CreateCancellationSource(cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            EnsureLifecycleOperationAllowed();
            await Plugin.SuspendAsync(
                new PluginQuiesceContext(deadline),
                bounded.Token).ConfigureAwait(false);
            return PublishLifecycle(DeviceCycleState.Suspended, null);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<DevicePluginState> ResumeCoreAsync(
        long cycleGeneration,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        using var bounded = deadline.CreateCancellationSource(cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            EnsureLifecycleOperationAllowed();
            if (_cycleState is not DeviceCycleState.Suspended || cycleGeneration <= CycleGeneration)
            {
                throw new InvalidOperationException(
                    "Plugin resume requires a suspended lifecycle and a fresh generation.");
            }

            CycleGeneration = cycleGeneration;
            _adapter.SetCycleGeneration(cycleGeneration);
            PublishLifecycle(DeviceCycleState.Activating, null);
            var result = await Plugin.ResumeAsync(
                new PluginResumeContext(cycleGeneration, deadline),
                bounded.Token).ConfigureAwait(false);
            return PublishLifecycle(MapOperationalState(result.State), result.Reason);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<DevicePluginState> StopCoreAsync(
        PluginStopReason reason,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        CloseCommandAdmission();
        TryCancel(_startCancellation);
        CancelCommands();
        using var bounded = deadline.CreateCancellationSource(cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return SnapshotLifecycle();
            }

            if (_pluginStopAttempted)
            {
                _stopped = true;
                var failed = PublishLifecycle(DeviceCycleState.Disabled,
                    new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                        "The earlier stop attempt did not produce a final result; it was not retried."));
                Complete(DeviceRuntimeExitReason.Intentional, "Device plugin stop remains unverified.");
                return failed;
            }

            if (!_pluginStartAttempted)
            {
                _stopped = true;
                var passiveStop = PublishLifecycle(DeviceCycleState.Disabled, null);
                Complete(DeviceRuntimeExitReason.Intentional, "Device plugin stopped without acquiring a cycle.");
                return passiveStop;
            }

            var commandFailures = await QuiesceCommandsAsync(
                deadline,
                bounded.Token).ConfigureAwait(false);
            PublishLifecycle(DeviceCycleState.Deactivating, null);
            _pluginStopAttempted = true;
            var result = await Plugin.StopAsync(
                new PluginStopContext(reason, deadline),
                bounded.Token).ConfigureAwait(false);
            _pluginStartAttempted = false;
            _stopped = true;
            var stopReason = result.Status switch
            {
                PluginStopStatus.Clean => null,
                PluginStopStatus.Unverified => result.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Plugin cleanup completed without verified restoration."),
                PluginStopStatus.Failed => result.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Plugin cleanup failed."),
                _ => new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    $"Plugin returned an unknown stop status {result.Status}.")
            };
            var stopped = PublishLifecycle(
                DeviceCycleState.Disabled,
                stopReason);
            Complete(DeviceRuntimeExitReason.Intentional, "Device plugin stopped.");
            if (commandFailures.Count > 0)
            {
                throw new AggregateException(
                    "Plugin commands did not quiesce cleanly before stop.",
                    commandFailures);
            }

            return stopped;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Delivers stored plugin settings inside the lifecycle lane.</summary>
    /// <param name="values">The resolved values.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    /// <returns>A task completing once the plugin took the values.</returns>
    /// <remarks>
    ///     Serialized with start, suspend, resume and stop, so a delivery never overlaps a stop and is
    ///     refused once the plugin stopped. A plugin that ignores cancellation keeps the lane until it
    ///     returns, as for every other lifecycle call; the caller still returns at the deadline.
    /// </remarks>
    internal async Task ApplySettingsValuesAsync(
        IReadOnlyList<DeviceSettingValue> values,
        CancellationToken cancellationToken)
    {
        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
        using var bounded = deadline.CreateCancellationSource(cancellationToken, _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        Task? apply = null;
        try
        {
            EnsureLifecycleOperationAllowed();
            var applyToken = bounded.Token;
            apply = Task.Run(async () =>
            {
                applyToken.ThrowIfCancellationRequested();
                await Plugin.ApplySettingsAsync(values, applyToken).ConfigureAwait(false);
            });
            await apply.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        finally
        {
            ReturnLifecycleGate(apply);
        }
    }

    internal Task ApplyHapticOutputAsync(
        HapticOutputFrame output,
        CancellationToken cancellationToken)
    {
        return _cycleState is not (DeviceCycleState.Active or DeviceCycleState.Degraded)
            ? Task.CompletedTask
            : Plugin.ApplyHapticOutputAsync(output, cancellationToken).AsTask();
    }

    internal async Task ReleaseControllerAsync(
        HandoffScope scope,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        if (scope is HandoffScope.FullDeactivation)
        {
            CloseCommandAdmission();
            TryCancel(_startCancellation);
            CancelCommands();
        }

        using var bounded = deadline.CreateCancellationSource(cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        Task? release = null;
        try
        {
            EnsureLifecycleActive();
            var commandFailures = scope is HandoffScope.FullDeactivation
                ? await QuiesceCommandsAsync(deadline, bounded.Token).ConfigureAwait(false)
                : [];
            if (commandFailures.Count > 0)
            {
                Log.Warn("Plugin commands did not quiesce cleanly before controller release: "
                         + string.Join("; ", commandFailures.Select(failure => failure.Message)));
            }

            var releaseToken = bounded.Token;
            release = Task.Run(async () =>
            {
                releaseToken.ThrowIfCancellationRequested();
                await Plugin.ReleaseControllerAsync(
                    new PluginControllerReleaseContext(scope, deadline),
                    releaseToken).ConfigureAwait(false);
            });
            await release.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        finally
        {
            ReturnLifecycleGate(release);
        }
    }

    private void ReturnLifecycleGate(Task? operation)
    {
        if (operation is null || operation.IsCompleted)
        {
            _lifecycleGate.Release();
            return;
        }

        // Keep this runtime and its package alive, and reject overlapping lifecycle calls until
        // code that ignored cancellation actually returns. The caller still returns at its deadline.
        _ = ObserveLateLifecycleAsync(operation);
    }

    private async Task ObserveLateLifecycleAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Device plugin lifecycle operation failed after its caller returned", ex);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task FinishLateDisposalAsync(Task operation)
    {
        try
        {
            if (operation is Task<PluginStopResult> stop)
            {
                var result = await stop.ConfigureAwait(false);
                await Plugin.DisposeAsync().ConfigureAwait(false);
                if (result.Status is not PluginStopStatus.Clean)
                {
                    Log.Warn("Late plugin stop remained unverified; its package remains loaded.");
                    return;
                }
            }
            else
            {
                await operation.ConfigureAwait(false);
            }

            // No plugin lifecycle call still runs. Only successful managed disposal releases the
            // package; a hung or failed disposal continues holding it, without holding up the caller.
            _adapter.Dispose();
            _package.Unload();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Late device plugin disposal failed; its package remains loaded", ex);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task SetControllerManagementAsync(
        bool enabled,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        using var bounded = deadline.CreateCancellationSource(cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            EnsureLifecycleOperationAllowed();
            await Plugin.SetControllerManagementAsync(
                new PluginControllerManagementContext(enabled, deadline),
                bounded.Token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void EnsureOperationAllowed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_commandGate)
        {
            if (_commandAdmissionClosed)
            {
                throw new InvalidOperationException("The device plugin is quiescing.");
            }
        }
    }

    private void EnsureLifecycleOperationAllowed()
    {
        EnsureOperationAllowed();
        EnsureLifecycleActive();
    }

    private void EnsureLifecycleActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pluginStartAttempted || _stopped)
        {
            throw new InvalidOperationException("The device plugin is not active.");
        }
    }

    private DevicePluginState PublishLifecycle(
        DeviceCycleState state,
        CapabilityReason? reason)
    {
        _cycleState = state;
        var notification = SnapshotLifecycle(reason);
        Raise(LifecycleStateReceived, notification, "lifecycle");
        return notification;
    }

    private DevicePluginState SnapshotLifecycle(CapabilityReason? reason = null)
    {
        return new DevicePluginState
        {
            State = _cycleState,
            CycleGeneration = CycleGeneration,
            DeviceDefinitionId = _deviceDefinitionId,
            Reason = reason
        };
    }

    private async Task RemoveCommandWhenCompleteAsync(CommandOperation operation)
    {
        await operation.Task.ConfigureAwait(false);
        RemoveCommand(operation);
    }

    private void RemoveCommand(CommandOperation operation)
    {
        lock (_commandGate)
        {
            if (_commands.TryGetValue(operation.Command.CommandId, out var current)
                && ReferenceEquals(current, operation))
            {
                _commands.Remove(operation.Command.CommandId);
            }
        }

        operation.Dispose();
    }

    private void CloseCommandAdmission()
    {
        lock (_commandGate)
        {
            _commandAdmissionClosed = true;
        }
    }

    private void CancelCommands()
    {
        CommandOperation[] commands;
        lock (_commandGate)
        {
            commands = [.. _commands.Values];
        }

        foreach (var command in commands)
        {
            command.Cancel();
        }
    }

    private async Task<IReadOnlyList<Exception>> QuiesceCommandsAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        CommandOperation[] commands;
        lock (_commandGate)
        {
            _commandAdmissionClosed = true;
            commands = [.. _commands.Values];
        }

        foreach (var command in commands)
        {
            command.Cancel();
        }

        if (commands.Length == 0)
        {
            return [];
        }

        List<Exception> failures = [];
        using var bounded = deadline.CreateCancellationSource(cancellationToken);
        try
        {
            await Task.WhenAll(commands.Select(command => command.Task))
                .WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
        }

        foreach (var command in commands)
        {
            if (command.Task.IsCompleted)
            {
                RemoveCommand(command);
            }
        }

        return failures;
    }

    private void Complete(DeviceRuntimeExitReason reason, string detail)
    {
        _completion.TrySetResult(new DeviceRuntimeExit(reason, detail));
    }

    private void ReportPluginFault(string scope, string message)
    {
        if (_disposed || _stopped)
        {
            Log.Change(
                "device-plugin-late-fault",
                $"Device plugin ignored a late {scope} fault after teardown: {message}");
            return;
        }

        // Completion is the coordinator's teardown trigger. Close admission first so observing the
        // fault can never race a new hardware write into the cycle that is already being released.
        CloseCommandAdmission();
        TryCancel(_startCancellation);
        CancelCommands();
        Complete(
            DeviceRuntimeExitReason.BackgroundFault,
            $"Plugin background service '{scope}' failed: {message}");
    }

    private static CapabilityCommandResult Rejected(CapabilityCommand command, string detail)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                detail,
                true),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static CapabilityCommandResult FailedCommand(
        CapabilityCommand command,
        Exception exception)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                DescribePluginFailure("command", exception)),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <param name="command">The command that did not produce a final result.</param>
    /// <param name="deadlinePassed">Whether the command's own deadline is what cancelled it.</param>
    /// <remarks>
    ///     Classified from the deadline source, not by comparing the wall clock with the deadline: the
    ///     source fires from a tick-resolution timer that can run ahead of <c>UtcNow</c> by up to one
    ///     tick, so the comparison alone called a deadline that had just fired Indeterminate.
    /// </remarks>
    private static CapabilityCommandResult CanceledCommand(
        CapabilityCommand command,
        bool deadlinePassed)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = deadlinePassed || command.Deadline.HasExpired
                ? CommandOutcome.TimedOut
                : CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The command was canceled before the plugin produced a final result."),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static DeviceCycleState MapOperationalState(PluginOperationalState state)
    {
        return state switch
        {
            PluginOperationalState.Active => DeviceCycleState.Active,
            PluginOperationalState.Passive => DeviceCycleState.Passive,
            PluginOperationalState.Degraded => DeviceCycleState.Degraded,
            _ => throw new InvalidDataException("Unknown plugin operational state.")
        };
    }

    private static string CreatePluginStateDirectory(string packageId, string stateRoot)
    {
        var directory = Path.Combine(stateRoot, packageId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string DescribePluginFailure(string operation, Exception exception)
    {
        return $"Plugin {operation} failed ({exception.GetType().Name}): {exception.Message}";
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (AggregateException ex)
        {
            Log.Warn($"Device plugin cancellation callback failed: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            Log.Change(
                "device-plugin-late-cancellation",
                "Device plugin cancellation arrived after its teardown token was disposed.");
        }
    }

    private static void Raise<T>(Action<T>? handlers, T value, string channel)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Change(
                    $"device-plugin-publication-{channel}",
                    $"Device plugin {channel} consumer failed: {ex.Message}");
            }
        }
    }

    private sealed class CommandOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;

        private readonly TaskCompletionSource<CapabilityCommandResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // The deadline has its own source so a cancellation can be attributed: the caller, the
        // runtime's lifetime, or the command's own deadline, which is the one that reads as a
        // timeout rather than an indeterminate result.
        private readonly CancellationTokenSource _deadline;
        private int _disposeStarted;
        private int _started;

        internal CommandOperation(
            CapabilityCommand command,
            CancellationToken caller,
            CancellationToken lifetime)
        {
            Command = command;
            _deadline = command.Deadline.CreateCancellationSource();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                caller,
                lifetime,
                _deadline.Token);
        }

        internal CapabilityCommand Command { get; }
        internal CancellationToken Token => _cancellation.Token;

        /// <summary>Whether the command's own deadline has fired.</summary>
        internal bool DeadlinePassed => _deadline.IsCancellationRequested;

        internal Task<CapabilityCommandResult> Task => _completion.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _cancellation.Dispose();
            _deadline.Dispose();
        }

        internal void Start(Task<CapabilityCommandResult> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("The command already started.");
            }

            _ = CompleteAsync(task);
        }

        internal void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _completion.TrySetResult(FailedCommand(Command, exception));
            }
        }

        internal void Cancel()
        {
            TryCancel(_cancellation);
        }

        private async Task CompleteAsync(Task<CapabilityCommandResult> task)
        {
            try
            {
                _completion.TrySetResult(await task.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetResult(CanceledCommand(Command, DeadlinePassed));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _completion.TrySetResult(FailedCommand(Command, ex));
            }
        }
    }

    private sealed class DirectPluginHostAdapter(
        DevicePluginRuntime owner,
        long cycleGeneration) : IPluginHostAdapter, IDisposable
    {
        private readonly Lock _generationGate = new();
        private long _descriptorGeneration;
        private volatile bool _disposed;
        private long _stateSequence;

        public void Dispose()
        {
            _disposed = true;
        }

        public long CycleGeneration { get; private set; } = cycleGeneration;

        public ValueTask PublishDescriptorsAsync(
            CapabilityDescriptorSet descriptors,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(descriptors);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_generationGate)
            {
                if (descriptors.CycleGeneration != CycleGeneration
                    || descriptors.Generation <= _descriptorGeneration)
                {
                    throw new InvalidOperationException(
                        "Descriptor generations must be current and monotonic.");
                }

                _descriptorGeneration = descriptors.Generation;
            }

            Raise(owner.DescriptorSetReceived, descriptors, "descriptor set");
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishCapabilityStateAsync(
            CapabilityState state,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_generationGate)
            {
                if (state.CycleGeneration != CycleGeneration
                    || state.DescriptorGeneration != _descriptorGeneration)
                {
                    throw new InvalidOperationException(
                        "Capability state belongs to a stale generation.");
                }
            }

            Raise(
                owner.CapabilityStateReceived,
                new CapabilityStateDelta(Interlocked.Increment(ref _stateSequence), state),
                "capability state");
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishPhysicalDevicesAsync(
            IReadOnlyList<PhysicalDeviceIdentity> devices,
            HapticCapabilities? output,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(devices);
            cancellationToken.ThrowIfCancellationRequested();
            Raise(owner.PhysicalIdentitiesReceived, (devices, output), "physical identities");
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishControllerSampleAsync(
            CanonicalControllerSample sample,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            Raise(owner.ControllerSampleReceived, sample, "controller sample");
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishOemControlsAsync(
            IReadOnlyList<OemControlDescriptor> controls,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(controls);
            cancellationToken.ThrowIfCancellationRequested();
            Raise(owner.OemControlsReceived, controls, "OEM controls");
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishOemEventAsync(
            OemControlEvent controlEvent,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(controlEvent);
            cancellationToken.ThrowIfCancellationRequested();
            Raise(owner.OemEventReceived, controlEvent, "OEM event");
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishSettingsManifestAsync(
            PluginSettingsManifest manifest,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(manifest);
            cancellationToken.ThrowIfCancellationRequested();
            if (!manifest.TryValidate(out var error))
            {
                Trace(DeviceTraceLevel.Warn, "settings", $"Settings manifest refused: {error}");
                return ValueTask.CompletedTask;
            }

            Volatile.Write(ref owner._settingsManifest, manifest);
            Raise(owner.SettingsManifestReceived, manifest, "settings manifest");
            return ValueTask.CompletedTask;
        }

        public void Trace(DeviceTraceLevel level, string scope, string message)
        {
            if (!_disposed)
            {
                PluginLogLine.Write("plugin", level, scope, message);
            }
        }

        /// <summary>Routes a plugin's keyed state through the host's own repeat suppression.</summary>
        /// <remarks>
        ///     Without this the plugin channel is the one part of the log that cannot be deduplicated,
        ///     which is exactly where the worst repetition has come from.
        /// </remarks>
        public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
        {
            if (!_disposed)
            {
                PluginLogLine.WriteChange("plugin", level, scope, key, message);
            }
        }

        public void ReportFault(string scope, string message)
        {
            Trace(DeviceTraceLevel.Error, scope, message);
            owner.ReportPluginFault(
                string.IsNullOrWhiteSpace(scope) ? "plugin" : scope,
                string.IsNullOrWhiteSpace(message) ? "No diagnostic detail was supplied." : message);
        }

        internal void SetCycleGeneration(long generation)
        {
            lock (_generationGate)
            {
                if (generation <= CycleGeneration)
                {
                    throw new InvalidOperationException(
                        "Cycle generation must increase before resources are reacquired.");
                }

                CycleGeneration = generation;
                _descriptorGeneration = 0;
                _stateSequence = 0;
            }
        }
    }
}

internal enum DeviceRuntimeExitReason
{
    Intentional,
    BackgroundFault
}

internal sealed record DeviceRuntimeExit(DeviceRuntimeExitReason Reason, string Detail);

internal sealed record DevicePluginState
{
    internal required DeviceCycleState State { get; init; }
    internal required long CycleGeneration { get; init; }
    internal string? DeviceDefinitionId { get; init; }
    internal CapabilityReason? Reason { get; init; }
}

internal sealed record DeviceCommandDispatch(
    CapabilityCommandResult Immediate,
    Task<CapabilityCommandResult>? LateCompletion = null);
