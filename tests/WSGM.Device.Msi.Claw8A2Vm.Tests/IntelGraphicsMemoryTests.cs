using Microsoft.Win32;
using WSGM.Device.Msi.Claw8A2Vm;

namespace WSGM.Device.Tests;

/// <summary>
/// The Intel shared-memory split, exercised against a disposable HKCU subtree.
/// </summary>
/// <remarks>
/// The real key is machine-wide under HKLM and no test may write it, which is what the transport's
/// root seam exists for. Every fact these tests assert about the shape of that key was measured on
/// the reference unit on 2026-09-10: <c>GMM\GpuSystemMemoryPinninglimit</c> as the only value the
/// driver reads, an unreadable sibling adapter subkey alongside the Intel one, and Intel Graphics
/// Software writing 44 into exactly that value and nothing else.
/// </remarks>
public sealed class IntelGraphicsMemoryTests : IDisposable
{
    private const string ClassPath = @"Software\WSGM.Tests\intel-memory";
    private const ulong ThirtyTwoGigabytes = 33_866_657_792;

    private readonly string _scope = $@"{ClassPath}\{Guid.NewGuid():N}";

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_scope, throwOnMissingSubKey: false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.IO.IOException)
        {
            // A leaked unique subtree is preferable to a failed test run reporting a false defect.
        }
    }

    [Fact]
    public void AnIntelAdapterWithThePinningLimitIsFound()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 19_327_352_832);

        IntelGraphicsMemoryTransport transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(new IntelGraphicsMemoryState(57, 19_327_352_832), transport.Read());
    }

    [Fact]
    public void ASiblingAdapterWithoutTheKeyDoesNotHideTheOneThatHasIt()
    {
        // Measured: the reference unit's display adapter class holds a bare 0000 next to the Intel
        // adapter, so an enumeration that gives up on the first miss finds nothing on a machine
        // that has the feature.
        using (RegistryKey bare = Registry.CurrentUser.CreateSubKey($@"{_scope}\0000"))
        {
            bare.SetValue("DriverDesc", "Something else");
        }
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 0);

        Assert.True(Open().IsAvailable);
    }

    [Theory]
    [InlineData("32.0.101.6973")]
    [InlineData("31.0.101.9999")]
    public void ADriverOlderThanTheFeatureIsNotOffered(string version)
    {
        // Intel shipped Shared GPU Memory Override in 32.0.101.6974. An older driver that happens
        // to carry the value would not act on a change, so offering the row would be a lie.
        WriteAdapter("0001", "Intel Corporation", version, percent: 57, reportedBytes: 0);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void ANonIntelAdapterIsNotOffered()
    {
        WriteAdapter("0001", "Advanced Micro Devices, Inc.", "32.0.101.8992", percent: 57, reportedBytes: 0);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void TooLittleSystemMemoryIsNotOffered()
    {
        // Intel documents a 10 GB floor for the feature.
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 0);

        IntelGraphicsMemoryTransport transport = new(
            Registry.CurrentUser, _scope, totalPhysicalBytes: 8UL * 1024 * 1024 * 1024);

        Assert.False(transport.IsAvailable);
    }

    [Fact]
    public void TwoAdaptersStoringTheLimitAreAmbiguousRatherThanAGuess()
    {
        WriteAdapter("0000", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 0);
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 44, reportedBytes: 0);

        Assert.False(Open().IsAvailable);
    }

    [Fact]
    public void AWriteIsStoredAndReadBack()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 19_327_352_832);
        IntelGraphicsMemoryTransport transport = Open();

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
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 0);
        IntelGraphicsMemoryTransport transport = Open();

        Assert.False(transport.TryWrite(percent));
        Assert.Equal(57, transport.Read()!.Value.Percent);
    }

    [Theory]
    [InlineData(IntelGraphicsMemoryTransport.MinimumPercent)]
    [InlineData(IntelGraphicsMemoryTransport.DefaultPercent)]
    [InlineData(IntelGraphicsMemoryTransport.MaximumPercent)]
    public void EveryOfferedBoundIsAccepted(int percent)
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 0);
        IntelGraphicsMemoryTransport transport = Open();

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
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 57, reportedBytes: 19_327_352_832);

        // 57 percent of the reference unit's total physical memory is 19,303,994,889 bytes and its
        // adapter reports 19,327,352,832 — the driver rounds its own figure to a whole 18.00 GiB.
        // Agreeing to within a fraction of a gibibyte is the arithmetic that ties this registry
        // value to the feature at all, so the test asserts the tie rather than a false exactness.
        ulong derived = Open().BytesForPercent(57);
        ulong reported = 19_327_352_832;

        Assert.InRange(reported - derived, 0UL, 256UL * 1024 * 1024);
    }

    [Fact]
    public void AnUntouchedAdapterReadsAsTheDefaultRatherThanAsMissing()
    {
        // There is no "has been changed" flag and none is needed: Intel's reset writes the literal
        // 57 back rather than deleting the value, so an absent value means the same thing. Treating
        // it as missing would hide the row on every machine nobody has configured yet.
        using (RegistryKey adapter = Registry.CurrentUser.CreateSubKey($@"{_scope}\0001"))
        {
            adapter.SetValue("ProviderName", "Intel Corporation");
            adapter.SetValue("DriverVersion", "32.0.101.8992");
        }

        IntelGraphicsMemoryTransport transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(IntelGraphicsMemoryTransport.DefaultPercent, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void AWriteCreatesTheMemoryManagerKeyWhenTheDefaultWasNeverStored()
    {
        using (RegistryKey adapter = Registry.CurrentUser.CreateSubKey($@"{_scope}\0001"))
        {
            adapter.SetValue("ProviderName", "Intel Corporation");
            adapter.SetValue("DriverVersion", "32.0.101.8992");
        }
        IntelGraphicsMemoryTransport transport = Open();

        Assert.True(transport.TryWrite(44));
        Assert.Equal(44, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void TheAdapterCarryingTheValueWinsOverOneThatOnlyCouldHaveIt()
    {
        // Two supported Intel adapters is ambiguous on its own, but the value only ever exists under
        // the one the driver reads it from, so its presence resolves the ambiguity.
        using (RegistryKey other = Registry.CurrentUser.CreateSubKey($@"{_scope}\0000"))
        {
            other.SetValue("ProviderName", "Intel Corporation");
            other.SetValue("DriverVersion", "32.0.101.8992");
        }
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", percent: 44, reportedBytes: 0);

        IntelGraphicsMemoryTransport transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(44, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void NoAdapterAtAllIsSimplyUnavailable()
    {
        Registry.CurrentUser.CreateSubKey(_scope).Dispose();

        IntelGraphicsMemoryTransport transport = Open();

        Assert.False(transport.IsAvailable);
        Assert.Null(transport.Read());
        Assert.False(transport.TryWrite(44));
    }

    private IntelGraphicsMemoryTransport Open() =>
        new(Registry.CurrentUser, _scope, ThirtyTwoGigabytes);

    private void WriteAdapter(string index, string provider, string version, int percent, long reportedBytes)
    {
        using RegistryKey adapter = Registry.CurrentUser.CreateSubKey($@"{_scope}\{index}");
        adapter.SetValue("ProviderName", provider);
        adapter.SetValue("DriverVersion", version);
        if (reportedBytes > 0)
        {
            adapter.SetValue("HardwareInformation.qwMemorySize", reportedBytes, RegistryValueKind.QWord);
        }

        using RegistryKey memory = adapter.CreateSubKey("GMM");
        memory.SetValue("GpuSystemMemoryPinninglimit", percent, RegistryValueKind.DWord);
    }
}
