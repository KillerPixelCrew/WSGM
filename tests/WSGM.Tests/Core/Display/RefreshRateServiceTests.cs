using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Tests.Core.Display;

public sealed class RefreshRateServiceTests
{
    [Fact]
    public void TheListedRatesAreDiscoveredOnceAndReadAgainAfterInvalidation()
    {
        Harness harness = new();

        Assert.Equal([48, 60, 75, 100, 120], harness.Service.ListedRates());
        Assert.Equal([48, 60, 75, 100, 120], harness.Service.ListedRates());
        Assert.Equal(1, harness.RateReads);

        harness.Service.Invalidate();
        harness.Rates = [60, 120];

        Assert.Equal([60, 120], harness.Service.ListedRates());
        Assert.Equal(2, harness.RateReads);
    }

    [Fact]
    public void AListedRateIsWrittenAsTheWindowsMode()
    {
        Harness harness = new();

        Assert.True(harness.Service.TryApply(75));

        Assert.Equal(75, Assert.Single(harness.Applied));
        Assert.Equal(75, harness.Service.CurrentRate());
    }

    [Theory]
    [InlineData(72, 75)]
    [InlineData(54, 60)]
    [InlineData(1000, 120)]
    public void ARateBetweenListedOnesGoesToTheNearestAndNeverReachesWindowsAsAsked(int asked, int applied)
    {
        Harness harness = new();

        Assert.True(harness.Service.TryApply(asked));

        Assert.Equal(applied, Assert.Single(harness.Applied));
    }

    [Fact]
    public void NoListedRateRefusesTheWriteAndStillSaysSo()
    {
        Harness harness = new() { Rates = [] };
        var changes = 0;
        harness.Service.Changed += () => changes++;

        Assert.False(harness.Service.TryApply(60));

        Assert.Empty(harness.Applied);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ARefusedModeIsReportedAndSoARowCanReturnToTheRateInForce()
    {
        Harness harness = new() { ApplySucceeds = false };
        var changes = 0;
        harness.Service.Changed += () => changes++;

        Assert.False(harness.Service.TryApply(60));

        Assert.Equal(1, changes);
        Assert.Equal(120, harness.Service.CurrentRate());
    }

    [Fact]
    public void ADisplayThatChangedDuringTheWriteIsNotWrittenAgainstTheOldList()
    {
        Harness harness = new();
        _ = harness.Service.ListedRates();
        harness.Width = 1280;
        harness.Rates = [60];

        Assert.True(harness.Service.TryApply(120));

        // The forced read before the write found the new display, so the rate came from its list.
        Assert.Equal(60, Assert.Single(harness.Applied));
    }

    [Fact]
    public void AnUnreadableDisplayHasNoRatesAndNoCurrentRate()
    {
        Harness harness = new() { Readable = false };

        Assert.Empty(harness.Service.ListedRates());
        Assert.Null(harness.Service.CurrentRate());
        Assert.False(harness.Service.TryApply(60));
    }

    [Theory]
    [InlineData(120, 4)]
    [InlineData(48, 0)]
    [InlineData(90, 2)]
    [InlineData(30, 0)]
    public void APositionIsTheListedRateAtOrBelowTheCurrentOne(int current, int position)
    {
        Assert.Equal(position, RefreshRateService.PositionOf([48, 60, 75, 100, 120], current));
    }

    [Fact]
    public void AnUnknownRateHasNoPosition()
    {
        Assert.Null(RefreshRateService.PositionOf([60, 120], null));
        Assert.Null(RefreshRateService.PositionOf([], 60));
    }

    [Fact]
    public void TheFrameLimitRangeFollowsTheFastestListedRate()
    {
        Harness harness = new();

        Assert.Equal((30, 120), harness.Service.FrameLimitRange());
        Assert.Equal(0, harness.Service.FrameLimitOptions()[0]);
        Assert.Equal(30, harness.Service.FrameLimitOptions()[1]);
        Assert.Equal(120, harness.Service.FrameLimitOptions()[^1]);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Service = new RefreshRateService(
                () =>
                {
                    RateReads++;
                    return Rates;
                },
                () => Readable
                    ? new DisplayOperatingPoint(
                        new DisplayTargetIdentity("display", null, null, "display", 0, 0, 1), Width, 1080, 32)
                    : null,
                (_, rate) =>
                {
                    if (!ApplySucceeds)
                    {
                        return false;
                    }

                    Applied.Add(rate);
                    Current = rate;
                    return true;
                },
                _ => Current);
        }

        public RefreshRateService Service { get; }

        public List<int> Applied { get; } = [];

        public IReadOnlyList<int> Rates { get; set; } = [48, 60, 75, 100, 120];

        public int RateReads { get; private set; }

        public int Width { get; set; } = 1920;

        public bool Readable { get; set; } = true;

        public bool ApplySucceeds { get; set; } = true;

        private int Current { get; set; } = 120;
    }
}
