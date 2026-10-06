using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Tests.Fakes;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>
///     The Intel shared-memory split, exercised against an in-memory registry hive.
/// </summary>
/// <remarks>
///     Ported from the Claw package. The real key is machine-wide under HKLM and no test may write it,
///     which is what the transport's root seam exists for. Every fact these tests assert about the shape
///     of that key was measured on the Claw on 2026-09-10: <c>GMM\GpuSystemMemoryPinninglimit</c> as the
///     only value the driver reads, an unreadable sibling adapter subkey alongside the Intel one, and
///     Intel Graphics Software writing 44 into exactly that value and nothing else.
/// </remarks>
public sealed class IntelGraphicsMemoryTests
{
    private const string ClassPath = @"SYSTEM\Class\Display";
    private const ulong ThirtyTwoGigabytes = 33_866_657_792;

    private readonly MemoryRegistryNode _hive = new();
    private static WriteAdmission Admission => new(CancellationToken.None, Deadline.Never, static () => true);

    [Fact]
    public void AnIntelAdapterWithThePinningLimitIsFound()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(57, transport.Read());
        Assert.Equal(@"pci\ven_8086&dev_7d55", transport.MatchingDeviceId);
    }

    [Fact]
    public void ASiblingAdapterWithoutTheKeyDoesNotHideTheOneThatHasIt()
    {
        _hive.Create($@"{ClassPath}\0000").Set("DriverDesc", "Something else");

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);

        Assert.True(Open().IsAvailable);
    }

    [Fact]
    public void AnUnreadableSiblingAdapterDoesNotHideTheOneThatHasIt()
    {
        // Measured on the Claw: that 0000 cannot be opened at all, and an access failure escaping the
        // enumeration would remove the feature on a machine that has it.
        _hive.Create($@"{ClassPath}\0000").Unreadable = true;

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);

        Assert.True(Open().IsAvailable);
    }

    [Theory]
    [InlineData("32.0.101.6973")]
    [InlineData("31.0.101.9999")]
    public void ADriverOlderThanTheFeatureIsNotOffered(string version)
    {
        WriteAdapter("0001", "Intel Corporation", version, 57);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void ANonIntelAdapterIsNotOffered()
    {
        WriteAdapter("0001", "Advanced Micro Devices, Inc.", "32.0.101.8992", 57);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void TooLittleSystemMemoryIsNotOffered()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);

        IntelGraphicsMemoryTransport transport = new(_hive, ClassPath, 8UL * 1024 * 1024 * 1024);

        Assert.False(transport.IsAvailable);
    }

    [Fact]
    public void TwoAdaptersStoringTheLimitAreAmbiguousRatherThanAGuess()
    {
        WriteAdapter("0000", "Intel Corporation", "32.0.101.8992", 57);
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void AWriteIsStoredAndReadBack()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);
        var transport = Open();

        Assert.True(transport.TryWrite(44, Admission, out _));

        // The stored percentage is what reads back; the size the driver reports only follows it after
        // a restart, and the transport does not read it.
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void CancellationDuringPreparationKeepsTheExistingMemorySetting()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);
        var transport = Open();
        using var cancellation = new CancellationTokenSource();
        var admission = new WriteAdmission(cancellation.Token, Deadline.Never, static () => true);
        _hive.Create($@"{ClassPath}\0001").BeforeCreateSubKey = cancellation.Cancel;

        Assert.Throws<DriverFailure>(() => transport.TryWrite(44, admission, out _));
        Assert.Equal(57, transport.Read());
    }

    [Theory]
    [InlineData(12)]
    [InlineData(88)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWriteOutsideTheOfferedRangeIsRefused(int percent)
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);
        var transport = Open();

        Assert.False(transport.TryWrite(percent, Admission, out _));
        Assert.Equal(57, transport.Read());
    }

    [Theory]
    [InlineData(IntelGraphicsMemoryTransport.MinimumPercent)]
    [InlineData(IntelGraphicsMemoryTransport.DefaultPercent)]
    [InlineData(IntelGraphicsMemoryTransport.MaximumPercent)]
    public void EveryOfferedBoundIsAccepted(int percent)
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);
        var transport = Open();

        Assert.True(transport.TryWrite(percent, Admission, out _));
        Assert.Equal(percent, transport.Read());
    }

    [Fact]
    public void TheOfferedRangeIsTheOneIntelGraphicsSoftwareShows()
    {
        Assert.Equal(13, IntelGraphicsMemoryTransport.MinimumPercent);
        Assert.Equal(87, IntelGraphicsMemoryTransport.MaximumPercent);
        Assert.Equal(57, IntelGraphicsMemoryTransport.DefaultPercent);
    }

    [Fact]
    public void APercentageMapsToTheMemoryTheDriverReports()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);

        // 57 percent of the Claw's total physical memory is 19,303,994,889 bytes and its adapter reports
        // 19,327,352,832; the driver rounds its own figure to a whole 18.00 GiB.
        var derived = Open().BytesForPercent(57);
        const ulong reported = 19_327_352_832;

        Assert.InRange(reported - derived, 0UL, 256UL * 1024 * 1024);
    }

    [Fact]
    public void AnUntouchedAdapterReadsAsTheDefaultRatherThanAsMissing()
    {
        WriteBareAdapter("0001");

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(IntelGraphicsMemoryTransport.DefaultPercent, transport.Read());
    }

    [Fact]
    public void AWriteCreatesTheMemoryManagerKeyWhenTheDefaultWasNeverStored()
    {
        WriteBareAdapter("0001");

        var transport = Open();

        Assert.True(transport.TryWrite(44, Admission, out _));
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void TheAdapterCarryingTheValueWinsOverOneThatOnlyCouldHaveIt()
    {
        WriteBareAdapter("0000");
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void NoAdapterAtAllIsSimplyUnavailable()
    {
        _hive.Create(ClassPath);

        var transport = Open();

        Assert.False(transport.IsAvailable);
        Assert.Null(transport.Read());
        Assert.False(transport.TryWrite(44, Admission, out _));
    }

    [Fact]
    public void FailedReadbackDoesNotRefuseAnAcceptedMemoryWrite()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57);
        var transport = Open();
        var memory = _hive.Create($@"{ClassPath}\0001\GMM");
        memory.ReadValue = (_, _) => throw new IOException("Fixture read failure.");
        Assert.True(transport.TryWrite(44, Admission, out var error));
        Assert.Null(error);
        memory.ReadValue = null;
        Assert.Equal(44, transport.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportProbeRemainsAcceptedAfterDifferentOrFailedReadback(bool fails)
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44);
        var transport = Open();
        var memory = _hive.Create($@"{ClassPath}\0001\GMM");
        memory.AfterSetDWord = () => memory.ReadValue = (_, _) =>
            fails ? throw new IOException("Fixture read failure.") : 57;
        Assert.True(transport.ProbeSupport(Admission));
        memory.ReadValue = null;
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void RefusedRegistrySetterStillRefusesTheSupportProbe()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44);
        var transport = Open();
        _hive.Create($@"{ClassPath}\0001\GMM").BeforeSetDWord = () => throw new UnauthorizedAccessException();
        Assert.False(transport.ProbeSupport(Admission));
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void SupportProbePreservesAnAbsentOverride()
    {
        WriteBareAdapter("0001");
        Assert.True(Open().ProbeSupport(Admission));
        using var adapter = _hive.OpenSubKey($@"{ClassPath}\0001");
        Assert.Null(adapter!.OpenSubKey("GMM"));
    }

    [Fact]
    public void SupportProbePreservesTheExactStoredPercentage()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44);
        Assert.True(Open().ProbeSupport(Admission));
        Assert.Equal(44, Open().Read());
    }

    private IntelGraphicsMemoryTransport Open()
    {
        return new IntelGraphicsMemoryTransport(_hive, ClassPath, ThirtyTwoGigabytes);
    }

    private void WriteBareAdapter(string index)
    {
        var adapter = _hive.Create($@"{ClassPath}\{index}");
        adapter.Set("ProviderName", "Intel Corporation");
        adapter.Set("DriverVersion", "32.0.101.8992");
    }

    private void WriteAdapter(string index, string provider, string version, int percent)
    {
        var adapter = _hive.Create($@"{ClassPath}\{index}");
        adapter.Set("ProviderName", provider);
        adapter.Set("DriverVersion", version);
        adapter.Set("MatchingDeviceId", @"pci\ven_8086&dev_7d55");
        adapter.Create("GMM").SetDWord("GpuSystemMemoryPinninglimit", percent);
    }
}
