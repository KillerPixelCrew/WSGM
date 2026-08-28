using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Ipc;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.DeviceHost;

/// <summary>Owns handshake, one plugin instance, request routing, and bounded disposal.</summary>
internal sealed class DeviceHostSession : IAsyncDisposable
{
    private const uint HelloRequestId = 1;
    private readonly HostArguments _arguments;
    private readonly PluginPackageMetadata _metadata;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _commands = new();
    private readonly ConcurrentDictionary<long, Task> _operations = new();
    private PluginPackageLoader? _package;
    private PluginHostAdapter? _adapter;
    private RecoveryJournalStore? _journal;
    private DeviceFrameStream? _frames;
    private HostWireSender? _sender;
    private ushort _protocolVersion;
    private long _operationSequence;
    private long _deviceGeneration;
    private string? _deviceDefinitionId;
    private DeviceCycleState _cycleState = DeviceCycleState.Disabled;
    private IReadOnlyList<RecoveryJournalEntry> _outstandingJournalEntries = [];
    private bool _disposed;

    public DeviceHostSession(HostArguments arguments, PluginPackageMetadata metadata)
    {
        _arguments = arguments;
        _metadata = metadata;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        using NamedPipeClientStream pipe = DeviceControlPipe.CreateClient(_arguments.PipeName);
        using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);

        _frames = new DeviceFrameStream(pipe);
        _sender = new HostWireSender(_frames);
        await HandshakeAsync(lifetime.Token).ConfigureAwait(false);

        _journal = RecoveryJournalStore.Open(_metadata.PackageId);
        _package = PluginPackageLoader.LoadPlugin(_metadata);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                DeviceFrame? frame = await _frames.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                if (frame.Header.ProtocolVersion != _protocolVersion)
                {
                    throw new InvalidDataException("A frame used a protocol version other than the negotiated version.");
                }

                Dispatch(frame, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Cancel();
            foreach (CancellationTokenSource command in _commands.Values)
            {
                command.Cancel();
            }

            Task[] operations = _operations.Values.ToArray();
            if (operations.Length > 0)
            {
                try
                {
                    await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        foreach (CancellationTokenSource command in _commands.Values)
        {
            command.Cancel();
            command.Dispose();
        }

        if (_package is not null)
        {
            await _package.Plugin.DisposeAsync().ConfigureAwait(false);
            _package.Dispose();
        }

        _adapter?.Dispose();
        if (_journal is not null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
        }

        if (_frames is not null)
        {
            await _frames.DisposeAsync().ConfigureAwait(false);
        }

        _lifecycleGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        DeviceHostHello hello = new()
        {
            MinProtocolVersion = DeviceProtocol.MinSupportedVersion,
            MaxProtocolVersion = DeviceProtocol.MaxSupportedVersion,
            SchemaFingerprint = DeviceProtocol.SchemaFingerprint,
            Nonce = Convert.ToBase64String(_arguments.Nonce),
            PackageId = _metadata.Manifest.Id,
            PackageVersion = _metadata.Manifest.Version,
            RuntimeVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
            SessionId = _arguments.SessionId,
            HostGeneration = _arguments.HostGeneration,
        };
        await Sender.SendAsync(
            DeviceMessageType.Hello,
            HelloRequestId,
            FrameFlags.None,
            hello,
            DeviceWireJsonContext.Default.DeviceHostHello,
            DeviceProtocol.MaxSupportedVersion,
            cancellationToken).ConfigureAwait(false);

        DeviceFrame frame = await Frames.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The coordinator closed before acknowledging the handshake.");
        if (!HostHandshakeValidator.IsExpectedAckEnvelope(
            frame.Header,
            HelloRequestId,
            out string envelopeDetail))
        {
            throw new InvalidDataException(envelopeDetail);
        }

        DeviceHostHelloAck ack = Deserialize(frame, DeviceWireJsonContext.Default.DeviceHostHelloAck);
        if (!HostHandshakeValidator.TryValidateAck(
            frame.Header,
            ack,
            HelloRequestId,
            _metadata.Manifest.Id,
            out ushort protocolVersion,
            out string detail))
        {
            throw new InvalidDataException(detail);
        }

        _protocolVersion = protocolVersion;
    }

    private void Dispatch(DeviceFrame frame, CancellationToken sessionToken)
    {
        bool isRequest = frame.Header.RequestId != 0
            && (frame.Header.Flags & FrameFlags.IsResponse) == 0;
        if (!isRequest)
        {
            throw new InvalidDataException("DeviceHost received an unsolicited response or requestless command.");
        }

        switch (frame.Header.MessageType)
        {
            case DeviceMessageType.Activate:
                StartOperation(() => ActivateAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Suspend:
                StartOperation(() => SuspendAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Resume:
                StartOperation(() => ResumeAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Deactivate:
                StartOperation(() => DeactivateAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Command:
                StartOperation(() => CommandAsync(frame, sessionToken));
                break;
            case DeviceMessageType.CancelCommand:
                StartOperation(() => CancelCommandAsync(frame, sessionToken));
                break;
            case DeviceMessageType.HapticOutput:
                StartOperation(() => HapticOutputAsync(frame, sessionToken));
                break;
            case DeviceMessageType.ControllerHandoff:
                StartOperation(() => ControllerHandoffAsync(frame, sessionToken));
                break;
            case DeviceMessageType.ControllerManagement:
                StartOperation(() => ControllerManagementAsync(frame, sessionToken));
                break;
            case DeviceMessageType.DiagnosticsRequest:
                StartOperation(() => DiagnosticsAsync(frame, sessionToken));
                break;
            default:
                StartOperation(() => SendErrorAsync(
                    frame.Header.RequestId,
                    "unknown-message",
                    $"Message type {(ushort)frame.Header.MessageType} is not accepted by DeviceHost.",
                    recoverable: true,
                    sessionToken));
                break;
        }
    }

    private void StartOperation(Func<Task> operation)
    {
        long id = Interlocked.Increment(ref _operationSequence);
        Task task;
        try
        {
            task = operation();
        }
        catch (Exception ex)
        {
            task = Task.FromException(ex);
        }

        _operations[id] = task;
        _ = ObserveOperationAsync(id, task);
    }

    private async Task ObserveOperationAsync(long id, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _shutdown.Cancel();
        }
        finally
        {
            _operations.TryRemove(id, out _);
        }
    }

    private async Task ActivateAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceActivateRequest request = Deserialize(frame, DeviceWireJsonContext.Default.DeviceActivateRequest);
        await _lifecycleGate.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            if (_cycleState is not DeviceCycleState.Disabled)
            {
                await SendErrorAsync(frame.Header.RequestId, "already-active",
                    "This host already owns a device cycle.", true, sessionToken).ConfigureAwait(false);
                return;
            }

            using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
            PluginDetectionResult detection = await Plugin.DetectAsync(new PluginDetectionContext
            {
                Identity = request.Identity,
                HostGeneration = _arguments.HostGeneration,
            }, bounded.Token).ConfigureAwait(false);
            _deviceGeneration = request.DeviceGeneration;
            _outstandingJournalEntries = MergeOutstandingJournalEntries(
                request.OutstandingJournalEntries,
                Journal.Outstanding);
            if (!detection.Matched || string.IsNullOrWhiteSpace(detection.DeviceDefinitionId))
            {
                _cycleState = DeviceCycleState.Passive;
                await PublishLifecycleAsync(
                    frame.Header.RequestId,
                    detection.Reason,
                    isResponse: true,
                    bounded.Token).ConfigureAwait(false);
                return;
            }

            _deviceDefinitionId = detection.DeviceDefinitionId;
            _cycleState = DeviceCycleState.Detected;
            await PublishLifecycleAsync(0, null, false, bounded.Token).ConfigureAwait(false);
            _cycleState = DeviceCycleState.Activating;
            await PublishLifecycleAsync(0, null, false, bounded.Token).ConfigureAwait(false);

            _adapter = new PluginHostAdapter(
                Sender,
                _protocolVersion,
                _arguments.HostGeneration,
                _deviceGeneration,
                _arguments.StateRingName,
                _arguments.StateEventName,
                Journal);
            if (Journal.CorruptionQuarantined)
            {
                _cycleState = DeviceCycleState.Degraded;
                await SendErrorAsync(
                    frame.Header.RequestId,
                    "recovery-journal-corrupt",
                    "A corrupt recovery journal was quarantined; hardware writes remain blocked.",
                    false,
                    bounded.Token).ConfigureAwait(false);
                return;
            }

            await Plugin.ActivateAsync(new PluginActivationContext
            {
                Host = _adapter,
                HostGeneration = _arguments.HostGeneration,
                DeviceGeneration = _deviceGeneration,
                DeviceDefinitionId = _deviceDefinitionId,
                OutstandingJournalEntries = _outstandingJournalEntries,
                ControllerManagementEnabled = request.ControllerManagementEnabled,
            }, bounded.Token).ConfigureAwait(false);

            bool anyOwned = _adapter.Resources.Values.Any(resource => resource.State is ResourceState.Owned);
            bool anyUnhealthy = _adapter.Resources.Values.Any(resource => resource.State
                is ResourceState.Passive or ResourceState.Degraded or ResourceState.Faulted
                    or ResourceState.ReleasedUnverified);
            _cycleState = anyOwned
                ? anyUnhealthy ? DeviceCycleState.Degraded : DeviceCycleState.Active
                : DeviceCycleState.Passive;
            await PublishLifecycleAsync(
                frame.Header.RequestId,
                null,
                isResponse: true,
                bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            _cycleState = DeviceCycleState.Degraded;
            await SendErrorAsync(frame.Header.RequestId, "activation-timeout",
                "Plugin activation exceeded its deadline.", true, sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _cycleState = DeviceCycleState.Degraded;
            await SendErrorAsync(frame.Header.RequestId, "activation-failed",
                ex.GetType().Name, true, sessionToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task SuspendAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceLifecycleRequest request = Deserialize(frame, DeviceWireJsonContext.Default.DeviceLifecycleRequest);
        await _lifecycleGate.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
            await Plugin.SuspendAsync(new PluginQuiesceContext(request.Deadline), bounded.Token)
                .ConfigureAwait(false);
            _cycleState = DeviceCycleState.Suspended;
            await PublishLifecycleAsync(frame.Header.RequestId, null, true, bounded.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ResumeAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceLifecycleRequest request = Deserialize(frame, DeviceWireJsonContext.Default.DeviceLifecycleRequest);
        if (request.DeviceGeneration is not long generation || generation <= _deviceGeneration)
        {
            await SendErrorAsync(frame.Header.RequestId, "stale-generation",
                "Resume requires a new device generation.", true, sessionToken).ConfigureAwait(false);
            return;
        }

        await _lifecycleGate.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
            _adapter?.SetDeviceGeneration(generation);
            _deviceGeneration = generation;
            _cycleState = DeviceCycleState.Activating;
            await Plugin.ResumeAsync(new PluginResumeContext(generation, request.Deadline), bounded.Token)
                .ConfigureAwait(false);
            _cycleState = DeviceCycleState.Active;
            await PublishLifecycleAsync(frame.Header.RequestId, null, true, bounded.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DeactivateAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceDeactivateRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceDeactivateRequest);
        await _lifecycleGate.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            _cycleState = DeviceCycleState.Deactivating;
            await PublishLifecycleAsync(0, null, false, sessionToken).ConfigureAwait(false);
            using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
            await Plugin.DeactivateAsync(new PluginDeactivationContext(
                MapReason(request.Reason),
                request.Deadline), bounded.Token).ConfigureAwait(false);
            _cycleState = DeviceCycleState.Disabled;
            await PublishLifecycleAsync(frame.Header.RequestId, null, true, sessionToken)
                .ConfigureAwait(false);
            _shutdown.Cancel();
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            await SendErrorAsync(frame.Header.RequestId, "deactivation-timeout",
                "Plugin cleanup exceeded its deadline; restoration is unverified.", false, sessionToken)
                .ConfigureAwait(false);
            _shutdown.Cancel();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CommandAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        CapabilityCommand command = Deserialize(frame, DeviceWireJsonContext.Default.CapabilityCommand);
        if (_cycleState is not (DeviceCycleState.Active or DeviceCycleState.Degraded))
        {
            CapabilityReasonCode code = _cycleState is DeviceCycleState.Suspended
                or DeviceCycleState.Deactivating
                ? CapabilityReasonCode.Quiescing
                : CapabilityReasonCode.HostUnavailable;
            await SendCommandResultAsync(frame.Header.RequestId, new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(code, $"Host state is {_cycleState}.", true),
                CompletedAt = DateTimeOffset.UtcNow,
            }, sessionToken).ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        TimeSpan remaining = command.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            commandCancellation.Cancel();
        }
        else
        {
            commandCancellation.CancelAfter(remaining);
        }

        if (!_commands.TryAdd(command.CommandId, commandCancellation))
        {
            await SendCommandResultAsync(frame.Header.RequestId, new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.ValueOutOfRange,
                    "The command ID is already in flight."),
                CompletedAt = DateTimeOffset.UtcNow,
            }, sessionToken).ConfigureAwait(false);
            return;
        }

        try
        {
            CapabilityCommandResult result = await Plugin.ExecuteCommandAsync(
                command,
                commandCancellation.Token).ConfigureAwait(false);
            await SendCommandResultAsync(frame.Header.RequestId, result, sessionToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendCommandResultAsync(frame.Header.RequestId, new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = DateTimeOffset.UtcNow >= command.Deadline
                    ? CommandOutcome.TimedOut
                    : CommandOutcome.Indeterminate,
                Reason = new CapabilityReason(CapabilityReasonCode.Quiescing,
                    "Command was cancelled before a final hardware result was available."),
                CompletedAt = DateTimeOffset.UtcNow,
            }, sessionToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.TryRemove(command.CommandId, out _);
        }
    }

    private async Task CancelCommandAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceCancelCommandRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceCancelCommandRequest);
        bool found = _commands.TryGetValue(request.CommandId, out CancellationTokenSource? command);
        command?.Cancel();
        await SendAckAsync(frame.Header.RequestId, found,
            found ? null : "The command was not in flight.", sessionToken).ConfigureAwait(false);
    }

    private async Task HapticOutputAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        HapticOutputFrame output = Deserialize(frame, DeviceWireJsonContext.Default.HapticOutputFrame);
        await Plugin.ApplyHapticOutputAsync(output, sessionToken).ConfigureAwait(false);
        await SendAckAsync(frame.Header.RequestId, true, null, sessionToken).ConfigureAwait(false);
    }

    private async Task ControllerHandoffAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceControllerHandoffRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceControllerHandoffRequest);
        using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
        PluginControllerRelease release = await Plugin.ReleaseControllerAsync(
            new PluginControllerReleaseContext(request.Scope, request.Deadline),
            bounded.Token).ConfigureAwait(false);
        DeviceControllerHandoffResponse response = new()
        {
            Step = release.Step,
            Result = release.Result,
            ReleasedDevices = release.ReleasedDevices,
        };
        await Sender.SendAsync(
            DeviceMessageType.ControllerHandoff,
            frame.Header.RequestId,
            FrameFlags.IsResponse,
            response,
            DeviceWireJsonContext.Default.DeviceControllerHandoffResponse,
            _protocolVersion,
            sessionToken).ConfigureAwait(false);
    }

    private async Task ControllerManagementAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceControllerManagementRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceControllerManagementRequest);
        using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
        if (request.Enabled)
        {
            if (request.DeviceGeneration <= _deviceGeneration)
            {
                await SendErrorAsync(frame.Header.RequestId, "stale-generation",
                    "Controller activation requires a fresh device generation.", true, sessionToken)
                    .ConfigureAwait(false);
                return;
            }

            _adapter?.SetDeviceGeneration(request.DeviceGeneration);
            _deviceGeneration = request.DeviceGeneration;
        }

        await Plugin.SetControllerManagementAsync(new PluginControllerManagementContext(
            request.Enabled,
            request.DeviceGeneration,
            request.Deadline), bounded.Token).ConfigureAwait(false);
        await SendAckAsync(frame.Header.RequestId, true, null, sessionToken).ConfigureAwait(false);
    }

    private async Task DiagnosticsAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        _ = Deserialize(frame, DeviceWireJsonContext.Default.DeviceDiagnosticsRequest);
        Dictionary<string, ResourceState> resources = _adapter?.Resources.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.State,
            StringComparer.Ordinal) ?? new Dictionary<string, ResourceState>(StringComparer.Ordinal);
        DeviceDiagnosticsSnapshot snapshot = new()
        {
            SchemaVersion = 1,
            PackageId = _metadata.Manifest.Id,
            DeviceId = _deviceDefinitionId ?? "unmatched",
            TrustTier = _arguments.TrustTier,
            CycleState = _cycleState,
            HostGeneration = _arguments.HostGeneration,
            DeviceGeneration = _deviceGeneration,
            Resources = resources,
            OutstandingJournalEntries = _outstandingJournalEntries,
            CapturedAt = DateTimeOffset.UtcNow,
        };
        await Sender.SendAsync(
            DeviceMessageType.DiagnosticsSnapshot,
            frame.Header.RequestId,
            FrameFlags.IsResponse,
            snapshot,
            DeviceWireJsonContext.Default.DeviceDiagnosticsSnapshot,
            _protocolVersion,
            sessionToken).ConfigureAwait(false);
    }

    private Task PublishLifecycleAsync(
        uint requestId,
        CapabilityReason? reason,
        bool isResponse,
        CancellationToken cancellationToken)
    {
        DeviceLifecycleNotification notification = new()
        {
            State = _cycleState,
            HostGeneration = _arguments.HostGeneration,
            DeviceGeneration = _deviceGeneration,
            DeviceDefinitionId = _deviceDefinitionId,
            Reason = reason,
        };
        return Sender.SendAsync(
            DeviceMessageType.LifecycleState,
            requestId,
            isResponse ? FrameFlags.IsResponse : FrameFlags.None,
            notification,
            DeviceWireJsonContext.Default.DeviceLifecycleNotification,
            _protocolVersion,
            cancellationToken).AsTask();
    }

    private Task SendCommandResultAsync(
        uint requestId,
        CapabilityCommandResult result,
        CancellationToken cancellationToken) => Sender.SendAsync(
            DeviceMessageType.OperationAck,
            requestId,
            FrameFlags.IsResponse,
            result,
            DeviceWireJsonContext.Default.CapabilityCommandResult,
            _protocolVersion,
            cancellationToken).AsTask();

    private Task SendAckAsync(
        uint requestId,
        bool completed,
        string? detail,
        CancellationToken cancellationToken) => Sender.SendAsync(
            DeviceMessageType.CommandResult,
            requestId,
            FrameFlags.IsResponse,
            new DeviceOperationAck { Completed = completed, Detail = detail },
            DeviceWireJsonContext.Default.DeviceOperationAck,
            _protocolVersion,
            cancellationToken).AsTask();

    private Task SendErrorAsync(
        uint requestId,
        string code,
        string detail,
        bool recoverable,
        CancellationToken cancellationToken) => Sender.SendAsync(
            DeviceMessageType.Error,
            requestId,
            FrameFlags.IsResponse,
            new DeviceProtocolError
            {
                Code = code,
                Detail = detail,
                Recoverable = recoverable,
            },
            DeviceWireJsonContext.Default.DeviceProtocolError,
            _protocolVersion,
            cancellationToken).AsTask();

    private static T Deserialize<T>(DeviceFrame frame, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(frame.Payload, typeInfo)
                ?? throw new InvalidDataException("A semantic payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A semantic payload was malformed.", ex);
        }
    }

    private static CancellationTokenSource DeadlineToken(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
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

    private static PluginDeactivationReason MapReason(DeviceDeactivationReason reason) => reason switch
    {
        DeviceDeactivationReason.WsgmExiting => PluginDeactivationReason.WsgmExiting,
        DeviceDeactivationReason.IntegrationDisabled => PluginDeactivationReason.IntegrationDisabled,
        DeviceDeactivationReason.Updating => PluginDeactivationReason.Updating,
        DeviceDeactivationReason.SessionEnding => PluginDeactivationReason.SessionEnding,
        _ => throw new InvalidDataException("Unknown deactivation reason."),
    };

    private RecoveryJournalStore Journal => _journal
        ?? throw new InvalidOperationException("Recovery journal is not initialized.");

    private static IReadOnlyList<RecoveryJournalEntry> MergeOutstandingJournalEntries(
        IReadOnlyList<RecoveryJournalEntry> coordinatorEntries,
        IReadOnlyList<RecoveryJournalEntry> hostEntries) =>
        coordinatorEntries.Concat(hostEntries)
            .GroupBy(entry => $"{entry.PackageId}:{entry.Sequence}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(entry => entry.Status).First())
            .OrderByDescending(entry => entry.Sequence)
            .Take(1000)
            .ToArray();

    private IDevicePlugin Plugin => _package?.Plugin
        ?? throw new InvalidOperationException("Plugin has not been loaded.");

    private DeviceFrameStream Frames => _frames
        ?? throw new InvalidOperationException("Control stream has not been connected.");

    private HostWireSender Sender => _sender
        ?? throw new InvalidOperationException("Control stream has not been connected.");
}
