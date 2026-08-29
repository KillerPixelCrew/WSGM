using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.DeviceHost;

/// <summary>Generation-validating semantic publication surface for one plugin instance.</summary>
internal sealed class PluginHostAdapter : IPluginHostAdapter, IDisposable
{
    private readonly HostWireSender _sender;
    private readonly ushort _protocolVersion;
    private readonly SharedStateRing? _stateRing;
    private readonly EventWaitHandle? _stateEvent;
    private long _descriptorGeneration;
    private long _stateSequence;
    private bool _disposed;

    public PluginHostAdapter(
        HostWireSender sender,
        ushort protocolVersion,
        long cycleGeneration,
        string? stateRingName,
        string? stateEventName)
    {
        _sender = sender;
        _protocolVersion = protocolVersion;
        CycleGeneration = cycleGeneration;
        if (!string.IsNullOrWhiteSpace(stateRingName))
        {
            _stateRing = SharedStateRing.Open(
                stateRingName,
                slotCount: 256,
                CanonicalSampleCodec.PayloadBytes);
        }

        if (!string.IsNullOrWhiteSpace(stateEventName))
        {
            _stateEvent = EventWaitHandle.OpenExisting(stateEventName);
        }
    }

    public long CycleGeneration { get; private set; }

    public ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.CycleGeneration != CycleGeneration
            || descriptors.Generation <= Interlocked.Read(ref _descriptorGeneration))
        {
            throw new InvalidOperationException("Descriptor generations must be current and monotonic.");
        }

        Interlocked.Exchange(ref _descriptorGeneration, descriptors.Generation);
        return _sender.SendAsync(
            DeviceMessageType.DescriptorSet,
            0,
            FrameFlags.None,
            descriptors,
            DeviceWireJsonContext.Default.CapabilityDescriptorSet,
            _protocolVersion,
            cancellationToken);
    }

    public ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        if (state.CycleGeneration != CycleGeneration
            || state.DescriptorGeneration != Interlocked.Read(ref _descriptorGeneration))
        {
            throw new InvalidOperationException("Capability state belongs to a stale generation.");
        }

        CapabilityStateDelta delta = new(
            Interlocked.Increment(ref _stateSequence),
            state);
        return _sender.SendAsync(
            DeviceMessageType.StateDelta,
            0,
            FrameFlags.None,
            delta,
            DeviceWireJsonContext.Default.CapabilityStateDelta,
            _protocolVersion,
            cancellationToken);
    }

    public ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(devices);
        DevicePhysicalIdentitiesNotification notification = new()
        {
            Devices = devices,
            Output = output,
        };
        return _sender.SendAsync(
            DeviceMessageType.PhysicalIdentities,
            0,
            FrameFlags.None,
            notification,
            DeviceWireJsonContext.Default.DevicePhysicalIdentitiesNotification,
            _protocolVersion,
            cancellationToken);
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
            throw new InvalidOperationException("Controller sample belongs to a stale generation.");
        }

        if (_stateRing is null)
        {
            throw new InvalidOperationException("The coordinator did not provision a state ring.");
        }

        Span<byte> payload = stackalloc byte[CanonicalSampleCodec.PayloadBytes];
        CanonicalSampleCodec.Write(sample, payload);
        _stateRing.Write(payload);
        _stateEvent?.Set();
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(controls);
        DeviceOemControlsNotification notification = new() { Controls = controls };
        return _sender.SendAsync(
            DeviceMessageType.OemControls,
            0,
            FrameFlags.None,
            notification,
            DeviceWireJsonContext.Default.DeviceOemControlsNotification,
            _protocolVersion,
            cancellationToken);
    }

    public ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(controlEvent);
        return _sender.SendAsync(
            DeviceMessageType.OemEvent,
            0,
            FrameFlags.None,
            controlEvent,
            DeviceWireJsonContext.Default.OemControlEvent,
            _protocolVersion,
            cancellationToken);
    }

    public void SetCycleGeneration(long cycleGeneration)
    {
        if (cycleGeneration <= CycleGeneration)
        {
            throw new InvalidOperationException("Cycle generation must increase on resume.");
        }

        CycleGeneration = cycleGeneration;
        Interlocked.Exchange(ref _descriptorGeneration, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateEvent?.Dispose();
        _stateRing?.Dispose();
    }
}
