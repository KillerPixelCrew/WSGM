using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Ipc;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Shell;

/// <summary>Authenticated WSGM side of one supervised DeviceHost generation.</summary>
internal sealed class DeviceHostClient : IAsyncDisposable
{
    private const int MaxHandleCount = 4096;
    private const long MaxWorkingSetBytes = 512L * 1024 * 1024;
    private readonly DevicePackageCandidate _candidate;
    private readonly long _hostGeneration;
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
        DevicePackageCandidate candidate,
        long hostGeneration,
        DeviceHostProcess host,
        SharedStateRing stateRing,
        EventWaitHandle stateEvent,
        DeviceFrameStream frames,
        ushort protocolVersion)
    {
        _candidate = candidate;
        _hostGeneration = hostGeneration;
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

    public event Action<DeviceResourceStateNotification>? ResourceStateReceived;

    public event Action<DeviceLifecycleNotification>? LifecycleStateReceived;

    public event Action<DevicePhysicalIdentitiesNotification>? PhysicalIdentitiesReceived;

    public event Action<DeviceOemControlsNotification>? OemControlsReceived;

    public event Action<OemControlEvent>? OemEventReceived;

    public event Action<CanonicalControllerSample>? ControllerSampleReceived;

    public static async Task<DeviceHostClient> StartAsync(
        DevicePackageCandidate candidate,
        uint sessionId,
        long hostGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string pipeName = ControlEndpoint.PipeName(sessionId, token);
        string ringName = $"Local\\WSGM.DeviceState.{sessionId}.{token}";
        string eventName = $"Local\\WSGM.DeviceStateEvent.{sessionId}.{token}";
        byte[] nonce = RandomNumberGenerator.GetBytes(ControlEndpoint.NonceBytes);
        NamedPipeServerStream pipe = DeviceControlPipe.CreateServer(pipeName);
        SharedStateRing ring = SharedStateRing.Create(
            ringName,
            slotCount: 256,
            CanonicalSampleCodec.PayloadBytes);
        EventWaitHandle stateEvent = new(false, EventResetMode.AutoReset, eventName, out _);
        DeviceHostProcess? host = null;
        DeviceFrameStream? frames = null;
        try
        {
            host = DeviceHostProcess.Start(
                candidate,
                pipeName,
                nonce,
                sessionId,
                hostGeneration,
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
            (ushort protocolVersion, DeviceHostHello hello) = await AuthenticateAsync(
                frames,
                candidate,
                nonce,
                sessionId,
                hostGeneration,
                handshake.Token).ConfigureAwait(false);
            Log.Info(
                $"DeviceHost handshake: package={hello.PackageId}, version={hello.PackageVersion}, "
                    + $"hostGeneration={hostGeneration}, protocol={protocolVersion}.");
            return new DeviceHostClient(
                candidate,
                hostGeneration,
                host,
                ring,
                stateEvent,
                frames,
                protocolVersion);
        }
        catch
        {
            if (frames is not null)
            {
                await frames.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                pipe.Dispose();
            }

            host?.Dispose();
            stateEvent.Dispose();
            ring.Dispose();
            throw;
        }
    }

    public async Task<DeviceLifecycleNotification> ActivateAsync(
        DeviceIdentitySnapshot identity,
        long deviceGeneration,
        bool controllerManagementEnabled,
        IReadOnlyList<RecoveryJournalEntry> outstandingJournalEntries,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Activate,
            new DeviceActivateRequest
            {
                Identity = identity,
                DeviceGeneration = deviceGeneration,
                ControllerManagementEnabled = controllerManagementEnabled,
                OutstandingJournalEntries = outstandingJournalEntries,
                Deadline = deadline,
            },
            DeviceWireJsonContext.Default.DeviceActivateRequest,
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
        long deviceGeneration,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Resume,
            new DeviceLifecycleRequest
            {
                Deadline = deadline,
                DeviceGeneration = deviceGeneration,
            },
            DeviceWireJsonContext.Default.DeviceLifecycleRequest,
            deadline,
            cancellationToken).ConfigureAwait(false);
        return RequireResponse(response, DeviceMessageType.LifecycleState,
            DeviceWireJsonContext.Default.DeviceLifecycleNotification);
    }

    public async Task<DeviceLifecycleNotification> DeactivateAsync(
        DeviceDeactivationReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.Deactivate,
            new DeviceDeactivateRequest { Reason = reason, Deadline = deadline },
            DeviceWireJsonContext.Default.DeviceDeactivateRequest,
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
        long deviceGeneration,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        DeviceFrame response = await RequestAsync(
            DeviceMessageType.ControllerManagement,
            new DeviceControllerManagementRequest
            {
                Enabled = enabled,
                DeviceGeneration = deviceGeneration,
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
                Log.Warn($"Controller state ring skipped {missed} superseded samples.");
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
        _lifetime.Cancel();
        using ManualResetEvent waitUnregistered = new(initialState: false);
        if (_stateRegistration.Unregister(waitUnregistered))
        {
            _ = await Task.Run(() => waitUnregistered.WaitOne(TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
        }
        foreach (TaskCompletionSource<DeviceFrame> pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        await _frames.DisposeAsync().ConfigureAwait(false);
        _stateEvent.Dispose();
        _stateRing.Dispose();
        _host.Dispose();
        _lifetime.Dispose();
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
            case DeviceMessageType.ResourceState:
                ResourceStateReceived?.Invoke(Deserialize(
                    frame,
                    DeviceWireJsonContext.Default.DeviceResourceStateNotification));
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
            default:
                Log.Warn($"DeviceHost notification ignored: type={(ushort)frame.Header.MessageType}.");
                break;
        }
    }

    private async Task<DeviceHostExit> MonitorAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        TimeSpan previousCpu = TimeSpan.Zero;
        DateTimeOffset previousSample = started;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_host.Process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                _host.Process.Refresh();
                if (_host.Process.HandleCount > MaxHandleCount
                    || _host.Process.WorkingSet64 > MaxWorkingSetBytes)
                {
                    string detail = _host.Process.HandleCount > MaxHandleCount
                        ? $"handle limit exceeded ({_host.Process.HandleCount})"
                        : $"working-set limit exceeded ({_host.Process.WorkingSet64})";
                    _host.Terminate(72);
                    return new DeviceHostExit(72, DeviceHostExitReason.ResourceLimit, detail,
                        DateTimeOffset.UtcNow - started);
                }

                TimeSpan cpu = _host.Process.TotalProcessorTime;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                double cpuFraction = (cpu - previousCpu).TotalSeconds
                    / Math.Max(0.001, (now - previousSample).TotalSeconds);
                previousCpu = cpu;
                previousSample = now;
                if (cpuFraction > 0.75)
                {
                    Log.Warn($"DeviceHost high CPU: {cpuFraction:P0} of one logical processor.");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (!_disposed && _protocolFaultDetail is not null)
                {
                    _host.Terminate(71);
                    return new DeviceHostExit(
                        71,
                        DeviceHostExitReason.ProtocolFault,
                        _protocolFaultDetail,
                        DateTimeOffset.UtcNow - started);
                }

                return new DeviceHostExit(0, DeviceHostExitReason.Intentional, "Coordinator stopped.",
                    DateTimeOffset.UtcNow - started);
            }

            await _host.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static async Task<(ushort ProtocolVersion, DeviceHostHello Hello)> AuthenticateAsync(
        DeviceFrameStream frames,
        DevicePackageCandidate candidate,
        byte[] nonce,
        uint sessionId,
        long hostGeneration,
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
        NegotiationResult negotiation = DeviceProtocol.Negotiate(
            hello.MinProtocolVersion,
            hello.MaxProtocolVersion,
            hello.SchemaFingerprint,
            out ushort protocolVersion);
        bool nonceDecoded = TryDecodeNonce(hello.Nonce, out byte[] presentedNonce);
        HandshakeVerifier verifier = new(nonce);
        bool accepted = negotiation is NegotiationResult.Agreed
            && nonceDecoded
            && verifier.Accept(presentedNonce)
            && hello.SessionId == sessionId
            && hello.HostGeneration == hostGeneration
            && string.Equals(hello.PackageId, candidate.Manifest?.Id, StringComparison.Ordinal)
            && string.Equals(hello.PackageVersion, candidate.Manifest?.Version, StringComparison.Ordinal);

        DeviceHostHelloAck ack = new()
        {
            Accepted = accepted,
            Negotiation = negotiation,
            ProtocolVersion = accepted ? protocolVersion : (ushort)0,
            PackageId = candidate.Manifest?.Id ?? string.Empty,
            Detail = accepted ? null : "Protocol, launch identity, or one-time nonce did not match.",
        };
        byte[] ackBytes = JsonSerializer.SerializeToUtf8Bytes(
            ack,
            DeviceWireJsonContext.Default.DeviceHostHelloAck);
        await frames.WriteAsync(new FrameHeader
        {
            PayloadLength = ackBytes.Length,
            ProtocolVersion = accepted ? protocolVersion : DeviceProtocol.MaxSupportedVersion,
            MessageType = DeviceMessageType.HelloAck,
            RequestId = helloFrame.Header.RequestId,
            Flags = FrameFlags.IsResponse,
        }, ackBytes, cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new InvalidDataException("DeviceHost authentication failed.");
        }

        return (protocolVersion, hello);
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
    ResourceLimit,
}

/// <summary>Sanitized completion record for one host generation.</summary>
internal sealed record DeviceHostExit(
    int ExitCode,
    DeviceHostExitReason Reason,
    string Detail,
    TimeSpan Lifetime);
