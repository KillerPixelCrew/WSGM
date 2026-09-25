using System.Buffers.Binary;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabMsiWmiTests
{
    private const byte Sustained = 0x50;
    private const byte Boost = 0x51;
    private const byte Charge = 0xD7;

    private static LabMsiLayout Layout()
    {
        return new LabMsiLayout
        {
            Get = "Get_Data",
            Set = "Set_Data",
            Sustained = Sustained,
            Boost = Boost,
            MinimumWatts = 8,
            MaximumWatts = 37,
            Charge = Charge,
            ChargeMinimum = 60,
            ChargeMaximum = 100
        };
    }

    [Fact]
    public void WritePair_RejectsValuesOutsideTheRecordRange()
    {
        var channel = new FakeChannel();
        using LabMsiWmi wmi = new(channel, Layout(), new LabPowerLog());
        var current = wmi.ReadPower();

        Assert.Throws<InvalidOperationException>(() => wmi.WritePair(current, 7, 7)); // Below 8.
        Assert.Throws<InvalidOperationException>(() => wmi.WritePair(current, 8, 38)); // Above 37.
        Assert.Throws<InvalidOperationException>(() => wmi.WritePair(current, 20, 10)); // PL1 > PL2.
    }

    [Fact]
    public void WritePair_RaisingSustainedAboveBoost_WritesBoostFirst()
    {
        var channel = new FakeChannel(10, 12);
        using LabMsiWmi wmi = new(channel, Layout(), new LabPowerLog());
        var current = wmi.ReadPower();

        wmi.WritePair(current, 20, 20);

        var order = channel.Writes.Where(write => write.Address is Sustained or Boost).Select(write => write.Address);
        Assert.Equal([Boost, Sustained], order);
    }

    [Fact]
    public void RestorePower_PutsTheCapturedPairBack()
    {
        var channel = new FakeChannel();
        using LabMsiWmi wmi = new(channel, Layout(), new LabPowerLog());
        var original = wmi.ReadPower();

        wmi.WritePair(original, 15, 15);
        var restored = wmi.RestorePower(original);

        Assert.True(restored);
        Assert.Equal(30, wmi.ReadPower().Sustained);
        Assert.Equal(30, wmi.ReadPower().Boost);
    }

    [Fact]
    public void WriteChargeRaw_RejectsAPercentageOutsideTheRange()
    {
        var channel = new FakeChannel();
        using LabMsiWmi wmi = new(channel, Layout(), new LabPowerLog());

        Assert.Throws<InvalidOperationException>(() => wmi.WriteChargeRaw(50)); // 50 % is below 60.
    }

    [Fact]
    public void Original_CapturesThePowerStateAndTheRawChargeLimit()
    {
        var channel = new FakeChannel(20, 25, 0x80 | 80);
        using LabMsiWmi wmi = new(channel, Layout(), new LabPowerLog());

        var original = wmi.Original();

        Assert.Equal(new LabMsiState(20, 25, 0), original.Power);
        Assert.Equal(0x80 | 80, original.ChargeRaw);
        Assert.Null(original.PowerUnavailable);
        Assert.Null(original.ChargeUnavailable);
        Assert.Empty(channel.Writes); // A snapshot never writes.
    }

    [Fact]
    public void EncodeCharge_CarriesTheFlagBitThrough()
    {
        // Bit 7 set (Battery Master flag), percentage 60: the flag is kept, the percentage replaced.
        Assert.Equal(0x80 | 80, LabMsiWmi.EncodeCharge(0x80 | 60, 80));
        Assert.Equal(80, LabMsiWmi.EncodeCharge(60, 80));
    }

    private sealed class FakeChannel : ILabMsiWmiChannel
    {
        private readonly Dictionary<byte, byte[]> _registers = new();

        public FakeChannel(int sustained = 30, int boost = 30, byte charge = 0x80 | 100)
        {
            _registers[Sustained] = Package(Sustained, sustained);
            _registers[Boost] = Package(Boost, boost);
            _registers[Charge] = ByteRegister(Charge, charge);
        }

        public List<(byte Address, byte[] Package)> Writes { get; } = [];

        public byte[] Get(string method, byte selector)
        {
            return _registers.TryGetValue(selector, out var value) ? (byte[])value.Clone() : Package(selector, 0);
        }

        public void Set(string method, byte[] package)
        {
            Writes.Add((package[0], (byte[])package.Clone()));
            _registers[package[0]] = (byte[])package.Clone();
        }

        public void Dispose()
        {
        }

        private static byte[] Package(byte address, int value)
        {
            var package = new byte[LabMsiWmi.PackageLength];
            package[0] = 0x01; // Success status.
            BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(1, sizeof(int)), value);
            return package;
        }

        private static byte[] ByteRegister(byte address, byte value)
        {
            var package = new byte[LabMsiWmi.PackageLength];
            package[0] = 0x01;
            package[1] = value;
            return package;
        }
    }
}
