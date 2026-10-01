using WSGM.Device.Msi.Claw.Tests.Fakes;

namespace WSGM.Device.Msi.Claw.Tests;

/// <summary>
///     The Intel shared-memory split, exercised against an in-memory registry hive.
/// </summary>
/// <remarks>
///     The real key is machine-wide under HKLM and no test may write it, which is what the transport's
///     root seam exists for. Every fact these tests assert about the shape of that key was measured on
///     the reference unit on 2026-09-10: <c>GMM\GpuSystemMemoryPinninglimit</c> as the only value the
///     driver reads, an unreadable sibling adapter subkey alongside the Intel one, and Intel Graphics
///     Software writing 44 into exactly that value and nothing else.
///     The transport traces its writes, and <c>PluginTrace</c> has one process-wide sink, so these
///     tests share the collection that installs it. Without that, a write here lands in whichever
///     trace host another class installed and fails that class's assertion instead.
/// </remarks>
[Collection("plugin-trace")]
public sealed class IntelGraphicsMemoryTests
{
    private const string ClassPath = @"SYSTEM\Class\Display";
    private const ulong ThirtyTwoGigabytes = 33_866_657_792;

    private readonly MemoryRegistryNode _hive = new();

    [Fact]
    public void AnIntelAdapterWithThePinningLimitIsFound()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 19_327_352_832);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(new IntelGraphicsMemoryState(57, 19_327_352_832), transport.Read());
    }

    [Fact]
    public void ASiblingAdapterWithoutTheKeyDoesNotHideTheOneThatHasIt()
    {
        // The reference unit's display adapter class holds another 0000 next to the Intel adapter,
        // so an enumeration that gives up on the first miss finds nothing on a machine that has the
        // feature.
        _hive.Create($@"{ClassPath}\0000").Set("DriverDesc", "Something else");

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);

        Assert.True(Open().IsAvailable);
    }

    [Fact]
    public void AnUnreadableSiblingAdapterDoesNotHideTheOneThatHasIt()
    {
        // Measured: that 0000 cannot be opened at all, and an access failure escaping the
        // enumeration would remove the feature on a machine that has it.
        _hive.Create($@"{ClassPath}\0000").Unreadable = true;

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);

        Assert.True(Open().IsAvailable);
    }

    [Theory]
    [InlineData("32.0.101.6973")]
    [InlineData("31.0.101.9999")]
    public void ADriverOlderThanTheFeatureIsNotOffered(string version)
    {
        // Intel shipped Shared GPU Memory Override in 32.0.101.6974. An older driver that happens
        // to carry the value would not act on a change, so offering the row would be a lie.
        WriteAdapter("0001", "Intel Corporation", version, 57, 0);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void ANonIntelAdapterIsNotOffered()
    {
        WriteAdapter("0001", "Advanced Micro Devices, Inc.", "32.0.101.8992", 57, 0);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void TooLittleSystemMemoryIsNotOffered()
    {
        // Intel documents a 10 GB floor for the feature.
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);

        IntelGraphicsMemoryTransport transport = new(_hive, ClassPath, 8UL * 1024 * 1024 * 1024);

        Assert.False(transport.IsAvailable);
    }

    [Fact]
    public void TwoAdaptersStoringTheLimitAreAmbiguousRatherThanAGuess()
    {
        WriteAdapter("0000", "Intel Corporation", "32.0.101.8992", 57, 0);
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44, 0);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void AWriteIsStoredAndReadBack()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 19_327_352_832);
        var transport = Open();

        Assert.True(transport.TryWrite(44));

        // The reported adapter size deliberately does not move: the driver reads the percentage when
        // it initializes, so the two disagree until the machine restarts. Confirmed on the reference
        // unit after Intel Graphics Software wrote 44 and qwMemorySize stayed at 57 percent.
        Assert.Equal(new IntelGraphicsMemoryState(44, 19_327_352_832), transport.Read());
    }

    [Theory]
    [InlineData(12)]
    [InlineData(88)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AWriteOutsideTheOfferedRangeIsRefused(int percent)
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);
        var transport = Open();

        Assert.False(transport.TryWrite(percent));
        Assert.Equal(57, transport.Read()!.Value.Percent);
    }

    [Theory]
    [InlineData(IntelGraphicsMemoryTransport.MinimumPercent)]
    [InlineData(IntelGraphicsMemoryTransport.DefaultPercent)]
    [InlineData(IntelGraphicsMemoryTransport.MaximumPercent)]
    public void EveryOfferedBoundIsAccepted(int percent)
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);
        var transport = Open();

        Assert.True(transport.TryWrite(percent));
        Assert.Equal(percent, transport.Read()!.Value.Percent);
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
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 19_327_352_832);

        // 57 percent of the reference unit's total physical memory is 19,303,994,889 bytes and its
        // adapter reports 19,327,352,832; the driver rounds its own figure to a whole 18.00 GiB.
        // Agreeing to within a fraction of a gibibyte is the arithmetic that ties this registry
        // value to the feature at all, so the test asserts the tie rather than a false exactness.
        var derived = Open().BytesForPercent(57);
        const ulong reported = 19_327_352_832;

        Assert.InRange(reported - derived, 0UL, 256UL * 1024 * 1024);
    }

    [Fact]
    public void AnUntouchedAdapterReadsAsTheDefaultRatherThanAsMissing()
    {
        // There is no "has been changed" flag and none is needed: Intel's reset writes the literal
        // 57 back rather than deleting the value, so an absent value means the same thing. Treating
        // it as missing would hide the row on every machine nobody has configured yet.
        WriteBareAdapter("0001");

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(IntelGraphicsMemoryTransport.DefaultPercent, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void AWriteCreatesTheMemoryManagerKeyWhenTheDefaultWasNeverStored()
    {
        WriteBareAdapter("0001");

        var transport = Open();

        Assert.True(transport.TryWrite(44));
        Assert.Equal(44, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void TheAdapterCarryingTheValueWinsOverOneThatOnlyCouldHaveIt()
    {
        // Two supported Intel adapters is ambiguous on its own, but the value only ever exists under
        // the one the driver reads it from, so its presence resolves the ambiguity.
        WriteBareAdapter("0000");
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44, 0);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(44, transport.Read()!.Value.Percent);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    [InlineData(32u)]
    public void AFramePresentationModeIsStoredAndReadBack(uint mode)
    {
        // Intel's own gaming-flip flag values: 1 application default, 4 VSync on, 8 Smooth Sync,
        // 32 capped FPS. Confirmed on the reference unit, where each wrote and read back exactly.
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);
        var transport = Open();

        Assert.True(transport.TryWriteFlipMode(mode));
        Assert.Equal(mode, transport.ReadFlipMode());
    }

    [Fact]
    public void AnAdapterThatStoresNoModeReadsAsNothingRatherThanZero()
    {
        // Zero is not a flag Intel defines, so it must not be invented as a value. The caller reads
        // an absent mode as the application default, which is what an untouched driver does.
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);

        Assert.Null(Open().ReadFlipMode());
    }

    [Fact]
    public void AFramePresentationWriteCreatesTheSettingsKeyWhenItIsAbsent()
    {
        // The 3D settings key exists on a configured driver but need not on a fresh one, and a
        // capability that only works after Intel's software has run once is not a capability.
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);
        var transport = Open();

        Assert.True(transport.TryWriteFlipMode(4));
        Assert.Equal(4u, transport.ReadFlipMode());
    }

    [Fact]
    public void NoAdapterMeansNoFramePresentationModeEither()
    {
        _hive.Create(ClassPath);
        var transport = Open();

        Assert.Null(transport.ReadFlipMode());
        Assert.False(transport.TryWriteFlipMode(4));
    }

    [Fact]
    public void NoAdapterAtAllIsSimplyUnavailable()
    {
        _hive.Create(ClassPath);

        var transport = Open();

        Assert.False(transport.IsAvailable);
        Assert.Null(transport.Read());
        Assert.False(transport.TryWrite(44));
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

    private void WriteAdapter(string index, string provider, string version, int percent, long reportedBytes)
    {
        var adapter = _hive.Create($@"{ClassPath}\{index}");
        adapter.Set("ProviderName", provider);
        adapter.Set("DriverVersion", version);
        if (reportedBytes > 0)
        {
            adapter.Set("HardwareInformation.qwMemorySize", reportedBytes);
        }

        adapter.Create("GMM").SetDWord("GpuSystemMemoryPinninglimit", percent);
    }
}
