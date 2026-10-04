using WSGM.Overlay;

namespace WSGM.Tests.Overlay;

/// <summary>
///     Synthetic recognizer traces; these are not attended Claw calibration recordings. The jitter,
///     resting-start and inset-first-report traces reproduce rejection summaries from the 2026-09-26
///     Claw log, where 8 of 9 deliberate top swipes failed under the previous thresholds.
/// </summary>
public sealed class TouchSwipeMonitorTests
{
    [Theory]
    [InlineData(ScreenEdge.Bottom, 100, 100, 125, 35, 65)]
    [InlineData(ScreenEdge.Right, 100, 100, 35, 125, 65)]
    [InlineData(ScreenEdge.Left, 100, 100, 165, 35, 65)]
    [InlineData(ScreenEdge.Top, 100, 100, 35, 165, 65)]
    public void InwardDistanceUsesTheDirectionOppositeEachScreenEdge(
        object edge, int startX, int startY, int x, int y, int expected)
    {
        Assert.Equal(expected, TouchSwipeMonitor.InwardDistance((ScreenEdge)edge, startX, startY, x, y));
    }

    [Theory]
    [InlineData(true, false, true, false, 100, 100, 165, 100, ScreenEdge.Left)]
    [InlineData(true, false, true, false, 100, 100, 100, 35, ScreenEdge.Bottom)]
    [InlineData(false, true, false, true, 100, 100, 35, 100, ScreenEdge.Right)]
    [InlineData(false, true, false, true, 100, 100, 100, 165, ScreenEdge.Top)]
    public void PickTriggeredEdgeUsesTheDominantInwardDirectionAtCorners(
        bool bottom, bool right, bool left, bool top,
        int startX, int startY, int x, int y, object expected)
    {
        Assert.Equal((ScreenEdge)expected, TouchSwipeMonitor.PickTriggeredEdge(
            bottom, right, left, top, startX, startY, x, y, 48));
    }

    [Theory]
    [InlineData(140, 70)] // Neither edge has enough inward travel.
    [InlineData(165, 35)] // A diagonal has no dominant edge.
    public void PickTriggeredEdgeWaitsForEnoughDominantTravel(int x, int y)
    {
        Assert.Null(TouchSwipeMonitor.PickTriggeredEdge(
            true, false, true, false,
            100, 100, x, y, 48));
    }

    [Theory]
    [InlineData(ScreenEdge.Top, 640, 1, 641, 18, 643, 60)]
    [InlineData(ScreenEdge.Bottom, 640, 798, 641, 781, 643, 739)]
    [InlineData(ScreenEdge.Left, 1, 400, 18, 401, 60, 403)]
    [InlineData(ScreenEdge.Right, 1278, 400, 1261, 401, 1219, 403)]
    public void BezelSwipeTraceEntersQuicklyAndTriggersOnce(
        object expected, int startX, int startY, int entryX, int entryY, int endX, int endY)
    {
        var trace = Trace(startX, startY);
        Assert.Null(trace.Move(entryX, entryY, 35));
        Assert.Equal((ScreenEdge)expected, trace.Move(endX, endY, 110));
        Assert.Null(trace.Move(endX, endY, 120));
        Assert.Equal(35UL, trace.EntryMs);
    }

    [Fact]
    public void JitterAcrossAStraightSwipeDoesNotCountAsSidewaysTravel()
    {
        var trace = Trace(640, 0);
        Assert.Null(trace.Move(646, 6, 8));
        Assert.Null(trace.Move(640, 12, 16));
        Assert.Null(trace.Move(646, 18, 24));
        Assert.Null(trace.Move(640, 24, 32));
        Assert.Null(trace.Move(646, 30, 40));
        Assert.Null(trace.Move(640, 36, 48));
        Assert.Null(trace.Move(646, 42, 56));
        Assert.Equal(ScreenEdge.Top, trace.Move(640, 50, 64));
        Assert.Equal(48, trace.HorizontalTravel);
    }

    [Fact]
    public void FingerRestingOnTheBezelCanStillSwipeWithinTheEntryWindow()
    {
        var trace = Trace(640, 0);
        Assert.Null(trace.Move(641, 0, 100));
        Assert.Null(trace.Move(640, 1, 280));
        Assert.Null(trace.Move(641, 20, 320));
        Assert.Equal(ScreenEdge.Top, trace.Move(642, 70, 360));
        Assert.Equal(320UL, trace.EntryMs);
    }

    [Fact]
    public void FirstReportInsideTheStartZoneButOffTheEdgeStillSwipes()
    {
        var trace = Trace(640, 10);
        Assert.Equal(ScreenEdge.Top, trace.Move(640, 70, 80));
    }

    [Fact]
    public void StartZoneEndsAtItsConfiguredWidth()
    {
        Assert.Equal(ScreenEdge.Top, Trace(640, 21).Move(640, 90, 80));
        Assert.False(Trace(640, 22).HasCandidates);
    }

    [Fact]
    public void TitleBarDragBelowTheStartZoneIsNotAnEdgeSwipe()
    {
        var trace = Trace(640, 30);
        Assert.Null(trace.Move(640, 30, 60));
        Assert.Null(trace.Move(641, 51, 90));
        Assert.Null(trace.Move(642, 136, 170));
        Assert.Equal("outside-start-band", trace.Decision);
    }

    [Fact]
    public void SlowEdgeTouchTraceCannotTurnIntoASwipeAfterItsEntryWindow()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(640, 3, 60));
        Assert.Null(trace.Move(640, 8, 200));
        Assert.Null(trace.Move(640, 14, 400));
        Assert.Null(trace.Move(640, 30, 450));
        Assert.Null(trace.Move(640, 100, 500));
        Assert.Equal("late-entry", trace.Decision);
        Assert.Null(trace.EntryMs);
    }

    [Fact]
    public void FirstMoveAfterTheEntryWindowCannotQualifyEvenWhenItHasEnoughTravel()
    {
        var trace = Trace(640, 0);
        Assert.Null(trace.Move(640, 100, 401));
        Assert.Equal("late-entry", trace.Decision);
    }

    [Fact]
    public void SidewaysDragCannotRecoverByReturningToItsStartingColumn()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(660, 4, 30));
        Assert.Null(trace.Move(640, 60, 80));
        Assert.Equal("sideways-travel", trace.Decision);
    }

    [Fact]
    public void DiagonalTravelAfterEntryDoesNotTriggerWithoutTwoToOneDominance()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(641, 20, 30));
        Assert.Null(trace.Move(700, 60, 90));
        Assert.Null(trace.Move(740, 100, 150));
        Assert.Equal(30UL, trace.EntryMs);
        Assert.Equal("waiting", trace.Decision);
    }

    [Fact]
    public void AnUnenteredContactStillExpiresBeforeALateFinish()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(640, 10, 35));
        Assert.Null(trace.Move(640, 60, 801));
        Assert.Equal("expired", trace.Decision);
    }

    [Fact]
    public void CancelledTraceNeverTriggers()
    {
        var trace = Trace(640, 0);
        Assert.Null(trace.Move(640, 20, 30));
        trace.Cancel("second-contact");
        Assert.Null(trace.Move(640, 80, 60));
        Assert.False(trace.HasCandidates);
        Assert.Equal("second-contact", trace.Decision);
    }

    [Fact]
    public void DisabledEdgesNeverAdmitAContact()
    {
        var trace = new TouchSwipeMonitor.GestureTrace(640, 0, 1280, 800,
            22, 22, false, false, false, false);
        Assert.Null(trace.Move(640, 60, 70));
    }

    [Theory]
    [InlineData(-1, 400)]
    [InlineData(1280, 400)]
    [InlineData(640, -1)]
    [InlineData(640, 800)]
    public void ContactOutsideThePanelIsNotABezelStart(int x, int y)
    {
        var trace = Trace(x, y);
        Assert.False(trace.HasCandidates);
    }

    [Theory]
    [InlineData(172.0, 1920, 22)] // Claw 8 panel width: 2 mm.
    [InlineData(107.5, 1200, 22)] // Claw 8 panel height: 2 mm.
    [InlineData(0.0, 1200, 24)] // No physical size: 2 % of the axis.
    [InlineData(0.0, 1920, 38)]
    [InlineData(500.0, 800, 8)] // Clamped to the minimum.
    [InlineData(0.0, 4000, 48)] // Clamped to the maximum.
    public void StartBandIsTwoMillimetresOrTwoPercentWithoutAPhysicalSize(double spanMm, int screenPx,
        int expected)
    {
        Assert.Equal(expected, TouchSwipeMonitor.StartBandPx(spanMm, screenPx));
    }

    [Theory]
    [InlineData(0x11u, 0x0Eu, 0, 1720, 172.0)] // Centimetres, exponent -2.
    [InlineData(0x11u, 0xFEu, 0, 1720, 172.0)] // Exponent stored as a signed byte.
    [InlineData(0x11u, 0x0Fu, 0, 172, 172.0)] // Centimetres, exponent -1.
    [InlineData(0x13u, 0x0Eu, 0, 677, 171.958)] // Inches, exponent -2.
    [InlineData(0x00u, 0x00u, 0, 1920, 0.0)] // No unit.
    [InlineData(0x11u, 0x00u, 0, 1920, 0.0)] // 19.2 m is not a panel.
    [InlineData(0x11u, 0x0Eu, 0, 0, 0.0)] // Empty physical range.
    [InlineData(0x101u, 0x0Eu, 0, 1720, 0.0)] // Length combined with mass.
    public void PhysicalSpanReadsHidLengthUnitsAndRejectsImplausibleSizes(uint units, uint unitsExp,
        int physicalMin, int physicalMax, double expected)
    {
        Assert.Equal(expected, TouchSwipeMonitor.PhysicalSpanMm(units, unitsExp, physicalMin, physicalMax), 3);
    }

    private static TouchSwipeMonitor.GestureTrace Trace(int x, int y)
    {
        return new TouchSwipeMonitor.GestureTrace(x, y, 1280, 800, 22, 22,
            true, true, true, true);
    }
}
