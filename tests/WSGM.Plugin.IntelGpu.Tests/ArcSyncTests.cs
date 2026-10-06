using WSGM.Plugin.IntelGpu.Controls;
using WSGM.Plugin.IntelGpu.Display;
using WSGM.Plugin.IntelGpu.Igcl;
using WSGM.Plugin.IntelGpu.Tests.Fakes;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>How a monitor's Arc Sync range becomes the Custom profile's rows.</summary>
/// <remarks>
///     The Claw 8 AI+ A2VM reported 30-120 Hz on 2026-08-30; the Custom path itself follows Intel's
///     <c>Samples/IntelArcSync</c> and is blind.
/// </remarks>
public sealed class ArcSyncTests
{
    [Fact]
    public void TheCustomRangeIsTheMonitorsWholeRefreshRange()
    {
        var bounds = ArcSyncBounds.From(Monitor(29.97f, 120f, 5000, 5000))!.Value;

        Assert.Equal((30, 120), bounds.RangeOf(ArcSyncField.MinimumHz));
        Assert.Equal((30, 120), bounds.RangeOf(ArcSyncField.MaximumHz));

        // The frame time span of 30-120 Hz: 33,333 us down to 8,333 us.
        Assert.Equal((0, 25000), bounds.RangeOf(ArcSyncField.FrameTimeIncrease));
    }

    [Fact]
    public void AReportedFrameTimeLimitAboveTheSpanWidensTheRange()
    {
        var bounds = ArcSyncBounds.From(Monitor(48f, 60f, 9000, 0))!.Value;

        Assert.Equal(9000, bounds.FrameTimeMaximum);
    }

    [Fact]
    public void ARangeWithoutTwoWholeRatesAllowsNoCustomProfile()
    {
        Assert.Null(ArcSyncBounds.From(Monitor(60f, 60f, 0, 0)));
        Assert.Null(ArcSyncBounds.From(Monitor(0f, 60f, 0, 0)));
    }

    [Fact]
    public void AFieldReadIsRoundedIntoTheRowsRange()
    {
        var bounds = ArcSyncBounds.From(Monitor(30f, 120f, 0, 0))!.Value;
        CtlArcSyncProfileParams profile = default;
        profile.MinimumHz = 47.6f;
        profile.MaximumHz = 144f;
        profile.MaxFrameTimeIncreaseUs = 40000;

        Assert.Equal(48, bounds.Read(profile, ArcSyncField.MinimumHz));
        Assert.Equal(120, bounds.Read(profile, ArcSyncField.MaximumHz));
        Assert.Equal(25000, bounds.Read(profile, ArcSyncField.FrameTimeIncrease));
    }

    [Fact]
    public void AFieldWriteKeepsTheOtherFields()
    {
        CtlArcSyncProfileParams profile = default;
        profile.MinimumHz = 40;
        profile.MaximumHz = 100;
        profile.MaxFrameTimeIncreaseUs = 5000;
        profile.MaxFrameTimeDecreaseUs = 6000;

        var written = ArcSyncBounds.Apply(profile, ArcSyncField.FrameTimeDecrease, 7000);

        Assert.Equal((40f, 100f, 5000u, 7000u),
            (written.MinimumHz, written.MaximumHz, written.MaxFrameTimeIncreaseUs, written.MaxFrameTimeDecreaseUs));
    }

    [Fact]
    public void ClampingAnInvertedRangeFallsBackToTheMonitorsRange()
    {
        var bounds = ArcSyncBounds.From(Monitor(30f, 120f, 0, 0))!.Value;
        CtlArcSyncProfileParams profile = default;
        profile.MinimumHz = 130;
        profile.MaximumHz = 20;

        var clamped = bounds.Clamp(profile);

        Assert.Equal((30f, 120f), (clamped.MinimumHz, clamped.MaximumHz));
    }

    [Theory]
    [InlineData(30, 120, true)]
    [InlineData(60, 60, false)]
    public void TheDriverRangeDecidesWhetherThePublishedProfileOffersCustom(float minimum, float maximum, bool custom)
    {
        var driver = new FakeIgclDriver { MinimumHz = minimum, MaximumHz = maximum };
        using var session = driver.OpenSession();
        var output = new IgclOutput(3, Assert.Single(session.Adapters), 0, default, true);
        var display = ArcSyncDisplay.TryCreate(session, output, IntelLog.None, "Fixture")!;
        var control = new ArcSyncProfileControl(display, "display-fixture", new Placement("display", "refresh", 0));
        Assert.Equal(custom, control.Descriptor.Choices.Any(choice => choice.Value == "custom"));
        session.Dispose();
        GC.KeepAlive(driver);
    }

    private static CtlArcSyncMonitorParams Monitor(float minimum, float maximum, uint increase, uint decrease)
    {
        CtlArcSyncMonitorParams monitor = default;
        monitor.IsSupported = 1;
        monitor.MinimumHz = minimum;
        monitor.MaximumHz = maximum;
        monitor.MaxFrameTimeIncreaseUs = increase;
        monitor.MaxFrameTimeDecreaseUs = decrease;
        return monitor;
    }
}
