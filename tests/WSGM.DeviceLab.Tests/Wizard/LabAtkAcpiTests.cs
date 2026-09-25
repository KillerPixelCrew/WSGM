using System.Buffers.Binary;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabAtkAcpiTests
{
    private const uint Mode = 0x00120075;
    private const uint Spl = 0x001200A3;
    private const uint Sppt = 0x001200A0;
    private const uint Fppt = 0x001200C1;
    private const uint CpuCurve = 0x00110024;
    private const uint GpuCurve = 0x00110025;
    private const uint Charge = 0x00120057;

    private static LabAsusLayout Layout()
    {
        return new LabAsusLayout
        {
            Mode = Mode,
            ModeValues = [0, 1, 2],
            ModeNames = new Dictionary<int, string> { [0] = "balanced", [1] = "turbo", [2] = "silent" },
            Spl = Spl,
            Sppt = Sppt,
            Fppt = Fppt,
            MinimumWatts = 5,
            MaximumWatts = 25,
            CpuCurve = CpuCurve,
            GpuCurve = GpuCurve
        };
    }

    [Fact]
    public void Original_CapturesThePowerStateAndTheChargeLimit()
    {
        var channel = new FakeChannel();
        channel.OnRead = id => id == Charge ? 80 : channel.Value(id);
        using LabAtkAcpi acpi = new(channel, Layout() with { Charge = Charge }, new LabPowerLog(), _ => { });

        var original = acpi.Original();

        Assert.Equal(15, original.Power?.Spl);
        Assert.Null(original.PowerUnavailable);
        Assert.Equal(80, original.ChargeLimit);
    }

    [Fact]
    public void Original_ReportsAChangingStateInsteadOfCapturingIt()
    {
        var channel = new FakeChannel();
        var reads = 0;
        channel.OnRead = id => id == Spl ? 15 + reads++ : channel.Value(id);
        using LabAtkAcpi acpi = new(channel, Layout(), new LabPowerLog(), _ => { });

        var original = acpi.Original();

        Assert.Null(original.Power);
        Assert.NotNull(original.PowerUnavailable);
        Assert.Null(original.ChargeLimit); // The layout has no charge limit.
    }

    [Fact]
    public void Snapshot_ReadsTheWholeState()
    {
        var channel = new FakeChannel();
        using LabAtkAcpi acpi = new(channel, Layout(), new LabPowerLog(), _ => { });

        var state = acpi.Snapshot();

        Assert.Equal(0, state.Mode);
        Assert.Equal(15, state.Spl);
        Assert.Equal(20, state.Sppt);
        Assert.Equal(25, state.Fppt);
    }

    [Fact]
    public void StableSnapshot_RefusesAChangingState()
    {
        var channel = new FakeChannel();
        var first = true;
        channel.OnRead = id =>
        {
            if (id == Spl && first)
            {
                first = false;
                return 15;
            }

            return id == Spl ? 16 : channel.Value(id);
        };
        using LabAtkAcpi acpi = new(channel, Layout(), new LabPowerLog(), _ => { });

        Assert.Throws<InvalidOperationException>(() => acpi.StableSnapshot());
    }

    [Fact]
    public void Set_RejectsAValueOutsideTheReviewedEnvelope()
    {
        var channel = new FakeChannel();
        using LabAtkAcpi acpi = new(channel, Layout(), new LabPowerLog(), _ => { });

        Assert.Throws<InvalidOperationException>(() => acpi.Set(Spl, 4)); // Below 5.
        Assert.Throws<InvalidOperationException>(() => acpi.Set(Spl, 66)); // Above 65.
        Assert.Throws<InvalidOperationException>(() => acpi.Set(Mode, 3)); // Not a listed mode.
    }

    [Fact]
    public void SetLimits_WritesInAscendingOrderWhenLoweringAllThree()
    {
        var channel = new FakeChannel();
        using LabAtkAcpi acpi = new(channel, Layout(), new LabPowerLog(), _ => { });
        var original = acpi.Snapshot();

        acpi.SetLimits(original, 10);

        // Lowering: SPL, then SPPT, then FPPT, so SPL <= SPPT <= FPPT holds at every step.
        var order = channel.Writes.Where(write => write.Id is Spl or Sppt or Fppt).Select(write => write.Id).ToArray();
        Assert.Equal([Spl, Sppt, Fppt], order);
        Assert.Equal(10, channel.Value(Spl));
        Assert.Equal(10, channel.Value(Fppt));
    }

    [Fact]
    public void Restore_PutsEveryValueBackAndReadsItBack()
    {
        var channel = new FakeChannel();
        using LabAtkAcpi acpi = new(channel, Layout(), new LabPowerLog(), _ => { });
        var original = acpi.Snapshot();

        acpi.SetLimits(original, 10);
        var restored = acpi.Restore(original);

        Assert.True(restored);
        Assert.Equal(15, channel.Value(Spl));
        Assert.Equal(20, channel.Value(Sppt));
        Assert.Equal(25, channel.Value(Fppt));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidCurve_AcceptsRisingTemperaturesAndRejectsMalformed(bool valid)
    {
        byte[] curve = valid
            ? [20, 30, 40, 50, 60, 70, 80, 90, 10, 20, 30, 40, 50, 60, 70, 80]
            : [90, 80, 70, 60, 50, 40, 30, 20, 10, 20, 30, 40, 50, 60, 70, 80]; // Falling temperatures.

        Assert.Equal(valid, LabAtkAcpi.ValidCurve(curve));
    }

    [Fact]
    public void ValidCurve_RejectsAllZeroDuties()
    {
        Assert.False(LabAtkAcpi.ValidCurve([20, 30, 40, 50, 60, 70, 80, 90, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    // A register file that speaks the DEVS/DSTS protocol LabAtkAcpi builds.
    private sealed class FakeChannel : ILabAtkAcpiChannel
    {
        private readonly Dictionary<uint, byte[]> _curves = new()
        {
            [CpuCurve] = [20, 30, 40, 50, 60, 70, 80, 90, 10, 20, 30, 40, 50, 60, 70, 80],
            [GpuCurve] = [20, 30, 40, 50, 60, 70, 80, 90, 10, 20, 30, 40, 50, 60, 70, 80]
        };

        private readonly Dictionary<uint, int> _scalars = new()
        {
            [Mode] = 0,
            [Spl] = 15,
            [Sppt] = 20,
            [Fppt] = 25
        };

        public Func<uint, int>? OnRead { get; set; }

        public List<(uint Id, int Value)> Writes { get; } = [];

        public bool Control(byte[] input, byte[] output, out uint returned, out int error)
        {
            error = 0;
            var method = BinaryPrimitives.ReadUInt32LittleEndian(input);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8));
            var write = method == 0x53564544;
            var curve = id is CpuCurve or GpuCurve;
            if (write)
            {
                if (curve)
                {
                    _curves[id] = input.AsSpan(12, 16).ToArray();
                }
                else
                {
                    var value = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(12));
                    _scalars[id] = value;
                    Writes.Add((id, value));
                }

                Array.Clear(output);
                returned = 4;
                return true;
            }

            if (curve)
            {
                _curves[id].CopyTo(output, 0);
                returned = 16;
                return true;
            }

            var scalar = OnRead is not null ? OnRead(id) : _scalars[id];
            BinaryPrimitives.WriteUInt32LittleEndian(output, 0x00010000u | (uint)(scalar & 0xFFFF));
            returned = 4;
            return true;
        }

        public void Dispose()
        {
        }

        public int Value(uint id)
        {
            return _scalars[id];
        }
    }
}
