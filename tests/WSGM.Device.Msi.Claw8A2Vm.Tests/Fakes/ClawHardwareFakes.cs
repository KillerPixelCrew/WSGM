using System.Buffers.Binary;
using WSGM.Device.Msi.Claw8A2Vm;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Tests;

internal sealed class FakeIdentityReader : IClawIdentityReader
{
    public ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ClawIdentityState
        {
            Snapshot = new DeviceIdentitySnapshot
            {
                SystemManufacturer = ClawHardwareFacts.Manufacturer,
                BaseboardProduct = ClawHardwareFacts.BoardProduct,
                SystemSku = ClawHardwareFacts.SystemSku,
                EcFirmwareVersion = ClawHardwareFacts.EcFirmware,
                UsbEndpoints =
                [
                    new UsbEndpointObservation
                    {
                        VendorId = ClawHardwareFacts.UsbVendorId,
                        ProductId = ClawHardwareFacts.XInputProductId,
                        DeviceRelease = ClawHardwareFacts.McuFirmware,
                    },
                ],
            },
            ExactMachineMatch = true,
            WmiFirmwareVerified = true,
            McuFirmwareVerified = true,
            OnAcPower = true,
        });
    }
}

internal sealed class FakeWmiTransport : IMsiWmiTransport
{
    private readonly Dictionary<(string Method, byte Selector), byte[]> _responses = [];

    public FakeWmiTransport()
    {
        SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        SetData(ClawHardwareFacts.PowerBoostAddress, 37);
        SetData(ClawHardwareFacts.ScenarioAddress, 0xC1);
        SetData(ClawHardwareFacts.FanCustomAddress, 0);
        SetData(ClawHardwareFacts.FanFullSpeedAddress, 2);
        SetData(ClawHardwareFacts.ChargeLimitAddress, 80);
        _responses[("Get_Fan", 0)] = Response(0, 0xC7, 0, 0xCF);
        _responses[("Get_Temperature", 0)] = Response(52);
        _responses[("Get_Fan", 1)] = Table(0xA1, 0, 40, 49, 58, 67, 75, 0xA8);
        _responses[("Get_Fan", 2)] = Table(0x91, 0, 40, 49, 58, 67, 75, 0x98);
        _responses[("Get_Temperature", 1)] = Table(0, 0xB2, 0xB3, 50, 60, 70, 80, 88);
        _responses[("Get_Temperature", 2)] = Table(0, 0xC2, 0xC3, 50, 60, 70, 80, 88);
    }

    public List<(string Method, byte[] Package)> Writes { get; } = [];

    public int Reads { get; private set; }

    public void SetResponse(string method, byte selector, byte[] response) =>
        _responses[(method, selector)] = response;

    public bool FailNextSetter { get; set; }

    public Action<string, byte[]>? AfterSetter { get; set; }

    public int ProviderAvailabilityChecks { get; private set; }

    public int ReadData(byte address) =>
        BinaryPrimitives.ReadInt32LittleEndian(_responses[("Get_Data", address)].AsSpan(1, sizeof(int)));

    public void SetData(byte address, int value)
    {
        _responses[("Get_Data", address)] = Data(value);
    }

    public ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderAvailabilityChecks++;
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        return ValueTask.FromResult((byte[])[.. _responses[(methodName, selector)]]);
    }

    public ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextSetter)
        {
            FailNextSetter = false;
            throw new IOException("Synthetic WMI setter failure.");
        }

        Writes.Add((methodName, [.. package]));
        byte selector = package[0];
        if (methodName == "Set_Data")
        {
            _responses[("Get_Data", selector)] = Response(package[1], package[2], package[3], package[4]);
        }
        else
        {
            string getter = methodName == "Set_Fan" ? "Get_Fan" : "Get_Temperature";
            byte[] response = [.. package];
            response[0] = 1;
            _responses[(getter, selector)] = response;
        }

        AfterSetter?.Invoke(methodName, package);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static byte[] Data(int value)
    {
        byte[] response = new byte[32];
        response[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(1, sizeof(int)), value);
        return response;
    }

    private static byte[] Response(params byte[] payload)
    {
        byte[] response = new byte[32];
        response[0] = 1;
        payload.CopyTo(response, 1);
        return response;
    }

    private static byte[] Table(params byte[] payload) => Response(payload);
}

internal sealed class FakeOemEventSource : IMsiOemEventSource
{
    private Func<byte, DateTimeOffset, ValueTask>? _callback;

    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = callback;
        return ValueTask.FromResult(true);
    }

    public ValueTask EmitAsync(byte code, DateTimeOffset timestamp) =>
        _callback?.Invoke(code, timestamp) ?? ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMcuTransport : IClawMcuTransport
{
    private byte[] _profile = ClawA2VmLightingCapability.Encode(new LightingState(50, 0, 0, 0));

    public Action? AfterNextWrite { get; set; }

    public Func<byte[], byte[]>? TransformNextWrite { get; set; }

    public List<byte[]> ProfileWrites { get; } = [];

    public byte[] Profile
    {
        get => [.. _profile];
        set => _profile = [.. value];
    }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((byte[])[.. _profile]);
    }

    public ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] write = payload.ToArray();
        ProfileWrites.Add([.. write]);
        Func<byte[], byte[]>? transform = TransformNextWrite;
        TransformNextWrite = null;
        _profile = transform is null ? write : transform([.. write]);
        Action? afterWrite = AfterNextWrite;
        AfterNextWrite = null;
        afterWrite?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControllerTopology(
            mode,
            mode == ClawControllerMode.XInput
                ? ClawHardwareFacts.XInputProductId
                : ClawHardwareFacts.DirectInputProductId,
            physicalLocation,
            []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeControllerSource : IClawControllerSource
{
    private readonly object _gate = new();
    private Func<CanonicalControllerSample, CancellationToken, ValueTask>? _publish;
    private Action<Exception>? _fault;
    private CancellationTokenSource? _readerCancellation;
    private Task? _activePublication;

    public ControllerTopology Topology { get; set; } = new(
        ClawControllerMode.XInput,
        ClawHardwareFacts.XInputProductId,
        "PCIROOT(0)#USBROOT(0)#USB(2)",
        []);

    public bool FailNextRumble { get; set; }

    public bool FailStop { get; set; }

    public int RumbleWriteAttempts { get; private set; }

    public ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ControllerTopology?>(Topology);
    }

    public ValueTask StartAsync(
        long cycleGeneration,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _readerCancellation?.Dispose();
            _readerCancellation = new CancellationTokenSource();
            _publish = publish;
            _fault = fault;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? readerCancellation;
        Task? activePublication;
        lock (_gate)
        {
            readerCancellation = _readerCancellation;
            activePublication = _activePublication;
            readerCancellation?.Cancel();
        }

        if (activePublication is not null)
        {
            try
            {
                await activePublication.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readerCancellation?.IsCancellationRequested == true)
            {
            }
        }

        lock (_gate)
        {
            _publish = null;
            _fault = null;
            _activePublication = null;
            _readerCancellation?.Dispose();
            _readerCancellation = null;
        }

        if (FailStop)
        {
            throw new IOException("Synthetic controller source stop failure.");
        }
    }

    public ValueTask EmitAsync(CanonicalControllerSample sample)
    {
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish;
        CancellationToken cancellationToken;
        lock (_gate)
        {
            publish = _publish ?? throw new InvalidOperationException("The fake reader is not active.");
            cancellationToken = _readerCancellation?.Token
                ?? throw new InvalidOperationException("The fake reader has no cancellation source.");
            _activePublication = publish(sample, cancellationToken).AsTask();
            return new ValueTask(_activePublication);
        }
    }

    public void TriggerFault(Exception exception)
    {
        Action<Exception> fault;
        lock (_gate)
        {
            fault = _fault ?? throw new InvalidOperationException("The fake reader is not active.");
        }

        fault(exception);
    }

    public ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RumbleWriteAttempts++;
        if (FailNextRumble)
        {
            FailNextRumble = false;
            throw new IOException("Synthetic rumble write failure.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A motion source that accepts every start and keeps the publish callback, so a test can
/// drive samples through the service.</summary>
internal sealed class FakeMotionSource : IClawMotionSource
{
    public Func<MotionSample, ValueTask>? Publish { get; private set; }

    public ValueTask<bool> StartAsync(
        Func<MotionSample, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish = publish;
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeChordSuppressor : IFirmwareChordSuppressor
{
    private Action<Exception>? _fault;

    public ValueTask<bool> StartAsync(Action<Exception> fault, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fault = fault;
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fault = null;
        return ValueTask.CompletedTask;
    }

    public void TriggerFault(Exception exception) =>
        (_fault ?? throw new InvalidOperationException("The fake hook is not active."))(exception);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
