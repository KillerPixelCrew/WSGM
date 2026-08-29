using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.DeviceHost;

/// <summary>Owns handshake, one plugin instance, request routing, and bounded disposal.</summary>
internal sealed class DeviceHostSession : IAsyncDisposable
{
    private const uint HelloRequestId = 1;
    private static readonly TimeSpan DisconnectCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly HostArguments _arguments;
    private readonly PluginPackageMetadata _metadata;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly DeviceStartCancellationGate _startCancellation = new();
    private readonly DeviceCommandRegistry _commands = new();
    private readonly ConcurrentDictionary<long, Task> _operations = new();
    private PluginPackageLoader? _package;
    private PluginHostAdapter? _adapter;
    private DeviceFrameStream? _frames;
    private HostWireSender? _sender;
    private ushort _protocolVersion;
    private long _operationSequence;
    private long _cycleGeneration;
    private string? _deviceDefinitionId;
    private DeviceCycleState _cycleState = DeviceCycleState.Disabled;
    private bool _pluginStartAttempted;
    private bool _disposed;

    public DeviceHostSession(HostArguments arguments, PluginPackageMetadata metadata)
    {
        _arguments = arguments;
        _metadata = metadata;
        _cycleGeneration = arguments.CycleGeneration;
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
            await RunDisconnectTeardownAsync(
                _shutdown.Cancel,
                CancelInFlightCommands,
                WaitForInFlightOperationsAsync,
                StopAfterCoordinatorDisconnectAsync).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception> failures = [];
        RetainCleanupFailure(failures, "session cancellation", _shutdown.Cancel);
        RetainCleanupFailure(failures, "command cancellation", CancelInFlightCommands);
        if (_package is not null)
        {
            await RetainCleanupFailureAsync(
                failures,
                "plugin disposal",
                _package.Plugin.DisposeAsync).ConfigureAwait(false);
            RetainCleanupFailure(failures, "plugin load-context disposal", _package.Dispose);
        }

        RetainCleanupFailure(failures, "host adapter disposal", () => _adapter?.Dispose());
        if (_frames is not null)
        {
            await RetainCleanupFailureAsync(
                failures,
                "frame transport disposal",
                _frames.DisposeAsync).ConfigureAwait(false);
        }

        RetainCleanupFailure(failures, "lifecycle gate disposal", _lifecycleGate.Dispose);
        RetainCleanupFailure(failures, "shutdown token disposal", _shutdown.Dispose);
        if (failures.Count > 0)
        {
            throw new AggregateException("DeviceHost session disposal was incomplete.", failures);
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        DeviceHostHello hello = new()
        {
            Nonce = Convert.ToBase64String(_arguments.Nonce),
            PackageId = _metadata.Manifest.Id,
            PackageVersion = _metadata.Manifest.Version,
            RuntimeVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
            SessionId = _arguments.SessionId,
            CycleGeneration = _arguments.CycleGeneration,
        };
        await Sender.SendAsync(
            DeviceMessageType.Hello,
            HelloRequestId,
            FrameFlags.None,
            hello,
            DeviceWireJsonContext.Default.DeviceHostHello,
            DeviceProtocol.Version,
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
            out string detail))
        {
            throw new InvalidDataException(detail);
        }

        _protocolVersion = DeviceProtocol.Version;
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
            case DeviceMessageType.Start:
                StartOperation(() => StartAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Suspend:
                StartOperation(() => SuspendAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Resume:
                StartOperation(() => ResumeAsync(frame, sessionToken));
                break;
            case DeviceMessageType.Stop:
                StartOperation(() => StopAsync(frame, sessionToken));
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

    private async Task StartAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceStartRequest request = Deserialize(frame, DeviceWireJsonContext.Default.DeviceStartRequest);
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
            using IDisposable startRegistration = _startCancellation.Register(bounded);
            if (request.CycleGeneration != _cycleGeneration)
            {
                await SendErrorAsync(frame.Header.RequestId, "stale-generation",
                    "Start request does not match the launched cycle generation.", false, sessionToken)
                    .ConfigureAwait(false);
                return;
            }

            PluginDetectionResult detection = await Plugin.DetectAsync(new PluginDetectionContext
            {
                Identity = request.Identity,
            }, bounded.Token).ConfigureAwait(false);
            bounded.Token.ThrowIfCancellationRequested();
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
                _cycleGeneration,
                _arguments.StateRingName,
                _arguments.StateEventName);

            // From this point a plugin may have acquired hardware even if StartAsync later throws.
            // A lost coordinator must therefore run StopAsync before unloading the assembly.
            _pluginStartAttempted = true;
            PluginStartResult start = await Plugin.StartAsync(new PluginStartContext
            {
                Host = _adapter,
                CycleGeneration = _cycleGeneration,
                DeviceDefinitionId = _deviceDefinitionId,
                StateDirectory = CreatePluginStateDirectory(_metadata.Manifest.Id),
                ControllerManagementEnabled = request.ControllerManagementEnabled,
            }, bounded.Token).ConfigureAwait(false);

            _cycleState = MapOperationalState(start.State);
            await PublishLifecycleAsync(
                frame.Header.RequestId,
                start.Reason,
                isResponse: true,
                bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested
            && _startCancellation.TerminalStopRequested)
        {
            // The terminal handoff owns the response path now. Release the lifecycle gate without
            // spending cleanup time on a late Start response the coordinator no longer awaits.
            _cycleState = DeviceCycleState.Deactivating;
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            _cycleState = DeviceCycleState.Degraded;
            await SendErrorAsync(frame.Header.RequestId, "start-timeout",
                "Plugin startup exceeded its deadline.", true, sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _cycleState = DeviceCycleState.Degraded;
            await SendErrorAsync(frame.Header.RequestId, "start-failed",
                DescribeStartFailure(ex), true, sessionToken).ConfigureAwait(false);
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
        if (request.CycleGeneration is not long generation || generation <= _cycleGeneration)
        {
            await SendErrorAsync(frame.Header.RequestId, "stale-generation",
                "Resume requires a new device generation.", true, sessionToken).ConfigureAwait(false);
            return;
        }

        await _lifecycleGate.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
            _adapter?.SetCycleGeneration(generation);
            _cycleGeneration = generation;
            _cycleState = DeviceCycleState.Activating;
            PluginStartResult resumed = await Plugin.ResumeAsync(
                new PluginResumeContext(generation, request.Deadline), bounded.Token)
                .ConfigureAwait(false);
            _cycleState = MapOperationalState(resumed.State);
            await PublishLifecycleAsync(frame.Header.RequestId, resumed.Reason, true, bounded.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private Task StopAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceStopRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceStopRequest);
        Task<IReadOnlyList<Exception>> commandQuiescence =
            _commands.CloseAdmissionAndCancelAsync();
        return RunTerminalLifecycleAfterStartAsync(
            _startCancellation,
            _lifecycleGate,
            cancellationToken => RunTerminalActionAfterCommandQuiescenceAsync(
                commandQuiescence,
                token => StopUnderLifecycleGateAsync(frame, request, token),
                cancellationToken),
            sessionToken);
    }

    private async Task StopUnderLifecycleGateAsync(
        DeviceFrame frame,
        DeviceStopRequest request,
        CancellationToken sessionToken)
    {
        try
        {
            _cycleState = DeviceCycleState.Deactivating;
            await PublishLifecycleAsync(0, null, false, sessionToken).ConfigureAwait(false);
            using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
            PluginStopResult stopped = await Plugin.StopAsync(new PluginStopContext(
                MapReason(request.Reason),
                request.Deadline), bounded.Token).ConfigureAwait(false);
            _pluginStartAttempted = false;
            _cycleState = DeviceCycleState.Disabled;
            CapabilityReason? stopReason = stopped.Status switch
            {
                PluginStopStatus.Clean => null,
                PluginStopStatus.Unverified => stopped.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Plugin cleanup completed without verified restoration."),
                PluginStopStatus.Failed => stopped.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Plugin cleanup failed."),
                _ => throw new InvalidDataException("Unknown plugin stop status."),
            };
            await PublishLifecycleAsync(frame.Header.RequestId, stopReason, true, sessionToken)
                .ConfigureAwait(false);
            _shutdown.Cancel();
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            await SendErrorAsync(frame.Header.RequestId, "stop-timeout",
                "Plugin cleanup exceeded its deadline; restoration is unverified.", false, sessionToken)
                .ConfigureAwait(false);
            _shutdown.Cancel();
        }
    }

    private async Task StopAfterCoordinatorDisconnectAsync()
    {
        using CancellationTokenSource cleanup = new(DisconnectCleanupBudget);
        await RunDisconnectCleanupAfterLifecycleAsync(
            _lifecycleGate,
            () => NeedsDisconnectCleanup(_pluginStartAttempted, _cycleState),
            async cancellationToken =>
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow + DisconnectCleanupBudget;
                _cycleState = DeviceCycleState.Deactivating;
                PluginStopResult stopped = await Plugin.StopAsync(
                    new PluginStopContext(PluginStopReason.WsgmExiting, deadline),
                    cancellationToken).ConfigureAwait(false);
                _pluginStartAttempted = false;
                _cycleState = DeviceCycleState.Disabled;
                if (stopped.Status is not PluginStopStatus.Clean)
                {
                    throw new InvalidOperationException(
                        "Plugin cleanup after coordinator disconnect was not verified clean: "
                            + $"{stopped.Status}; {stopped.Reason?.Detail ?? "no detail"}.");
                }
            },
            cleanup.Token).ConfigureAwait(false);
    }

    internal static bool NeedsDisconnectCleanup(
        bool pluginStartAttempted,
        DeviceCycleState cycleState) => pluginStartAttempted
        && cycleState is not DeviceCycleState.Disabled;

    internal static async Task RunDisconnectCleanupAfterLifecycleAsync(
        SemaphoreSlim lifecycleGate,
        Func<bool> needsCleanup,
        Func<CancellationToken, Task> cleanupAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleGate);
        ArgumentNullException.ThrowIfNull(needsCleanup);
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (needsCleanup())
            {
                await cleanupAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
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

        using var commandCancellation = new DeviceCommandCancellation(
            sessionToken,
            command.Deadline);

        if (!_commands.TryAdd(
                command.CommandId,
                commandCancellation,
                out bool terminalAdmissionClosed))
        {
            await SendCommandResultAsync(frame.Header.RequestId, new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(
                    terminalAdmissionClosed
                        ? CapabilityReasonCode.Quiescing
                        : CapabilityReasonCode.ValueOutOfRange,
                    terminalAdmissionClosed
                        ? "DeviceHost is quiescing for terminal cleanup."
                        : "The command ID is already in flight.",
                    Retryable: terminalAdmissionClosed),
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
            commandCancellation.Complete();
            _commands.Remove(command.CommandId);
        }
    }

    private async Task CancelCommandAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceCancelCommandRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceCancelCommandRequest);
        bool found = _commands.TryGet(request.CommandId, out DeviceCommandCancellation? command);
        bool cancellationRequested = command?.TryCancel() is true;
        Exception? callbackFailure = command?.TakeCancellationFailure();
        bool completed = found && cancellationRequested && callbackFailure is null;
        string? detail = !found || !cancellationRequested
            ? "The command was not in flight."
            : callbackFailure is null
                ? null
                : $"The plugin cancellation callback failed ({callbackFailure.GetType().Name}).";
        await SendAckAsync(frame.Header.RequestId, completed, detail, sessionToken).ConfigureAwait(false);
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
        bool terminalHandoff = request.Scope is HandoffScope.FullDeactivation;
        using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
        async Task ReleaseAndRespondAsync(CancellationToken cancellationToken)
        {
            PluginControllerRelease release = await Plugin.ReleaseControllerAsync(
                new PluginControllerReleaseContext(request.Scope, request.Deadline),
                cancellationToken).ConfigureAwait(false);
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

        if (terminalHandoff)
        {
            // A full handoff is the first frame in terminal coordinator cleanup. Cancel any
            // in-flight command and plugin start immediately, then serialize release behind both
            // unwinds so Stop can acquire the same lifecycle gate within the cleanup deadline.
            Task<IReadOnlyList<Exception>> commandQuiescence =
                _commands.CloseAdmissionAndCancelAsync();
            await RunTerminalLifecycleAfterStartAsync(
                _startCancellation,
                _lifecycleGate,
                cancellationToken => RunTerminalActionAfterCommandQuiescenceAsync(
                    commandQuiescence,
                    ReleaseAndRespondAsync,
                    cancellationToken),
                bounded.Token).ConfigureAwait(false);
            return;
        }

        await ReleaseAndRespondAsync(bounded.Token).ConfigureAwait(false);
    }

    internal static async Task RunTerminalLifecycleAfterStartAsync(
        DeviceStartCancellationGate startCancellation,
        SemaphoreSlim lifecycleGate,
        Func<CancellationToken, Task> terminalAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startCancellation);
        ArgumentNullException.ThrowIfNull(lifecycleGate);
        ArgumentNullException.ThrowIfNull(terminalAction);
        Exception? cancellationFailure = startCancellation.RequestTerminalStop();
        Exception? terminalFailure = null;
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await terminalAction(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                terminalFailure = ex;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (cancellationFailure is not null && terminalFailure is not null)
        {
            throw new AggregateException(
                "Plugin start cancellation and terminal lifecycle both failed.",
                cancellationFailure,
                terminalFailure);
        }
        if (terminalFailure is not null)
        {
            ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }
        if (cancellationFailure is not null)
        {
            throw new InvalidOperationException(
                "Plugin start cancellation callback failed after terminal cleanup completed.",
                cancellationFailure);
        }
    }

    internal static async Task RunTerminalActionAfterCommandQuiescenceAsync(
        Task<IReadOnlyList<Exception>> commandQuiescence,
        Func<CancellationToken, Task> terminalAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandQuiescence);
        ArgumentNullException.ThrowIfNull(terminalAction);
        IReadOnlyList<Exception> cancellationFailures = await commandQuiescence
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        List<Exception> failures = [.. cancellationFailures];

        try
        {
            await terminalAction(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Command quiescence and terminal lifecycle were not both verified.",
                failures);
        }
    }

    private void CancelInFlightCommands() => _commands.CancelAll();

    private async Task WaitForInFlightOperationsAsync()
    {
        Task[] operations = _operations.Values.ToArray();
        if (operations.Length > 0)
        {
            await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    internal static async Task RunDisconnectTeardownAsync(
        Action cancelSession,
        Action cancelCommands,
        Func<Task> waitForOperationsAsync,
        Func<Task> stopAfterDisconnectAsync)
    {
        ArgumentNullException.ThrowIfNull(cancelSession);
        ArgumentNullException.ThrowIfNull(cancelCommands);
        ArgumentNullException.ThrowIfNull(waitForOperationsAsync);
        ArgumentNullException.ThrowIfNull(stopAfterDisconnectAsync);
        List<Exception> failures = [];
        RetainCleanupFailure(failures, "disconnect cancellation", cancelSession);
        RetainCleanupFailure(failures, "in-flight command cancellation", cancelCommands);
        await RetainCleanupFailureAsync(
            failures,
            "in-flight operation completion",
            () => new ValueTask(waitForOperationsAsync())).ConfigureAwait(false);
        await RetainCleanupFailureAsync(
            failures,
            "plugin stop after coordinator disconnect",
            () => new ValueTask(stopAfterDisconnectAsync())).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new AggregateException("DeviceHost disconnect teardown was incomplete.", failures);
        }
    }

    private static void RetainCleanupFailure(
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
            failures.Add(new InvalidOperationException($"DeviceHost {operation} failed.", ex));
        }
    }

    private static async ValueTask RetainCleanupFailureAsync(
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
            failures.Add(new InvalidOperationException($"DeviceHost {operation} failed.", ex));
        }
    }

    private async Task ControllerManagementAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        DeviceControllerManagementRequest request = Deserialize(
            frame,
            DeviceWireJsonContext.Default.DeviceControllerManagementRequest);
        using CancellationTokenSource bounded = DeadlineToken(request.Deadline, sessionToken);
        if (request.Enabled)
        {
            if (request.CycleGeneration <= _cycleGeneration)
            {
                await SendErrorAsync(frame.Header.RequestId, "stale-generation",
                    "Controller acquisition requires a fresh cycle generation.", true, sessionToken)
                    .ConfigureAwait(false);
                return;
            }

            _adapter?.SetCycleGeneration(request.CycleGeneration);
            _cycleGeneration = request.CycleGeneration;
        }

        await Plugin.SetControllerManagementAsync(new PluginControllerManagementContext(
            request.Enabled,
            request.CycleGeneration,
            request.Deadline), bounded.Token).ConfigureAwait(false);
        await SendAckAsync(frame.Header.RequestId, true, null, sessionToken).ConfigureAwait(false);
    }

    private async Task DiagnosticsAsync(DeviceFrame frame, CancellationToken sessionToken)
    {
        _ = Deserialize(frame, DeviceWireJsonContext.Default.DeviceDiagnosticsRequest);
        PluginDiagnostics diagnostics = await Plugin.GetDiagnosticsAsync(sessionToken)
            .ConfigureAwait(false);
        DeviceDiagnosticsSnapshot snapshot = new()
        {
            PackageId = _metadata.Manifest.Id,
            DeviceId = _deviceDefinitionId ?? "unmatched",
            CycleState = _cycleState,
            CycleGeneration = _cycleGeneration,
            PluginValues = diagnostics.Values,
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
            CycleGeneration = _cycleGeneration,
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
            DeviceMessageType.CommandResult,
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
            DeviceMessageType.OperationAck,
            requestId,
            FrameFlags.IsResponse,
            new DeviceOperationAck { Completed = completed, Detail = detail },
            DeviceWireJsonContext.Default.DeviceOperationAck,
            _protocolVersion,
            cancellationToken).AsTask();

    /// <summary>Describes a plugin start failure well enough to diagnose it from a pasted log.</summary>
    /// <param name="ex">The exception the plugin's start threw.</param>
    /// <returns>Bounded text naming the failure and what it was about.</returns>
    /// <remarks>
    /// The type name alone is not a diagnosis. A bare "DllNotFoundException" reached a user's log and
    /// cost an afternoon precisely because the one thing that identifies it — which library was not
    /// found — lives in the message, and the message was being discarded here.
    /// <para>
    /// Inner exceptions are included because a plugin's start is mostly async and reflective work, so
    /// the outer type is routinely a wrapper whose own message says nothing. The chain is bounded and
    /// the whole string is capped: this is a wire field, and a plugin is not trusted to keep its
    /// exception text short.
    /// </para>
    /// </remarks>
    internal static string DescribeStartFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        StringBuilder text = new();
        Exception? current = ex;
        for (int depth = 0; current is not null && depth < 4; depth++)
        {
            if (depth > 0)
            {
                text.Append(" -> ");
            }

            text.Append(current.GetType().Name);
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                text.Append(": ").Append(current.Message.Trim());
            }

            // The throwing frames, not the whole trace. For the failures that actually reach a user
            // this is the diagnosis: a NullReferenceException's message says nothing at all, and
            // without a frame there is no way to tell which of a plugin's start steps threw it.
            AppendTopFrames(text, current);
            current = current.InnerException;
        }

        const int maximum = 1200;
        return text.Length <= maximum ? text.ToString() : text.ToString(0, maximum);
    }

    /// <summary>Appends the innermost few stack frames of one exception.</summary>
    /// <param name="text">Buffer to append to.</param>
    /// <param name="ex">The exception whose frames to describe.</param>
    /// <remarks>
    /// Bounded to the frames nearest the throw, which are the ones that identify it. A plugin's
    /// stack is mostly async machinery, so the compiler-generated frames are skipped rather than
    /// spending the budget on <c>MoveNext</c> entries that name nothing.
    /// </remarks>
    private static void AppendTopFrames(StringBuilder text, Exception ex)
    {
        string? trace = ex.StackTrace;
        if (string.IsNullOrWhiteSpace(trace))
        {
            return;
        }

        int appended = 0;
        foreach (string line in trace.Split('\n'))
        {
            if (appended >= 3)
            {
                break;
            }

            string frame = line.Trim();
            if (frame.Length == 0)
            {
                continue;
            }

            text.Append(" | ").Append(frame.Length <= 200 ? frame : frame[..200]);
            appended++;
        }
    }

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

    private static PluginStopReason MapReason(DeviceStopReason reason) => reason switch
    {
        DeviceStopReason.WsgmExiting => PluginStopReason.WsgmExiting,
        DeviceStopReason.StartCanceled => PluginStopReason.StartCanceled,
        DeviceStopReason.StartFailed => PluginStopReason.StartFailed,
        DeviceStopReason.IntegrationDisabled => PluginStopReason.IntegrationDisabled,
        DeviceStopReason.Updating => PluginStopReason.Updating,
        DeviceStopReason.SessionEnding => PluginStopReason.SessionEnding,
        DeviceStopReason.Uninstalling => PluginStopReason.Uninstalling,
        _ => throw new InvalidDataException("Unknown stop reason."),
    };

    private static DeviceCycleState MapOperationalState(PluginOperationalState state) => state switch
    {
        PluginOperationalState.Active => DeviceCycleState.Active,
        PluginOperationalState.Passive => DeviceCycleState.Passive,
        PluginOperationalState.Degraded => DeviceCycleState.Degraded,
        _ => throw new InvalidDataException("Unknown plugin operational state."),
    };

    private static string CreatePluginStateDirectory(string packageId)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("The local application-data directory is unavailable.");
        }

        string directory = Path.Combine(localData, "WSGM", "DeviceState", packageId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private IDevicePlugin Plugin => _package?.Plugin
        ?? throw new InvalidOperationException("Plugin has not been loaded.");

    private DeviceFrameStream Frames => _frames
        ?? throw new InvalidOperationException("Control stream has not been connected.");

    private HostWireSender Sender => _sender
        ?? throw new InvalidOperationException("Control stream has not been connected.");
}

internal sealed class DeviceStartCancellationGate
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private bool _terminalStopRequested;

    internal IDisposable Register(CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        bool cancelImmediately;
        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException("Only one plugin start may be active.");
            }

            cancelImmediately = _terminalStopRequested;
            if (!cancelImmediately)
            {
                _active = cancellation;
            }
        }

        if (cancelImmediately)
        {
            cancellation.Cancel();
        }
        return new Registration(this, cancellation);
    }

    internal Exception? RequestTerminalStop()
    {
        lock (_gate)
        {
            _terminalStopRequested = true;
            try
            {
                _active?.Cancel();
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // CancellationToken callbacks are plugin code. A broken callback must be reported,
                // but it cannot prevent the terminal action from running after Start unwinds.
                return ex;
            }
        }
    }

    internal bool TerminalStopRequested
    {
        get
        {
            lock (_gate)
            {
                return _terminalStopRequested;
            }
        }
    }

    private void Unregister(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, cancellation))
            {
                _active = null;
            }
        }
    }

    private sealed class Registration(
        DeviceStartCancellationGate owner,
        CancellationTokenSource cancellation) : IDisposable
    {
        private DeviceStartCancellationGate? _owner = owner;

        public void Dispose()
        {
            DeviceStartCancellationGate? current = Interlocked.Exchange(ref _owner, null);
            current?.Unregister(cancellation);
        }
    }
}

internal sealed class DeviceCommandRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DeviceCommandCancellation> _commands = [];
    private bool _terminalAdmissionClosed;

    internal bool TryAdd(
        Guid commandId,
        DeviceCommandCancellation command,
        out bool terminalAdmissionClosed)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            terminalAdmissionClosed = _terminalAdmissionClosed;
            if (terminalAdmissionClosed || _commands.ContainsKey(commandId))
            {
                return false;
            }

            _commands.Add(commandId, command);
            return true;
        }
    }

    internal bool TryGet(Guid commandId, out DeviceCommandCancellation? command)
    {
        lock (_gate)
        {
            return _commands.TryGetValue(commandId, out command);
        }
    }

    internal void Remove(Guid commandId)
    {
        lock (_gate)
        {
            _commands.Remove(commandId);
        }
    }

    internal void CancelAll()
    {
        DeviceCommandCancellation[] commands;
        lock (_gate)
        {
            commands = _commands.Values.ToArray();
        }

        List<Exception> failures = [];
        foreach (DeviceCommandCancellation command in commands)
        {
            _ = command.TryCancel();
            Exception? failure = command.TakeCancellationFailure();
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more plugin command cancellations failed.", failures);
        }
    }

    internal Task<IReadOnlyList<Exception>> CloseAdmissionAndCancelAsync()
    {
        DeviceCommandCancellation[] commands;
        lock (_gate)
        {
            _terminalAdmissionClosed = true;
            commands = _commands.Values.ToArray();
        }

        List<Task> cancellationCompletions = [];
        foreach (DeviceCommandCancellation command in commands)
        {
            if (command.TryCancel())
            {
                cancellationCompletions.Add(command.CancellationCompletion);
            }
        }

        return WaitForCompletionAsync(commands, cancellationCompletions);
    }

    private static async Task<IReadOnlyList<Exception>> WaitForCompletionAsync(
        DeviceCommandCancellation[] commands,
        IReadOnlyList<Task> cancellationCompletions)
    {
        if (cancellationCompletions.Count > 0)
        {
            await Task.WhenAll(cancellationCompletions).ConfigureAwait(false);
        }
        if (commands.Length > 0)
        {
            await Task.WhenAll(commands.Select(command => command.Completion)).ConfigureAwait(false);
        }

        List<Exception> failures = [];
        foreach (DeviceCommandCancellation command in commands)
        {
            Exception? failure = command.TakeCancellationFailure();
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }
        return failures;
    }
}

internal sealed class DeviceCommandCancellation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancellationCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _sessionRegistration;
    private readonly Timer? _deadlineTimer;
    private List<Exception>? _failures;
    private int _activeCancellations;
    private bool _cancellationRequested;
    private bool _disposeRequested;
    private bool _sourceDisposed;

    internal DeviceCommandCancellation(
        CancellationToken sessionToken,
        DateTimeOffset deadline)
    {
        _sessionRegistration = sessionToken.Register(
            static state => _ = ((DeviceCommandCancellation)state!).TryCancel(),
            this);
        try
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || _source.IsCancellationRequested)
            {
                _deadlineTimer = null;
                _ = TryCancel();
            }
            else
            {
                _deadlineTimer = new Timer(
                    static state => _ = ((DeviceCommandCancellation)state!).TryCancel(),
                    this,
                    remaining,
                    Timeout.InfiniteTimeSpan);
            }
        }
        catch
        {
            _ = _sessionRegistration.Unregister();
            _source.Dispose();
            throw;
        }
    }

    internal CancellationToken Token => _source.Token;

    internal Task Completion => _completion.Task;

    internal Task CancellationCompletion => _cancellationCompletion.Task;

    internal void Complete() => _completion.TrySetResult();

    internal bool TryCancel()
    {
        lock (_gate)
        {
            if (_disposeRequested || _sourceDisposed)
            {
                return false;
            }
            if (_cancellationRequested)
            {
                return true;
            }

            _cancellationRequested = true;
            _activeCancellations++;
        }

        try
        {
            _source.Cancel();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                (_failures ??= []).Add(ex);
            }
        }
        finally
        {
            bool disposeSource;
            lock (_gate)
            {
                _activeCancellations--;
                disposeSource = _disposeRequested
                    && _activeCancellations == 0
                    && !_sourceDisposed;
                if (disposeSource)
                {
                    _sourceDisposed = true;
                }
            }

            if (disposeSource)
            {
                _source.Dispose();
            }
            _cancellationCompletion.TrySetResult();
        }

        return true;
    }

    internal Exception? TakeCancellationFailure()
    {
        lock (_gate)
        {
            if (_failures is not { Count: > 0 } failures)
            {
                return null;
            }

            _failures = null;
            return failures.Count == 1
                ? failures[0]
                : new AggregateException("Multiple plugin cancellation callbacks failed.", failures);
        }
    }

    public void Dispose()
    {
        bool disposeSource;
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            disposeSource = _activeCancellations == 0 && !_sourceDisposed;
            if (disposeSource)
            {
                _sourceDisposed = true;
            }
        }

        _ = _sessionRegistration.Unregister();
        _deadlineTimer?.Dispose();
        if (disposeSource)
        {
            _source.Dispose();
        }
    }
}
