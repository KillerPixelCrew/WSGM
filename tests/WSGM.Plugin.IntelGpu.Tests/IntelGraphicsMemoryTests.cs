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
    private const ulong ThirtyTwoGigabytes = 33_866_657_792;

    private readonly TemporaryRegistryKey _scope = new("intel-memory");

    /// <inheritdoc />
    public void Dispose()
    {
        _scope.Dispose();
    }

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
        using (var bare = _scope.Create("0000"))
        {
            bare.SetValue("DriverDesc", "Something else");
        }

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

        IntelGraphicsMemoryTransport transport = new(Registry.CurrentUser, _scope.Path, 8UL * 1024 * 1024 * 1024);

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

        Assert.True(transport.TryWrite(44));

        // The stored percentage is what reads back; the size the driver reports only follows it after
        // a restart, and the transport does not read it.
        Assert.Equal(44, transport.Read());
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

        Assert.False(transport.TryWrite(percent));
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

        Assert.True(transport.TryWrite(percent));
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
        using (var adapter = _scope.Create("0001"))
        {
            adapter.SetValue("ProviderName", "Intel Corporation");
            adapter.SetValue("DriverVersion", "32.0.101.8992");
        }

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(IntelGraphicsMemoryTransport.DefaultPercent, transport.Read());
    }

    [Fact]
    public void AWriteCreatesTheMemoryManagerKeyWhenTheDefaultWasNeverStored()
    {
        using (var adapter = _scope.Create("0001"))
        {
            adapter.SetValue("ProviderName", "Intel Corporation");
            adapter.SetValue("DriverVersion", "32.0.101.8992");
        }

        var transport = Open();

        Assert.True(transport.TryWrite(44));
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void TheAdapterCarryingTheValueWinsOverOneThatOnlyCouldHaveIt()
    {
        using (var other = _scope.Create("0000"))
        {
            other.SetValue("ProviderName", "Intel Corporation");
            other.SetValue("DriverVersion", "32.0.101.8992");
        }

        WriteAdapter("0001", "Intel Corporation", "32.0.101.8992", 44);

        var transport = Open();

        Assert.True(transport.IsAvailable);
        Assert.Equal(44, transport.Read());
    }

    [Fact]
    public void NoAdapterAtAllIsSimplyUnavailable()
    {
        _scope.Create().Dispose();

        var transport = Open();

        Assert.False(transport.IsAvailable);
        Assert.Null(transport.Read());
        Assert.False(transport.TryWrite(44));
    }

    private IntelGraphicsMemoryTransport Open()
    {
        return new IntelGraphicsMemoryTransport(Registry.CurrentUser, _scope.Path, ThirtyTwoGigabytes);
    }

    private void WriteAdapter(string index, string provider, string version, int percent)
    {
        using var adapter = _scope.Create(index);
        adapter.SetValue("ProviderName", provider);
        adapter.SetValue("DriverVersion", version);
        adapter.SetValue("MatchingDeviceId", @"pci\ven_8086&dev_7d55");
        using var memory = adapter.CreateSubKey("GMM");
        memory.SetValue("GpuSystemMemoryPinninglimit", percent, RegistryValueKind.DWord);
    }
}
