using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     The capability host WSGM gives one common plugin instance that publishes capabilities, and the
///     stream its capability router reads.
/// </summary>
/// <remarks>
///     The plugin registration owns the cycle: it begins a new cycle generation before the plugin starts
///     and before every resume, and closes command admission before a suspend or a stop. The channel checks
///     what the plugin publishes the way the device runtime's adapter does, generations and declared roles
///     first, before any of it reaches WSGM state, and it is the one path commands and per-application
///     syncs reach the plugin by.
/// </remarks>
internal sealed class PluginCapabilityChannel : ICapabilityHost, ICapabilityPublisher, IDisposable
{
    private readonly HashSet<CapabilityRole> _declared;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ICapabilityPlugin _plugin;
    private bool _closed;
    private long _cycleGeneration;
    private long _descriptorGeneration;
    private bool _open;
    private long _stateSequence;

    /// <param name="identity">The plugin instance.</param>
    /// <param name="declared">The capability roles its manifest declares.</param>
    /// <param name="plugin">The plugin's capability surface.</param>
    internal PluginCapabilityChannel(
        PluginInstanceIdentity identity,
        IReadOnlyList<CapabilityRole> declared,
        ICapabilityPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(declared);
        Identity = identity;
        DeclaredCapabilities = [.. declared];
        _declared = [.. declared];
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
    }

    /// <summary>The plugin instance this channel belongs to.</summary>
    internal PluginInstanceIdentity Identity { get; }

    /// <summary>Whether commands and syncs are admitted now.</summary>
    internal bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    public void Dispose()
    {
        Close();
        _lifetime.Dispose();
    }

    public long CycleGeneration
    {
        get
        {
            lock (_gate)
            {
                return _cycleGeneration;
            }
        }
    }

    public ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (descriptors.CycleGeneration != _cycleGeneration
                || descriptors.Generation <= _descriptorGeneration)
            {
                throw new InvalidOperationException("Descriptor generations must be current and monotonic.");
            }

            // The manifest's capability list is what setup offered the package for, so a role it does
            // not declare is a package defect and the set is refused whole.
            if (descriptors.Descriptors.FirstOrDefault(descriptor => !_declared.Contains(descriptor.Role)) is
                { } undeclared)
            {
                throw new InvalidOperationException(
                    $"Capability {undeclared.CapabilityId} uses role {undeclared.Role}, which the package "
                    + "manifest does not declare.");
            }

            _descriptorGeneration = descriptors.Generation;
        }

        Raise(DescriptorSetReceived, descriptors, "descriptor set");
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        long sequence;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (state.CycleGeneration != _cycleGeneration || state.DescriptorGeneration != _descriptorGeneration)
            {
                throw new InvalidOperationException("Capability state belongs to a stale generation.");
            }

            sequence = ++_stateSequence;
        }

        Raise(CapabilityStateReceived, new CapabilityStateDelta(sequence, state), "capability state");
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
            case DeviceTraceLevel.Warn:
                Log.Warn(line);
                break;
            case DeviceTraceLevel.Error:
                Log.Error(line);
                break;
            case DeviceTraceLevel.Debug:
                Log.Debug(line);
                break;
            default:
                Log.Info(line);
                break;
        }
    }

    public void TraceChange(DeviceTraceLevel level, string scope, string key, string message)
    {
        if (!TryFormat(scope, message, out var line))
        {
            return;
        }

        Log.Change(
            $"plugin/{Identity.PluginId}/{Normalize(scope)}/{(string.IsNullOrWhiteSpace(key) ? "state" : key)}",
            line,
            level switch
            {
                DeviceTraceLevel.Warn => LogLevel.Warn,
                DeviceTraceLevel.Error => LogLevel.Error,
                DeviceTraceLevel.Debug => LogLevel.Debug,
                _ => LogLevel.Info
            });
    }

    public IReadOnlyList<CapabilityRole> DeclaredCapabilities { get; }

    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;

    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    public async Task<DeviceCommandDispatch> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        CancellationToken lifetime;
        lock (_gate)
        {
            if (!_open)
            {
                return new DeviceCommandDispatch(Refused(command, CapabilityReasonCode.Quiescing,
                    "The graphics plugin is not accepting commands."));
            }

            if (command.ExpectedCycleGeneration != _cycleGeneration)
            {
                return new DeviceCommandDispatch(Refused(command, CapabilityReasonCode.GenerationChanged,
                    "The command belongs to an earlier cycle."));
            }

            lifetime = _lifetime.Token;
        }

        var remaining = command.Deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            return new DeviceCommandDispatch(Canceled(command));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        budget.CancelAfter(remaining);
        Task<CapabilityCommandResult> work;
        try
        {
            work = CompleteAsync(_plugin.ExecuteCommandAsync(command, budget.Token).AsTask(), command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new DeviceCommandDispatch(Failed(command, ex));
        }

        try
        {
            return new DeviceCommandDispatch(await work.WaitAsync(budget.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The plugin may still be writing. Its eventual result is handed back as the late completion,
            // and nothing here writes a second time.
            return work.IsCompleted
                ? new DeviceCommandDispatch(await work.ConfigureAwait(false))
                : new DeviceCommandDispatch(Canceled(command), work);
        }
    }

    /// <summary>Raised on the lifecycle lane when a new cycle generation began, before the plugin runs.</summary>
    internal event Action<long>? CycleStarted;

    /// <summary>Raised when command admission closed for a suspend, a stop or the channel's end.</summary>
    internal event Action? AdmissionClosed;

    /// <summary>Begins a new cycle generation and opens command admission.</summary>
    /// <param name="generation">The new generation; it must exceed the current one.</param>
    internal void BeginCycle(long generation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (generation <= _cycleGeneration)
            {
                throw new InvalidOperationException("Cycle generation must increase before the plugin republishes.");
            }

            _cycleGeneration = generation;
            _descriptorGeneration = 0;
            _stateSequence = 0;
            _open = true;
        }

        CycleStarted?.Invoke(generation);
    }

    /// <summary>Closes command admission until the next cycle begins.</summary>
    internal void Suspend()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
        }

        AdmissionClosed?.Invoke();
    }

    /// <summary>Ends the channel: admission closes for good and in-flight commands are canceled.</summary>
    internal void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _open = false;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        AdmissionClosed?.Invoke();
    }

    /// <summary>Hands the plugin every game's native per-application values.</summary>
    /// <param name="sync">The complete set.</param>
    /// <param name="budget">How long the plugin may take.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The plugin's result, or null when admission is closed.</returns>
    /// <exception cref="OperationCanceledException">The budget ran out or the caller canceled.</exception>
    internal async Task<ApplicationProfileSyncResult?> SyncApplicationProfilesAsync(
        ApplicationProfileSync sync,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sync);
        CancellationToken lifetime;
        lock (_gate)
        {
            if (!_open)
            {
                return null;
            }

            lifetime = _lifetime.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        linked.CancelAfter(budget);
        return await _plugin.SyncApplicationProfilesAsync(sync, linked.Token).AsTask().WaitAsync(linked.Token)
            .ConfigureAwait(false);
    }

    private static async Task<CapabilityCommandResult> CompleteAsync(
        Task<CapabilityCommandResult> task,
        CapabilityCommand command)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Canceled(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed(command, ex);
        }
    }

    private static CapabilityCommandResult Refused(CapabilityCommand command, CapabilityReasonCode code,
        string detail)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(code, detail, true),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static CapabilityCommandResult Canceled(CapabilityCommand command)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = command.Deadline.HasExpired ? CommandOutcome.TimedOut : CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The command was canceled before the plugin produced a final result."),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static CapabilityCommandResult Failed(CapabilityCommand command, Exception exception)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                exception.Message.Length <= 256 ? exception.Message : exception.Message[..256]),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static string Normalize(string scope)
    {
        return string.IsNullOrWhiteSpace(scope) ? "plugin" : scope;
    }

    private bool TryFormat(string scope, string message, out string line)
    {
        line = string.Empty;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }
        }

        var text = message.Length <= PluginTrace.MaxMessageLength
            ? message
            : message[..PluginTrace.MaxMessageLength];
        line = $"plugin/{Identity.PluginId}/{Normalize(scope)}: {text}";
        return true;
    }

    private void Raise<T>(Action<T>? handler, T value, string channel)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Change($"plugin/{Identity.PluginId}/publication-{channel}",
                $"Graphics plugin {Identity.PluginId} {channel} consumer failed: {ex.Message}");
        }
    }
}
