using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     The capability host WSGM gives one common plugin instance that publishes capabilities, and the
///     stream its capability router reads.
/// </summary>
/// <remarks>
///     The plugin registration opens and closes admission around its serialized lifecycle.
///     Publications must use declared roles. Commands and per-application syncs reach the plugin
///     through this owned channel; capability generations are not part of the contract.
/// </remarks>
internal sealed class PluginCapabilityChannel : ICapabilityHost, ICapabilityPublisher, IDisposable
{
    private readonly HashSet<CapabilityRole> _declared;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ICapabilityPlugin _plugin;
    private bool _closed;


    private bool _open;
    private long _stateSequence;

    /// <summary>Creates one instance's capability channel from its admitted manifest roles and borrowed plugin surface.</summary>
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

    /// <summary>Whether the channel has ended for good.</summary>
    internal bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            // The manifest's capability list is what setup offered the package for, so a role it does
            // not declare is a package defect and the set is refused whole.
            if (descriptors.Descriptors.FirstOrDefault(descriptor => !_declared.Contains(descriptor.Role)) is
                { } undeclared)
            {
                throw new InvalidOperationException(
                    $"Capability {undeclared.CapabilityId} uses role {undeclared.Role}, which the package "
                    + "manifest does not declare.");
            }
        }

        Raise(DescriptorSetReceived, descriptors, "descriptor set");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        long sequence;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            sequence = ++_stateSequence;
        }

        Raise(CapabilityStateReceived, new CapabilityStateDelta(sequence, state), "capability state");
        return ValueTask.CompletedTask;
    }

    public bool IsActive => IsOpen;

    /// <inheritdoc />
    public IReadOnlyList<CapabilityRole> DeclaredCapabilities { get; }

    /// <inheritdoc />
    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;

    /// <inheritdoc />
    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    /// <inheritdoc />
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

            lifetime = _lifetime.Token;
        }

        if (command.Deadline.HasExpired)
        {
            return new DeviceCommandDispatch(Canceled(command));
        }

        // Active time, so a sleep in the middle of a command does not spend its budget.
        using var budget = command.Deadline.CreateCancellationSource(cancellationToken, lifetime);
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

    /// <inheritdoc />
    public void Dispose()
    {
        Close();
        _lifetime.Dispose();
    }

    /// <summary>Raised when the owned publisher opens before the plugin runs.</summary>
    internal event Action? Opened;

    /// <summary>Raised when command admission closes.</summary>
    internal event Action? AdmissionClosed;

    internal void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _stateSequence = 0;
            _open = true;
        }

        Opened?.Invoke();
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
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, exception.Message),
            CompletedAt = DateTimeOffset.UtcNow
        };
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
