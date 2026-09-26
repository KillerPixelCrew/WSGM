using WSGM.Core;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class CpuBoostTests
{
    [Fact]
    public void ASchemeThatRefusesTheReadOffersNothing()
    {
        var status = new CpuBoost(new FakeCpuBoostApi { Readable = false }).Read();

        Assert.False(status.Supported);
        Assert.Null(status.OnAc);
    }

    [Fact]
    public void EachOfHcsModesReadsBackFromItsStoredValue()
    {
        FakeCpuBoostApi api = new();
        foreach (var option in CpuBoost.Offered)
        {
            api.Values[false] = (uint)option.Mode;
            api.Values[true] = (uint)option.Mode;

            var status = new CpuBoost(api).Read();

            Assert.True(status.Supported);
            Assert.Equal(option.Mode, status.OnAc);
            Assert.Equal(option.Mode, status.OnBattery);
        }
    }

    [Fact]
    public void AModeWsgmDoesNotOfferReadsAsUnknownRatherThanNearest()
    {
        // Windows' aggressive-at-guaranteed modes, which HC does not offer either.
        FakeCpuBoostApi api = new();
        api.Values[false] = 5;

        var status = new CpuBoost(api).Read();

        Assert.True(status.Supported);
        Assert.Null(status.OnAc);
        Assert.Equal(CpuBoostMode.Enabled, status.OnBattery);
    }

    [Fact]
    public void ApplyWritesBothSourcesRevealsAndReactivatesAsHcDoes()
    {
        FakeCpuBoostApi api = new();

        new CpuBoost(api).Apply(CpuBoostMode.Disabled);

        // HC's WritePowerCfg: attributes, AC, DC, then the scheme re-activated; then the readback.
        Assert.Equal(["read", "read", "reveal", "write ac 0", "write dc 0", "refresh", "read", "read"], api.Calls);
        Assert.Equal(0u, api.Values[false]);
        Assert.Equal(0u, api.Values[true]);
    }

    [Fact]
    public void ApplyWritesNothingWhenBothSourcesAlreadyHoldTheMode()
    {
        FakeCpuBoostApi api = new();

        new CpuBoost(api).Apply(CpuBoostMode.Enabled);

        Assert.DoesNotContain(api.Calls, call => call.StartsWith("write", StringComparison.Ordinal));
    }

    [Fact]
    public void AWriteWindowsDoesNotReportBackIsAFailure()
    {
        FakeCpuBoostApi api = new() { IgnoreWrites = true };

        var error = Assert.Throws<InvalidOperationException>(() => new CpuBoost(api).Apply(CpuBoostMode.Aggressive));

        Assert.Contains("Aggressive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdsRoundTripAndNameEveryOfferedMode()
    {
        foreach (var option in CpuBoost.Offered)
        {
            Assert.Equal(option.Mode, CpuBoost.ModeForId(CpuBoost.IdFor(option.Mode)));
            Assert.Equal(option.Name, CpuBoost.NameFor(option.Mode));
        }

        Assert.Null(CpuBoost.ModeForId("turbo"));
        Assert.Equal(5, CpuBoost.Offered.Count);
    }
}
