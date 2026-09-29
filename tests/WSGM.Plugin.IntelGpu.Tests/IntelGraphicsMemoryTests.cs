using Microsoft.Win32;
using WSGM.Plugin.IntelGpu.Graphics;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>
///     The Intel shared-memory split, exercised against a disposable HKCU subtree.
/// </summary>
/// <remarks>
///     Ported from the Claw package. The real key is machine-wide under HKLM and no test may write it,
///     which is what the transport's root seam exists for. Every fact these tests assert about the shape
///     of that key was measured on the Claw on 2026-09-10: <c>GMM\GpuSystemMemoryPinninglimit</c> as the
///     only value the driver reads, an unreadable sibling adapter subkey alongside the Intel one, and
///     Intel Graphics Software writing 44 into exactly that value and nothing else.
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
            Registry.CurrentUser.DeleteSubKeyTree(_scope, false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // A leaked unique subtree is preferable to a failed test run reporting a false defect.
        }
    }

    [Fact]
    public void AnIntelAdapterWithThePinningLimitIsFound()
    {
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 19_327_352_832);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(new IntelGraphicsMemoryState(57, 19_327_352_832), transport.Read());
        Assert.Equal(@"pci\ven_8086&dev_7d55", transport.MatchingDeviceId);
    }

    [Fact]
    public void ASiblingAdapterWithoutTheKeyDoesNotHideTheOneThatHasIt()
    {
        using (var bare = Registry.CurrentUser.CreateSubKey($@"{_scope}\0000"))
        {
            bare.SetValue("DriverDesc", "Something else");
        }

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);

        Assert.True(Open().IsAvailable);
    }

    [Theory]
    [InlineData("32.0.101.6973")]
    [InlineData("31.0.101.9999")]
    public void ADriverOlderThanTheFeatureIsNotOffered(string version)
    {
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
        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 57, 0);

        IntelGraphicsMemoryTransport transport = new(Registry.CurrentUser, _scope, 8UL * 1024 * 1024 * 1024);

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

        // The reported adapter size deliberately does not move: the driver reads the percentage when it
        // initializes, so the two disagree until the machine restarts.
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

        Assert.Null(transport.TryWrite(percent));
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

        // 57 percent of the Claw's total physical memory is 19,303,994,889 bytes and its adapter reports
        // 19,327,352,832; the driver rounds its own figure to a whole 18.00 GiB.
        var derived = Open().BytesForPercent(57);
        const ulong reported = 19_327_352_832;

        Assert.InRange(reported - derived, 0UL, 256UL * 1024 * 1024);
    }

    [Fact]
    public void AnUntouchedAdapterReadsAsTheDefaultRatherThanAsMissing()
    {
        using (var adapter = Registry.CurrentUser.CreateSubKey($@"{_scope}\0001"))
        {
            adapter.SetValue("ProviderName", "Intel Corporation");
            adapter.SetValue("DriverVersion", "32.0.101.8992");
        }

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(IntelGraphicsMemoryTransport.DefaultPercent, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void AWriteCreatesTheMemoryManagerKeyWhenTheDefaultWasNeverStored()
    {
        using (var adapter = Registry.CurrentUser.CreateSubKey($@"{_scope}\0001"))
        {
            adapter.SetValue("ProviderName", "Intel Corporation");
            adapter.SetValue("DriverVersion", "32.0.101.8992");
        }

        var transport = Open();

        Assert.True(transport.TryWrite(44));
        Assert.Equal(44, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void TheAdapterCarryingTheValueWinsOverOneThatOnlyCouldHaveIt()
    {
        using (var other = Registry.CurrentUser.CreateSubKey($@"{_scope}\0000"))
        {
            other.SetValue("ProviderName", "Intel Corporation");
            other.SetValue("DriverVersion", "32.0.101.8992");
        }

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44, 0);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(44, transport.Read()!.Value.Percent);
    }

    [Fact]
    public void NoAdapterAtAllIsSimplyUnavailable()
    {
        Registry.CurrentUser.CreateSubKey(_scope).Dispose();

        var transport = Open();

        Assert.False(transport.IsAvailable);
        Assert.Null(transport.Read());
        Assert.Null(transport.TryWrite(44));
    }

    private IntelGraphicsMemoryTransport Open()
    {
        return new IntelGraphicsMemoryTransport(Registry.CurrentUser, _scope, ThirtyTwoGigabytes);
    }

    private void WriteAdapter(string index, string provider, string version, int percent, long reportedBytes)
    {
        using var adapter = Registry.CurrentUser.CreateSubKey($@"{_scope}\{index}");
        adapter.SetValue("ProviderName", provider);
        adapter.SetValue("DriverVersion", version);
        adapter.SetValue("MatchingDeviceId", @"pci\ven_8086&dev_7d55");
        if (reportedBytes > 0)
        {
            adapter.SetValue("HardwareInformation.qwMemorySize", reportedBytes, RegistryValueKind.QWord);
        }

        using var memory = adapter.CreateSubKey("GMM");
        memory.SetValue("GpuSystemMemoryPinninglimit", percent, RegistryValueKind.DWord);
    }
}
