using System.Buffers.Binary;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests.Fakes;

internal sealed class FakeIdentityReader : IClawIdentityReader
{
    public int ReadCount { get; private set; }

    public static ClawIdentityState CreateState()
    {
        return new ClawIdentityState
        {
            Snapshot = new DeviceIdentitySnapshot
            {
                SystemManufacturer = ClawHardwareFacts.Manufacturer,
                BaseboardProduct = ClawHardwareFacts.BoardProduct,
                SystemSku = ClawHardwareFacts.SystemSku,
                EcFirmwareVersion = ClawHardwareFacts.EcFirmware,
                // A revision the plugin was never reviewed against, on purpose: controller and
                // lighting ownership must not depend on it.
                McuFirmwareVersion = "0230",
                UsbEndpoints =
                [
                    new UsbEndpointObservation
                    {
                        VendorId = ClawHardwareFacts.UsbVendorId,
                        ProductId = ClawHardwareFacts.XInputProductId,
                        DeviceRelease = "0230"
                    }
                ]
            },
            ExactMachineMatch = true,
            WmiFirmwareVerified = true,
            OnAcPower = true
        };
    }

    public ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ValueTask.FromResult(CreateState());
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

    public bool FailNextSetter { get; set; }

    public Action<string, byte[]>? AfterSetter { get; set; }

    public int ProviderAvailabilityChecks { get; private set; }

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
        var selector = package[0];
        if (methodName == "Set_Data")
        {
            _responses[("Get_Data", selector)] = Response(package[1], package[2], package[3], package[4]);
        }
        else
        {
            var getter = methodName == "Set_Fan" ? "Get_Fan" : "Get_Temperature";
            byte[] response = [.. package];
            response[0] = 1;
            _responses[(getter, selector)] = response;
        }

        AfterSetter?.Invoke(methodName, package);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void SetResponse(string method, byte selector, byte[] response)
    {
        _responses[(method, selector)] = response;
    }

    public int ReadData(byte address)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(_responses[("Get_Data", address)].AsSpan(1, sizeof(int)));
    }

    public void SetData(byte address, int value)
    {
        _responses[("Get_Data", address)] = Data(value);
    }

    private static byte[] Data(int value)
    {
        var response = new byte[32];
        response[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(1, sizeof(int)), value);
        return response;
    }

    private static byte[] Response(params byte[] payload)
    {
        var response = new byte[32];
        response[0] = 1;
        payload.CopyTo(response, 1);
        return response;
    }

    private static byte[] Table(params byte[] payload)
    {
        return Response(payload);
    }
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

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask EmitAsync(byte code, DateTimeOffset timestamp)
    {
        return _callback?.Invoke(code, timestamp) ?? ValueTask.CompletedTask;
    }
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
        init => _profile = [.. value];
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
        var write = payload.ToArray();
        ProfileWrites.Add([.. write]);
        var transform = TransformNextWrite;
        TransformNextWrite = null;
        _profile = transform is null ? write : transform([.. write]);
        var afterWrite = AfterNextWrite;
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

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeControllerSource : IClawControllerSource
{
    private readonly Lock _gate = new();
    private Task? _activePublication;
    private Func<CanonicalControllerSample, CancellationToken, ValueTask>? _publish;
    private CancellationTokenSource? _readerCancellation;

    public ControllerTopology Topology { get; init; } = new(
        ClawControllerMode.XInput,
        ClawHardwareFacts.XInputProductId,
        "PCIROOT(0)#USBROOT(0)#USB(2)",
        []);

    public bool FailNextRumble { get; set; }

    public bool FailStop { get; init; }

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
            _activePublication = null;
            _readerCancellation?.Dispose();
            _readerCancellation = null;
        }

        if (FailStop)
        {
            throw new IOException("Synthetic controller source stop failure.");
        }
    }

    public ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RumbleWriteAttempts++;
        if (!FailNextRumble)
        {
            return ValueTask.CompletedTask;
        }

        FailNextRumble = false;
        throw new IOException("Synthetic rumble write failure.");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask EmitAsync(CanonicalControllerSample sample)
    {
        lock (_gate)
        {
            var publish = _publish ?? throw new InvalidOperationException("The fake reader is not active.");
            var cancellationToken = _readerCancellation?.Token
                                    ?? throw new InvalidOperationException(
                                        "The fake reader has no cancellation source.");
            _activePublication = publish(sample, cancellationToken).AsTask();
            return new ValueTask(_activePublication);
        }
    }
}

/// <summary>
///     A motion source that accepts every start and keeps the publish callback, so a test can
///     drive samples through the service.
/// </summary>
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

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
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

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void TriggerFault(Exception exception)
    {
        (_fault ?? throw new InvalidOperationException("The fake hook is not active."))(exception);
    }
}
