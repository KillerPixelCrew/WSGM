using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Authenticated WSGM side of one supervised DeviceHost generation.</summary>
internal sealed class DeviceHostClient : IAsyncDisposable
{
    private readonly DeviceHostProcess _host;
    private readonly SharedStateRing _stateRing;
    private readonly EventWaitHandle _stateEvent;
    private readonly RegisteredWaitHandle _stateRegistration;
    private readonly DeviceFrameStream _frames;
    private readonly HostWireSender _sender;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DeviceFrame>> _pending = new();
    private readonly Task _reader;
    private readonly Task<DeviceHostExit> _completion;
    private uint _nextRequestId = 10;
    private string? _protocolFaultDetail;
    private long _lastSampleSequence;
    private int _sampleDispatching;
    private bool _disposed;

    private DeviceHostClient(
        DeviceHostProcess host,
        SharedStateRing stateRing,
        EventWaitHandle stateEvent,
        DeviceFrameStream frames,
        ushort protocolVersion)
    {
        _host = host;
        _stateRing = stateRing;
        _stateEvent = stateEvent;
        _frames = frames;
        _sender = new HostWireSender(frames, protocolVersion);
        ProtocolVersion = protocolVersion;
        _stateRegistration = ThreadPool.RegisterWaitForSingleObject(
            _stateEvent,
            static (state, _) => ((DeviceHostClient)state!).DispatchLatestSample(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
        _reader = ReceiveAsync(_lifetime.Token);
        _completion = MonitorAsync(_lifetime.Token);
    }

    public ushort ProtocolVersion { get; }

    public Task<DeviceHostExit> Completion => _completion;

    public event Action<CapabilityDescriptorSet>? DescriptorSetReceived;

    public event Action<CapabilityStateDelta>? CapabilityStateReceived;

    public event Action<CapabilityCommandResult>? LateCommandResultReceived;

    public event Action<DeviceLifecycleNotification>? LifecycleStateReceived;

    public event Action<DevicePhysicalIdentitiesNotification>? PhysicalIdentitiesReceived;

    public event Action<DeviceOemControlsNotification>? OemControlsReceived;

    public event Action<OemControlEvent>? OemEventReceived;

    public event Action<CanonicalControllerSample>? ControllerSampleReceived;

    public static async Task<DeviceHostClient> StartAsync(
        InstalledDevicePackage package,
        uint sessionId,
        long cycleGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string pipeName = ControlEndpoint.PipeName(sessionId, token);
        string ringName = $"Local\\WSGM.DeviceState.{sessionId}.{token}";
        string eventName = $"Local\\WSGM.DeviceStateEvent.{sessionId}.{token}";
        byte[] nonce = RandomNumberGenerator.GetBytes(ControlEndpoint.NonceBytes);
        (NamedPipeServerStream pipe, SharedStateRing ring, EventWaitHandle stateEvent) =
            AcquireStartupResources(
                () => DeviceControlPipe.CreateServer(pipeName),
                () => SharedStateRing.Create(
                    ringName,
                    slotCount: 256,
                    CanonicalSampleCodec.PayloadBytes),
                () => new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    eventName,
                    out _));
        DeviceHostProcess? host = null;
        DeviceFrameStream? frames = null;
        try
        {
            host = DeviceHostProcess.Start(
                package,
                pipeName,
                nonce,
                sessionId,
                cycleGeneration,
                ringName,
                eventName);
            using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            Task connection = pipe.WaitForConnectionAsync(handshake.Token);
            Task exited = host.Process.WaitForExitAsync(handshake.Token);
            Task first = await Task.WhenAny(connection, exited).ConfigureAwait(false);
            if (first == exited)
            {
                throw new InvalidOperationException(
                    $"DeviceHost exited before connecting (exit {host.Process.ExitCode}).");
            }

            await connection.ConfigureAwait(false);
            frames = new DeviceFrameStream(pipe);
            DeviceHostHello hello = await AuthenticateAsync(
                frames,
                package,
                nonce,
                sessionId,
                cycleGeneration,
                handshake.Token).ConfigureAwait(false);
            Log.Info(
                $"DeviceHost handshake: package={hello.PackageId}, version={hello.PackageVersion}, "
                    + $"cycleGeneration={cycleGeneration}, protocol={DeviceProtocol.Version}.");
            return new DeviceHostClient(
                host,
                ring,
                stateEvent,
                frames,
                DeviceProtocol.Version);
        }
        catch (Exception startFailure)
        {
            List<Exception> cleanupFailures = [];
            try
            {
                if (frames is not null)
                {
                    await RetainDisposeFailureAsync(
                        cleanupFailures,
                        "failed-start frame transport disposal",
                        frames.DisposeAsync).ConfigureAwait(false);
                }
                else
                {
                    RetainDisposeFailure(
                        cleanupFailures,
                        "failed-start pipe disposal",
                        pipe.Dispose);
                }
            }
            finally
            {
                try
                {
                    if (host is not null)
                    {
                        RetainDisposeFailure(
                            cleanupFailures,
                            "failed-start host/job disposal",
                            host.Dispose);
                    }
                }
                finally
                {
                    try
                    {
                        RetainDisposeFailure(
                            cleanupFailures,
                            "failed-start state event disposal",
                            stateEvent.Dispose);
                    }
                    finally
                    {
                        RetainDisposeFailure(
                            cleanupFailures,
                            "failed-start state ring disposal",
                            ring.Dispose);
                    }
                }
            }

            if (cleanupFailures.Count == 0)
            {
                throw;
            }
            List<Exception> failures = [startFailure, .. cleanupFailures];
            throw new AggregateException(
                "DeviceHost startup and resource cleanup both failed.",
                failures);
        }
    }

    public async Task<DeviceLifecycleNotification> StartAsync(
        DeviceIdentitySnapshot identity,
        long cycleGeneration,
        bool controllerManagementEnabled,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Start,
            new DeviceStartRequest
            {
                Identity = identity,
                CycleGeneration = cycleGeneration,
                ControllerManagementEnabled = controllerManagementEnabled,
                Deadline = deadline,
            },
            DeviceWireJsonContext.Default.DeviceStartRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(
            response,
            DeviceMessageType.LifecycleState,
            DeviceWireJsonContext.Default.DeviceLifecycleNotification);
    }

    public async Task<DeviceLifecycleNotification> SuspendAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Suspend,
            new DeviceLifecycleRequest { Deadline = deadline },
            DeviceWireJsonContext.Default.DeviceLifecycleRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.LifecycleState,
            DeviceWireJsonContext.Default.DeviceLifecycleNotification);
    }

    public async Task<DeviceLifecycleNotification> ResumeAsync(
        long cycleGeneration,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Resume,
            new DeviceLifecycleRequest
            {
                Deadline = deadline,
                CycleGeneration = cycleGeneration,
            },
            DeviceWireJsonContext.Default.DeviceLifecycleRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.LifecycleState,
            DeviceWireJsonContext.Default.DeviceLifecycleNotification);
    }

    public async Task<DeviceLifecycleNotification> StopAsync(
        DeviceStopReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Stop,
            new DeviceStopRequest { Reason = reason, Deadline = deadline },
            DeviceWireJsonContext.Default.DeviceStopRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.LifecycleState,
            DeviceWireJsonContext.Default.DeviceLifecycleNotification);
    }

    public async Task<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Command,
            command,
            DeviceWireJsonContext.Default.CapabilityCommand,
            command.Deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.CommandResult,
            DeviceWireJsonContext.Default.CapabilityCommandResult);
    }

    public async Task CancelCommandAsync(Guid commandId, CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.CancelCommand,
            new DeviceCancelCommandRequest { CommandId = commandId },
            DeviceWireJsonContext.Default.DeviceCancelCommandRequest,
            DateTimeOffset.UtcNow.AddSeconds(2),
            cancellationToken).ConfigureAwait(false);
        _ = RequireResponse(response, DeviceMessageType.OperationAck,
            DeviceWireJsonContext.Default.DeviceOperationAck);
    }

    public async Task ApplyHapticOutputAsync(
        HapticOutputFrame output,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.HapticOutput,
            output,
            DeviceWireJsonContext.Default.HapticOutputFrame,
            DateTimeOffset.UtcNow.AddMilliseconds(250),
            cancellationToken).ConfigureAwait(false);
        _ = RequireResponse(response, DeviceMessageType.OperationAck,
            DeviceWireJsonContext.Default.DeviceOperationAck);
    }

    public async Task<DeviceControllerHandoffResponse> ReleaseControllerAsync(
        HandoffScope scope,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.ControllerHandoff,
            new DeviceControllerHandoffRequest { Scope = scope, Deadline = deadline },
            DeviceWireJsonContext.Default.DeviceControllerHandoffRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.ControllerHandoff,
            DeviceWireJsonContext.Default.DeviceControllerHandoffResponse);
    }

    public async Task SetControllerManagementAsync(
        bool enabled,
        long cycleGeneration,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.ControllerManagement,
            new DeviceControllerManagementRequest
            {
                Enabled = enabled,
                CycleGeneration = cycleGeneration,
                Deadline = deadline,
            },
            DeviceWireJsonContext.Default.DeviceControllerManagementRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        _ = RequireResponse(response, DeviceMessageType.OperationAck,
            DeviceWireJsonContext.Default.DeviceOperationAck);
    }

    public async Task<DeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.DiagnosticsRequest,
            new DeviceDiagnosticsRequest(),
            DeviceWireJsonContext.Default.DeviceDiagnosticsRequest,
            DateTimeOffset.UtcNow.AddSeconds(2),
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.DiagnosticsSnapshot,
            DeviceWireJsonContext.Default.DeviceDiagnosticsSnapshot);
    }

    public bool TryReadLatestSample(out CanonicalControllerSample? sample, out long sequence)
    {
        Span<byte> payload = stackalloc byte[CanonicalSampleCodec.PayloadBytes];
        if (!_stateRing.TryReadLatest(payload, out sequence))
        {
            sample = null;
            return false;
        }

        return CanonicalSampleCodec.TryRead(payload, out sample);
    }

    private void DispatchLatestSample()
    {
        if (_disposed || Interlocked.Exchange(ref _sampleDispatching, 1) != 0)
        {
            return;
        }

        try
        {
            if (!TryReadLatestSample(out CanonicalControllerSample? sample, out long sequence)
                || sample is null
                || sequence <= Interlocked.Read(ref _lastSampleSequence))
            {
                return;
            }

            long previous = Interlocked.Exchange(ref _lastSampleSequence, sequence);
            long missed = previous <= 0 ? 0 : Math.Max(0, sequence - previous - 1);
            if (missed > 0)
            {
                // The ring runs at the pad's report rate, so a steady skip rate logged 6,382 lines
                // in one session while saying the same thing each time. The count is what matters
                // and it is in the message, so a change in it still gets a line.
                Log.Change(
                    "controller.ring.skips",
                    $"Controller state ring skipped {missed} superseded samples.",
                    "warn ");
            }

            ControllerSampleReceived?.Invoke(sample);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Controller state-ring notification was rejected: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _sampleDispatching, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await RunDisposeStepsAsync(
            _lifetime.Cancel,
            UnregisterStateWaitAsync,
            CancelPendingRequests,
            WaitForReaderAsync,
            _frames.DisposeAsync,
            _stateEvent.Dispose,
            _stateRing.Dispose,
            _host.Dispose,
            _lifetime.Dispose).ConfigureAwait(false);
    }

    private async ValueTask UnregisterStateWaitAsync()
    {
        using ManualResetEvent waitUnregistered = new(initialState: false);
        if (_stateRegistration.Unregister(waitUnregistered))
        {
            _ = await Task.Run(() => waitUnregistered.WaitOne(TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
        }
    }

    internal static (TPipe Pipe, TRing Ring, TEvent StateEvent) AcquireStartupResources<
        TPipe,
        TRing,
        TEvent>(
        Func<TPipe> createPipe,
        Func<TRing> createRing,
        Func<TEvent> createStateEvent)
        where TPipe : class, IDisposable
        where TRing : class, IDisposable
        where TEvent : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(createPipe);
        ArgumentNullException.ThrowIfNull(createRing);
        ArgumentNullException.ThrowIfNull(createStateEvent);
        TPipe? pipe = null;
        TRing? ring = null;
        try
        {
            pipe = createPipe();
            ring = createRing();
            TEvent stateEvent = createStateEvent();
            return (pipe, ring, stateEvent);
        }
        catch (Exception acquisitionFailure)
        {
            List<Exception> failures = [acquisitionFailure];
            try
            {
                if (ring is not null)
                {
                    RetainDisposeFailure(
                        failures,
                        "partial-start state ring disposal",
                        ring.Dispose);
                }
            }
            finally
            {
                if (pipe is not null)
                {
                    RetainDisposeFailure(
                        failures,
                        "partial-start pipe disposal",
                        pipe.Dispose);
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(acquisitionFailure).Throw();
            }

            throw new AggregateException(
                "DeviceHost startup resource acquisition and cleanup both failed.",
                failures);
        }
    }

    private void CancelPendingRequests()
    {
        foreach (TaskCompletionSource<DeviceFrame> pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }
    }

    private ValueTask WaitForReaderAsync() => WaitForReaderDuringDisposeAsync(_reader);

    internal static async ValueTask WaitForReaderDuringDisposeAsync(Task reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Stop is terminal: DeviceHost sends its final response and then closes the control
            // pipe. If that EOF wins the race with lifetime cancellation, reader completion is
            // still a verified teardown rather than a second protocol failure.
        }
    }

    internal static async ValueTask RunDisposeStepsAsync(
        Action cancelLifetime,
        Func<ValueTask> unregisterStateWaitAsync,
        Action cancelPendingRequests,
        Func<ValueTask> waitReaderAsync,
        Func<ValueTask> disposeFramesAsync,
        Action disposeStateEvent,
        Action disposeStateRing,
        Action disposeHost,
        Action disposeLifetime)
    {
        ArgumentNullException.ThrowIfNull(cancelLifetime);
        ArgumentNullException.ThrowIfNull(unregisterStateWaitAsync);
        ArgumentNullException.ThrowIfNull(cancelPendingRequests);
        ArgumentNullException.ThrowIfNull(waitReaderAsync);
        ArgumentNullException.ThrowIfNull(disposeFramesAsync);
        ArgumentNullException.ThrowIfNull(disposeStateEvent);
        ArgumentNullException.ThrowIfNull(disposeStateRing);
        ArgumentNullException.ThrowIfNull(disposeHost);
        ArgumentNullException.ThrowIfNull(disposeLifetime);
        List<Exception> failures = [];
        try
        {
            RetainDisposeFailure(failures, "lifetime cancellation", cancelLifetime);
            await RetainDisposeFailureAsync(
                failures,
                "state-wait unregistration",
                unregisterStateWaitAsync).ConfigureAwait(false);
            RetainDisposeFailure(failures, "pending request cancellation", cancelPendingRequests);
            await RetainDisposeFailureAsync(
                failures,
                "reader completion",
                waitReaderAsync).ConfigureAwait(false);
            await RetainDisposeFailureAsync(
                failures,
                "frame transport disposal",
                disposeFramesAsync).ConfigureAwait(false);
            RetainDisposeFailure(failures, "state event disposal", disposeStateEvent);
            RetainDisposeFailure(failures, "state ring disposal", disposeStateRing);
        }
        finally
        {
            // Closing the supervised host owns the kill-on-close job and must run even if an
            // earlier cleanup step faults catastrophically. Lifetime disposal follows it under a
            // nested finally so neither failure can suppress the other attempt.
            try
            {
                RetainDisposeFailure(failures, "host/job disposal", disposeHost);
            }
            finally
            {
                RetainDisposeFailure(failures, "lifetime disposal", disposeLifetime);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("DeviceHost client disposal was incomplete.", failures);
        }
    }

    private static void RetainDisposeFailure(
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
            Log.Warn($"DeviceHost client {operation} failed; cleanup continues: {ex.Message}");
        }
    }

    private static async ValueTask RetainDisposeFailureAsync(
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
            Log.Warn($"DeviceHost client {operation} failed; cleanup continues: {ex.Message}");
        }
    }

    private async Task<DeviceFrame> RequestAsync<T>(
        DeviceMessageType messageType,
        T payload,
        JsonTypeInfo<T> typeInfo,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        uint requestId = unchecked(Interlocked.Increment(ref _nextRequestId));
        if (requestId == 0)
        {
            requestId = unchecked(Interlocked.Increment(ref _nextRequestId));
        }

        TaskCompletionSource<DeviceFrame> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("DeviceHost request ID collision.");
        }

        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        bounded.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            await _sender.SendAsync(
                messageType,
                requestId,
                FrameFlags.None,
                payload,
                typeInfo,
                bounded.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DeviceFrame? frame = await _frames.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    throw new EndOfStreamException("DeviceHost closed the control pipe.");
                }

                if (frame.Header.ProtocolVersion != ProtocolVersion)
                {
                    throw new InvalidDataException("DeviceHost changed protocol version after negotiation.");
                }

                if ((frame.Header.Flags & FrameFlags.IsResponse) != 0)
                {
                    if (!_pending.TryGetValue(frame.Header.RequestId, out TaskCompletionSource<DeviceFrame>? pending))
                    {
                        if (frame.Header.MessageType is DeviceMessageType.CommandResult)
                        {
                            LateCommandResultReceived?.Invoke(Deserialize(
                                frame,
                                DeviceWireJsonContext.Default.CapabilityCommandResult));
                            continue;
                        }

                        Log.Warn($"DeviceHost late response ignored: request={frame.Header.RequestId}.");
                        continue;
                    }

                    pending.TrySetResult(frame);
                    continue;
                }

                DispatchNotification(frame);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _protocolFaultDetail = ex.Message;
            foreach (TaskCompletionSource<DeviceFrame> pending in _pending.Values)
            {
                pending.TrySetException(ex);
            }

            _lifetime.Cancel();
            throw;
        }
    }

    private void DispatchNotification(DeviceFrame frame)
    {
        switch (frame.Header.MessageType)
        {
            case DeviceMessageType.DescriptorSet:
                DescriptorSetReceived?.Invoke(Deserialize(
                    frame,
                    DeviceWireJsonContext.Default.CapabilityDescriptorSet));
                break;
            case DeviceMessageType.StateDelta:
                CapabilityStateReceived?.Invoke(Deserialize(
                    frame,
                    DeviceWireJsonContext.Default.CapabilityStateDelta));
                break;
            case DeviceMessageType.LifecycleState:
                LifecycleStateReceived?.Invoke(Deserialize(
                    frame,
                    DeviceWireJsonContext.Default.DeviceLifecycleNotification));
                break;
            case DeviceMessageType.PhysicalIdentities:
                PhysicalIdentitiesReceived?.Invoke(Deserialize(
                    frame,
                    DeviceWireJsonContext.Default.DevicePhysicalIdentitiesNotification));
                break;
            case DeviceMessageType.OemControls:
                OemControlsReceived?.Invoke(Deserialize(
                    frame,
                    DeviceWireJsonContext.Default.DeviceOemControlsNotification));
                break;
            case DeviceMessageType.OemEvent:
                OemEventReceived?.Invoke(Deserialize(frame, DeviceWireJsonContext.Default.OemControlEvent));
                break;
            case DeviceMessageType.Trace:
                WriteTrace(Deserialize(frame, DeviceWireJsonContext.Default.DeviceTraceMessage));
                break;
            default:
                Log.Warn($"DeviceHost notification ignored: type={(ushort)frame.Header.MessageType}.");
                break;
        }
    }

    /// <summary>
    /// Writes one host- or plugin-authored trace line into WSGM's log, marked as theirs.
    /// </summary>
    /// <remarks>
    /// The <c>plugin/</c> prefix is not decoration. These lines are written by a package WSGM did
    /// not author, so anyone reading a pasted log has to be able to tell a plugin's claim about the
    /// hardware from WSGM's own observation of it. The scope and text are re-bounded here rather
    /// than trusted from the wire, because the sender is the party being diagnosed.
    /// </remarks>
    private static void WriteTrace(DeviceTraceMessage trace)
    {
        string scope = Sanitize(trace.Scope, 32);
        string message = Sanitize(trace.Message, DeviceTraceMessage.MaxMessageLength);
        if (message.Length == 0)
        {
            return;
        }

        string line = $"plugin/{(scope.Length == 0 ? "plugin" : scope)}: {message}";
        switch (trace.Level)
        {
            case DeviceTraceLevel.Error:
                Log.Error(line);
                break;
            case DeviceTraceLevel.Warn:
                Log.Warn(line);
                break;
            default:
                Log.Info(line);
                break;
        }
    }

    /// <summary>Bounds untrusted trace text and keeps it to one line.</summary>
    /// <remarks>
    /// Newlines are the interesting case: a plugin that could emit them could forge whole log
    /// entries, including ones that look like WSGM's own, which would make the log actively
    /// misleading rather than merely noisy.
    /// </remarks>
    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (builder.Length == maxLength)
            {
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private async Task<DeviceHostExit> MonitorAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            await _host.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            int exitCode = _host.Process.ExitCode;
            return new DeviceHostExit(
                exitCode,
                exitCode == 0 ? DeviceHostExitReason.Clean : DeviceHostExitReason.ProcessFault,
                $"DeviceHost exited with code {exitCode}.",
                DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_disposed && _protocolFaultDetail is not null)
            {
                _host.Terminate(71);
                return new DeviceHostExit(71, DeviceHostExitReason.ProtocolFault,
                    _protocolFaultDetail, DateTimeOffset.UtcNow - started);
            }

            return new DeviceHostExit(0, DeviceHostExitReason.Intentional, "Coordinator stopped.",
                DateTimeOffset.UtcNow - started);
        }
    }

    private static async Task<DeviceHostHello> AuthenticateAsync(
        DeviceFrameStream frames,
        InstalledDevicePackage package,
        byte[] nonce,
        uint sessionId,
        long cycleGeneration,
        CancellationToken cancellationToken)
    {
        DeviceFrame helloFrame = await frames.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("DeviceHost closed before Hello.");
        if (helloFrame.Header.MessageType is not DeviceMessageType.Hello
            || helloFrame.Header.RequestId == 0
            || (helloFrame.Header.Flags & FrameFlags.IsResponse) != 0)
        {
            throw new InvalidDataException("The first DeviceHost frame was not Hello.");
        }

        DeviceHostHello hello = Deserialize(
            helloFrame,
            DeviceWireJsonContext.Default.DeviceHostHello);
        bool nonceDecoded = TryDecodeNonce(hello.Nonce, out byte[] presentedNonce);
        HandshakeVerifier verifier = new(nonce);
        bool accepted = helloFrame.Header.ProtocolVersion == DeviceProtocol.Version
            && nonceDecoded
            && verifier.Accept(presentedNonce)
            && hello.SessionId == sessionId
            && hello.CycleGeneration == cycleGeneration
            && string.Equals(hello.PackageId, package.Manifest?.Id, StringComparison.Ordinal)
            && string.Equals(hello.PackageVersion, package.Manifest?.Version, StringComparison.Ordinal);

        DeviceHostHelloAck ack = new()
        {
            Accepted = accepted,
            PackageId = package.Manifest?.Id ?? string.Empty,
            Detail = accepted ? null : "Protocol, launch identity, or one-time nonce did not match.",
        };
        byte[] ackBytes = JsonSerializer.SerializeToUtf8Bytes(
            ack,
            DeviceWireJsonContext.Default.DeviceHostHelloAck);
        await frames.WriteAsync(new FrameHeader
        {
            PayloadLength = ackBytes.Length,
            ProtocolVersion = DeviceProtocol.Version,
            MessageType = DeviceMessageType.HelloAck,
            RequestId = helloFrame.Header.RequestId,
            Flags = FrameFlags.IsResponse,
        }, ackBytes, cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new InvalidDataException("DeviceHost authentication failed.");
        }

        return hello;
    }

    private static bool TryDecodeNonce(string value, out byte[] nonce)
    {
        try
        {
            nonce = Convert.FromBase64String(value);
            return nonce.Length == ControlEndpoint.NonceBytes;
        }
        catch (FormatException)
        {
            nonce = [];
            return false;
        }
    }

    private static T RequireResponse<T>(
        DeviceFrame frame,
        DeviceMessageType expected,
        JsonTypeInfo<T> typeInfo)
    {
        if (frame.Header.MessageType is DeviceMessageType.Error)
        {
            DeviceProtocolError error = Deserialize(frame, DeviceWireJsonContext.Default.DeviceProtocolError);
            throw new InvalidOperationException($"DeviceHost {error.Code}: {error.Detail}");
        }

        if (frame.Header.MessageType != expected)
        {
            throw new InvalidDataException(
                $"DeviceHost response type {frame.Header.MessageType} was not {expected}.");
        }

        return Deserialize(frame, typeInfo);
    }

    private static T Deserialize<T>(DeviceFrame frame, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(frame.Payload, typeInfo)
                ?? throw new InvalidDataException("DeviceHost payload deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("DeviceHost payload was malformed.", ex);
        }
    }

    private sealed class HostWireSender
    {
        private readonly DeviceFrameStream _frames;
        private readonly ushort _protocolVersion;

        public HostWireSender(DeviceFrameStream frames, ushort protocolVersion)
        {
            _frames = frames;
            _protocolVersion = protocolVersion;
        }

        public ValueTask SendAsync<T>(
            DeviceMessageType messageType,
            uint requestId,
            FrameFlags flags,
            T payload,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
            return _frames.WriteAsync(new FrameHeader
            {
                PayloadLength = bytes.Length,
                ProtocolVersion = _protocolVersion,
                MessageType = messageType,
                RequestId = requestId,
                Flags = flags,
            }, bytes, cancellationToken);
        }
    }
}

/// <summary>Why one supervised host generation ended.</summary>
internal enum DeviceHostExitReason
{
    Clean,
    Intentional,
    ProcessFault,
    ProtocolFault,
}

/// <summary>Sanitized completion record for one host generation.</summary>
internal sealed record DeviceHostExit(
    int ExitCode,
    DeviceHostExitReason Reason,
    string Detail,
    TimeSpan Lifetime);
