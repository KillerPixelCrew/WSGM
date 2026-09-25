// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Asus.RogAlly.Tests.Fakes;

/// <summary>An ATKACPI firmware model: scalars and curves that reads return and writes change.</summary>
internal sealed class FakeAsusAcpi : IAsusAcpi
{
    private readonly Dictionary<AsusAcpiId, byte[]> _curves = new()
    {
        [AsusAcpiId.CpuFanCurve] = [.. AllyFanCapability.DefaultCpuCurve],
        [AsusAcpiId.GpuFanCurve] = [.. AllyFanCapability.DefaultGpuCurve]
    };

    private readonly Dictionary<AsusAcpiId, int> _scalars = new()
    {
        [AsusAcpiId.PerformanceMode] = 0,
        [AsusAcpiId.SustainedPower] = 15,
        [AsusAcpiId.SlowPower] = 20,
        [AsusAcpiId.FastPower] = 25,
        [AsusAcpiId.ChargeLimit] = 100,
        [AsusAcpiId.CpuFanSpeed] = 30,
        [AsusAcpiId.GpuFanSpeed] = 28
    };

    public bool Available { get; set; } = true;

    /// <summary>When false, DSTS reports every scalar as unsupported, as a firmware with write-only limits would.</summary>
    public bool ScalarsReadable { get; set; } = true;

    /// <summary>When set, writes to this ID are accepted but ignored.</summary>
    public AsusAcpiId? IgnoreWritesTo { get; set; }

    /// <summary>When set, a write to this ID throws after it lands.</summary>
    public AsusAcpiId? FailWritesTo { get; set; }

    public List<(AsusAcpiId Id, uint Value)> Writes { get; } = [];

    public List<(AsusAcpiId Id, byte[] Data)> BufferWrites { get; } = [];

    public int Scalar(AsusAcpiId id)
    {
        return _scalars[id];
    }

    public void SetScalar(AsusAcpiId id, int value)
    {
        _scalars[id] = value;
    }

    public byte[] Curve(AsusAcpiId id)
    {
        return _curves[id];
    }

    public void SetCurve(AsusAcpiId id, byte[] curve)
    {
        _curves[id] = curve;
    }

    public bool TryOpen()
    {
        return Available;
    }

    public uint ReadStatus(AsusAcpiId id, uint selector = 0)
    {
        if (AsusAcpiProtocol.IsCurve(id))
        {
            return _curves.TryGetValue(id, out var curve)
                ? BinaryPrimitives.ReadUInt32LittleEndian(curve)
                : AsusAcpiProtocol.Unsupported;
        }

        return ScalarsReadable && _scalars.TryGetValue(id, out var value)
            ? 0x10000u | (uint)value
            : AsusAcpiProtocol.Unsupported;
    }

    public byte[] ReadBuffer(AsusAcpiId id, uint selector)
    {
        var response = new byte[16];
        if (_curves.TryGetValue(id, out var curve))
        {
            curve.CopyTo(response, 0);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(response, AsusAcpiProtocol.Unsupported);
        }

        return response;
    }

    public uint Write(AsusAcpiId id, uint value)
    {
        Writes.Add((id, value));
        if (IgnoreWritesTo != id)
        {
            _scalars[id] = (int)value;
        }

        return FailWritesTo == id ? throw new IOException("simulated ATKACPI failure") : 1u;
    }

    public uint WriteBuffer(AsusAcpiId id, ReadOnlySpan<byte> data)
    {
        BufferWrites.Add((id, data.ToArray()));
        if (IgnoreWritesTo != id)
        {
            _curves[id] = data.ToArray();
        }

        return FailWritesTo == id ? throw new IOException("simulated ATKACPI failure") : 1u;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeIdentityReader(string product, string manufacturer = AllyModels.Manufacturer)
    : IAllyIdentityReader
{
    public string Product { get; set; } = product;

    public ValueTask<AllyIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = manufacturer,
            BaseboardProduct = Product,
            BiosVersion = "RC72LA.312"
        };
        return ValueTask.FromResult(new AllyIdentityState
        {
            Snapshot = snapshot,
            Model = AllyModels.Match(snapshot),
            OnAcPower = true
        });
    }
}

internal sealed class FakeVendorHid : IAllyVendorHid
{
    private Func<byte, DateTimeOffset, ValueTask>? _callback;

    public bool Available { get; set; } = true;

    public bool FailWrites { get; set; }

    public List<byte[]> Reports { get; } = [];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Available);
    }

    public ValueTask<bool> StartAsync(Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        _callback = callback;
        return ValueTask.FromResult(Available);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _callback = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteConfigurationAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new IOException("simulated feature report failure");
        }

        Reports.Add(report.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask RaiseAsync(byte code)
    {
        return _callback?.Invoke(code, DateTimeOffset.UtcNow) ?? ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAuraHid : IAllyAuraHid
{
    public bool Available { get; set; } = true;

    public int DynamicLightingRequests { get; private set; }

    public List<(byte[] Report, bool Feature)> Reports { get; } = [];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Available);
    }

    public ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        Reports.Add((report.ToArray(), true));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        Reports.Add((report.ToArray(), false));
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DisableDynamicLightingAsync(CancellationToken cancellationToken)
    {
        DynamicLightingRequests++;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeControllerSource : IAllyControllerSource
{
    private Func<CanonicalControllerSample, CancellationToken, ValueTask>? _publish;
    private long _generation;
    private long _sequence;

    public bool Present { get; set; } = true;

    public IReadOnlyList<PhysicalDeviceIdentity> Devices { get; set; } =
    [
        new()
        {
            InstancePath = @"USB\VID_0B05&PID_1B4C&MI_00\7&1234&0&0000",
            VendorId = "0B05",
            ProductId = "1B4C",
            RequiresHiding = true
        }
    ];

    public bool Running { get; private set; }

    /// <summary>The OEM button state the plugin hands the real source, merged the same way.</summary>
    public AllyOemButtonState? Buttons { get; set; }

    public List<(float Low, float High)> Rumble { get; } = [];

    public ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Present
            ? new AllyControllerTopology(AllyControllerRoute.XInput, 0, Devices, "fake")
            : null);
    }

    public ValueTask StartAsync(AllyControllerTopology topology, long cycleGeneration,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        _publish = publish;
        _generation = cycleGeneration;
        Running = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Running = false;
        _publish = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteRumbleAsync(float low, float high, CancellationToken cancellationToken)
    {
        Rumble.Add((low, high));
        return ValueTask.CompletedTask;
    }

    public ValueTask EmitAsync(CanonicalButtons buttons)
    {
        var now = DateTimeOffset.UtcNow;
        return _publish?.Invoke(new CanonicalControllerSample
        {
            Sequence = ++_sequence,
            CycleGeneration = _generation,
            Timestamp = now,
            Buttons = buttons | (Buttons?.Current(now) ?? CanonicalButtons.None)
        }, CancellationToken.None) ?? ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMotionSource : IAllyMotionSource
{
    public bool Present { get; set; } = true;

    public ValueTask<bool> StartAsync(Func<MotionSample, ValueTask> publish, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Present);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeKeyboardSource : IAllyKeyboardSource
{
    private Func<AllyKeyEvent, ValueTask>? _callback;

    public IReadOnlyCollection<uint> Watched { get; private set; } = [];

    public ValueTask<bool> StartAsync(Func<AllyKeyEvent, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        _callback = callback;
        return ValueTask.FromResult(true);
    }

    public void Watch(IReadOnlyCollection<uint> virtualKeys)
    {
        Watched = [.. virtualKeys];
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _callback = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Delivers a key the way the hook would: only when it is watched.</summary>
    public ValueTask PressAsync(uint virtualKey, bool down)
    {
        return Watched.Contains(virtualKey) && _callback is not null
            ? _callback(new AllyKeyEvent(virtualKey, down, DateTimeOffset.UtcNow))
            : ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>One set of fakes wired into a plugin.</summary>
internal sealed class AllyFakeHardware(string product = "RC72LA")
{
    public FakeAsusAcpi Acpi { get; } = new();

    public FakeAuraHid Aura { get; } = new();

    public FakeControllerSource Controller { get; } = new();

    public FakeIdentityReader Identity { get; } = new(product);

    public FakeKeyboardSource Keyboard { get; } = new();

    public FakeMotionSource Motion { get; } = new();

    public FakeVendorHid Vendor { get; } = new();

    public RogAllyPlugin CreatePlugin()
    {
        return new RogAllyPlugin(new AllyHardwareServices(
            Identity,
            Acpi,
            _ => Vendor,
            _ => Aura,
            (_, buttons) =>
            {
                Controller.Buttons = buttons;
                return Controller;
            },
            _ => Motion,
            Keyboard));
    }
}
