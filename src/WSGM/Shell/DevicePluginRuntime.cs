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
internal sealed class DevicePluginRuntime : IAsyncDisposable
{
    private static readonly TimeSpan EmergencyCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly DirectPluginHostAdapter _adapter;
    private readonly Lock _commandGate = new();
    private readonly Dictionary<Guid, CommandOperation> _commands = [];

    private readonly TaskCompletionSource<DeviceRuntimeExit> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PluginPackageLoader _package;
    private readonly string? _pluginStateRoot;
    private readonly CancellationTokenSource _startCancellation = new();
    private bool _commandAdmissionClosed;
    private DeviceCycleState _cycleState = DeviceCycleState.Disabled;
    private string? _deviceDefinitionId;
    private int _disposeStarted;
    private volatile bool _disposed;
    private bool _pluginStartAttempted;
    private volatile bool _stopped;

    private DevicePluginRuntime(
        PluginPackageLoader package,
        long cycleGeneration,
        string? pluginStateRoot)
    {
        _package = package;
        CycleGeneration = cycleGeneration;
        _pluginStateRoot = pluginStateRoot;
        _adapter = new DirectPluginHostAdapter(this, cycleGeneration);
    }

    internal long CycleGeneration { get; private set; }
    internal string PackageId => Plugin.PackageId;
    internal string StateDirectory => Path.Combine(_pluginStateRoot ?? DefaultPluginStateRoot(), PackageId);

    internal Task<DeviceRuntimeExit> Completion => _completion.Task;

    private IDevicePlugin Plugin => _package.Plugin;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        CloseCommandAdmission();
        TryCancel(_startCancellation);
        CancelCommands();
        List<Exception> failures = [];
        using CancellationTokenSource cleanup = new(EmergencyCleanupBudget);
        try
        {
            await _lifecycleGate.WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new AggregateException(
                "Device plugin disposal was blocked by a lifecycle operation that did not quiesce.",
                new TimeoutException("The device plugin lifecycle gate exceeded the cleanup budget."));
        }

        var canUnload = true;
        try
        {
            _disposed = true;
            var deadline = DateTimeOffset.UtcNow + EmergencyCleanupBudget;
            var commandFailures = await QuiesceCommandsAsync(
                deadline,
                cleanup.Token).ConfigureAwait(false);
            failures.AddRange(commandFailures);
            canUnload = commandFailures.Count == 0;
            if (_pluginStartAttempted && !_stopped)
            {
                try
                {
                    var result = await Plugin.StopAsync(
                        new PluginStopContext(PluginStopReason.WsgmExiting, deadline),
                        cleanup.Token).ConfigureAwait(false);
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
                await Plugin.DisposeAsync().AsTask().WaitAsync(cleanup.Token).ConfigureAwait(false);
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
                    _package.Dispose();
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
            _lifecycleGate.Release();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Device plugin disposal was incomplete.", failures);
        }
    }

    internal event Action<CapabilityDescriptorSet>? DescriptorSetReceived;
    internal event Action<CapabilityStateDelta>? CapabilityStateReceived;
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
        string? pluginStateRoot = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        if (pluginStateRoot is null)
        {
            return LoadAsync(package, cycleGeneration, null, cancellationToken);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pluginStateRoot);
        return LoadAsync(package, cycleGeneration, Path.GetFullPath(pluginStateRoot), cancellationToken);

        static Task<DevicePluginRuntime> LoadAsync(
            InstalledDevicePackage package,
            long cycleGeneration,
            string? stateRoot,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                () => new DevicePluginRuntime(
                    PluginPackageLoader.Load(package),
                    cycleGeneration,
                    stateRoot),
                cancellationToken);
        }
    }

    internal async Task<DevicePluginState> StartAsync(
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

        using var bounded = CreateDeadlineToken(
            DateTimeOffset.UtcNow.AddSeconds(15),
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

    internal async Task<DevicePluginState> SuspendAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        using var bounded = CreateDeadlineToken(
            deadline,
            cancellationToken,
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

    internal async Task<DevicePluginState> ResumeAsync(
        long cycleGeneration,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        using var bounded = CreateDeadlineToken(
            deadline,
            cancellationToken,
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

    internal async Task<DevicePluginState> StopAsync(
        PluginStopReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        CloseCommandAdmission();
        TryCancel(_startCancellation);
        CancelCommands();
        using var bounded = CreateDeadlineToken(
            deadline,
            cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return SnapshotLifecycle();
            }

            var commandFailures = await QuiesceCommandsAsync(
                deadline,
                bounded.Token).ConfigureAwait(false);
            PublishLifecycle(DeviceCycleState.Deactivating, null);
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
                _ => throw new InvalidDataException("Unknown plugin stop status.")
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

    internal async Task<DeviceCommandDispatch> ExecuteCommandAsync(
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
                operation.Start(Plugin.ExecuteCommandAsync(command, operation.Token).AsTask());
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

            _ = RemoveCommandWhenCompleteAsync(operation);
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

    internal Task ApplySettingsValuesAsync(
        IReadOnlyList<DeviceSettingValue> values,
        CancellationToken cancellationToken)
    {
        EnsureOperationAllowed();
        return Plugin.ApplySettingsAsync(values, cancellationToken).AsTask();
    }

    internal Task ApplyHapticOutputAsync(
        HapticOutputFrame output,
        CancellationToken cancellationToken)
    {
        return _cycleState is not (DeviceCycleState.Active or DeviceCycleState.Degraded)
            ? Task.CompletedTask
            : Plugin.ApplyHapticOutputAsync(output, cancellationToken).AsTask();
    }

    internal async Task<ControllerHandoff> ReleaseControllerAsync(
        HandoffScope scope,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (scope is HandoffScope.FullDeactivation)
        {
            CloseCommandAdmission();
            TryCancel(_startCancellation);
            CancelCommands();
        }

        using var bounded = CreateDeadlineToken(
            deadline,
            cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            EnsureLifecycleActive();
            var commandFailures = scope is HandoffScope.FullDeactivation
                ? await QuiesceCommandsAsync(deadline, bounded.Token).ConfigureAwait(false)
                : [];
            var release = await Plugin.ReleaseControllerAsync(
                new PluginControllerReleaseContext(scope, deadline),
                bounded.Token).ConfigureAwait(false);
            if (commandFailures.Count > 0)
            {
                throw new AggregateException(
                    "Plugin commands did not quiesce cleanly before controller release.",
                    commandFailures);
            }

            return new ControllerHandoff
            {
                Step = release.Step,
                Result = release.Result,
                ReleasedDevices = release.ReleasedDevices
            };
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task SetControllerManagementAsync(
        bool enabled,
        long cycleGeneration,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        using var bounded = CreateDeadlineToken(
            deadline,
            cancellationToken,
            _lifetime.Token);
        await _lifecycleGate.WaitAsync(bounded.Token).ConfigureAwait(false);
        try
        {
            EnsureLifecycleOperationAllowed();
            if (enabled)
            {
                if (cycleGeneration <= CycleGeneration)
                {
                    throw new InvalidOperationException(
                        "Controller acquisition requires a fresh device generation.");
                }

                CycleGeneration = cycleGeneration;
                _adapter.SetCycleGeneration(cycleGeneration);
            }

            await Plugin.SetControllerManagementAsync(
                new PluginControllerManagementContext(enabled, cycleGeneration, deadline),
                bounded.Token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // ReSharper disable once UnusedMember.Global
    internal async Task<DeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        EnsureOperationAllowed();
        var diagnostics = await Plugin.GetDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
        return new DeviceDiagnosticsSnapshot
        {
            PackageId = Plugin.PackageId,
            DeviceId = _deviceDefinitionId ?? "unmatched",
            CycleState = _cycleState,
            CycleGeneration = CycleGeneration,
            PluginValues = diagnostics.Values,
            CapturedAt = DateTimeOffset.UtcNow
        };
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
        DateTimeOffset deadline,
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
        using var bounded = CreateDeadlineToken(deadline, cancellationToken);
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

        var detail = message.Length <= PluginTrace.MaxMessageLength
            ? message
            : message[..PluginTrace.MaxMessageLength];
        // Completion is the coordinator's teardown trigger. Close admission first so observing the
        // fault can never race a new hardware write into the cycle that is already being released.
        CloseCommandAdmission();
        TryCancel(_startCancellation);
        CancelCommands();
        Complete(
            DeviceRuntimeExitReason.BackgroundFault,
            $"Plugin background service '{scope}' failed: {detail}");
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
            Outcome = deadlinePassed || DateTimeOffset.UtcNow >= command.Deadline
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

    private static CancellationTokenSource CreateDeadlineToken(
        DateTimeOffset deadline,
        params CancellationToken[] tokens)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(tokens);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            source.Cancel();
        }
        else
        {
            source.CancelAfter(remaining);
        }

        return source;
    }

    private static string CreatePluginStateDirectory(string packageId, string? stateRoot)
    {
        var root = stateRoot ?? DefaultPluginStateRoot();
        var directory = Path.Combine(root, packageId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string DefaultPluginStateRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException(
                "The local application-data directory is unavailable.");
        }

        return Path.Combine(localData, "WSGM", "DeviceState");
    }

    private static string DescribePluginFailure(string operation, Exception exception)
    {
        var detail = $"Plugin {operation} failed ({exception.GetType().Name}): {exception.Message}";
        return detail.Length <= 1200 ? detail : detail[..1200];
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
        private readonly CancellationTokenSource _deadline = new();
        private int _disposeStarted;
        private int _started;

        internal CommandOperation(
            CapabilityCommand command,
            CancellationToken caller,
            CancellationToken lifetime)
        {
            Command = command;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                caller,
                lifetime,
                _deadline.Token);
            var remaining = command.Deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _deadline.Cancel();
            }
            else
            {
                _deadline.CancelAfter(remaining);
            }
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
            ArgumentNullException.ThrowIfNull(sample);
            cancellationToken.ThrowIfCancellationRequested();
            if (sample.CycleGeneration != CycleGeneration)
            {
                throw new InvalidOperationException(
                    "Controller sample belongs to a stale generation.");
            }

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

            Raise(owner.SettingsManifestReceived, manifest, "settings manifest");
            return ValueTask.CompletedTask;
        }

        public void Trace(DeviceTraceLevel level, string scope, string message)
        {
            if (!TryFormat(scope, message, out var line))
            {
                return;
            }

            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (level)
            {
                case DeviceTraceLevel.Info:
                    Log.Info(line);
                    break;
                case DeviceTraceLevel.Warn:
                    Log.Warn(line);
                    break;
                case DeviceTraceLevel.Error:
                    Log.Error(line);
                    break;
                case DeviceTraceLevel.Debug:
                    Log.Debug(line);
                    break;
            }
        }

        /// <summary>Routes a plugin's keyed state through the host's own repeat suppression.</summary>
        /// <remarks>
        ///     Without this the plugin channel is the one part of the log that cannot be deduplicated,
        ///     which is exactly where the worst repetition has come from. The key is namespaced by
        ///     scope so two subsystems cannot collide on a short name like "state".
        /// </remarks>
        public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
        {
            if (!TryFormat(scope, message, out var line))
            {
                return;
            }

            Log.Change(
                $"plugin/{Normalize(scope)}/{(string.IsNullOrWhiteSpace(key) ? "state" : key)}",
                line,
                level switch
                {
                    DeviceTraceLevel.Warn => LogLevel.Warn,
                    DeviceTraceLevel.Error => LogLevel.Error,
                    DeviceTraceLevel.Debug => LogLevel.Debug,
                    _ => LogLevel.Info
                });
        }

        public void ReportFault(string scope, string message)
        {
            Trace(DeviceTraceLevel.Error, scope, message);
            owner.ReportPluginFault(
                string.IsNullOrWhiteSpace(scope) ? "plugin" : scope,
                string.IsNullOrWhiteSpace(message) ? "No diagnostic detail was supplied." : message);
        }

        private static string Normalize(string scope)
        {
            return string.IsNullOrWhiteSpace(scope) ? "plugin" : scope;
        }

        private bool TryFormat(string scope, string message, out string line)
        {
            line = string.Empty;
            if (_disposed || string.IsNullOrEmpty(message))
            {
                return false;
            }

            var text = message.Length <= PluginTrace.MaxMessageLength
                ? message
                : message[..PluginTrace.MaxMessageLength];
            line = $"plugin/{Normalize(scope)}: {text}";
            return true;
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
