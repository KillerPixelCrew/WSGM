using WSGM.Core;
using WSGM.Overlay;
using WSGM.Settings;

namespace WSGM.Tests.Overlay;

/// <summary>Synthetic recognizer traces; these are not attended Claw calibration recordings.</summary>
public sealed class TouchSwipeMonitorTests
{
    [Theory]
    [InlineData(ScreenEdge.Bottom, 100, 100, 125, 35, 65)]
    [InlineData(ScreenEdge.Right, 100, 100, 35, 125, 65)]
    [InlineData(ScreenEdge.Left, 100, 100, 165, 35, 65)]
    [InlineData(ScreenEdge.Top, 100, 100, 35, 165, 65)]
    public void InwardDistanceUsesTheDirectionOppositeEachScreenEdge(
        ScreenEdge edge, int startX, int startY, int x, int y, int expected)
    {
        Assert.Equal(expected, TouchSwipeMonitor.InwardDistance(edge, startX, startY, x, y));
    }

    [Theory]
    [InlineData(true, false, true, false, 100, 100, 165, 100, ScreenEdge.Left)]
    [InlineData(true, false, true, false, 100, 100, 100, 35, ScreenEdge.Bottom)]
    [InlineData(false, true, false, true, 100, 100, 35, 100, ScreenEdge.Right)]
    [InlineData(false, true, false, true, 100, 100, 100, 165, ScreenEdge.Top)]
    public void PickTriggeredEdgeUsesTheDominantInwardDirectionAtCorners(
        bool bottom, bool right, bool left, bool top,
        int startX, int startY, int x, int y, ScreenEdge expected)
    {
        Assert.Equal(expected, TouchSwipeMonitor.PickTriggeredEdge(
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
    [InlineData(ScreenEdge.Top, 640, 1, 641, 10, 643, 60)]
    [InlineData(ScreenEdge.Bottom, 640, 798, 641, 789, 643, 739)]
    [InlineData(ScreenEdge.Left, 1, 400, 10, 401, 60, 403)]
    [InlineData(ScreenEdge.Right, 1278, 400, 1269, 401, 1219, 403)]
    public void BezelSwipeTraceEntersQuicklyAndTriggersOnce(
        ScreenEdge expected, int startX, int startY, int entryX, int entryY, int endX, int endY)
    {
        var trace = Trace(startX, startY);
        Assert.Null(trace.Move(entryX, entryY, 35));
        Assert.Equal(expected, trace.Move(endX, endY, 110));
        Assert.Null(trace.Move(endX, endY, 120));
        Assert.Equal(35UL, trace.EntryMs);
    }

    [Fact]
    public void MaximizedTitleBarDragTraceNeverEntersTheStartZone()
    {
        var trace = Trace(640, 24);
        Assert.Null(trace.Move(640, 24, 60));
        Assert.Null(trace.Move(641, 45, 90));
        Assert.Null(trace.Move(642, 130, 170));
        Assert.Equal("outside-start-band", trace.Decision);
    }

    [Fact]
    public void SlowEdgeTouchTraceCannotTurnIntoASwipeAfterItsEntryDeadline()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(640, 2, 60));
        Assert.Null(trace.Move(640, 4, 120));
        Assert.Null(trace.Move(640, 30, 200));
        Assert.Null(trace.Move(640, 100, 300));
        Assert.Equal("late-entry", trace.Decision);
        Assert.Null(trace.EntryMs);
    }

    [Fact]
    public void FirstMoveAfterTheDeadlineCannotQualifyEvenWhenItHasEnoughTravel()
    {
        var trace = Trace(640, 0);
        Assert.Null(trace.Move(640, 100, 121));
        Assert.Equal("late-entry", trace.Decision);
    }

    [Fact]
    public void SidewaysDragCannotRecoverByReturningToItsStartingColumn()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(650, 4, 30));
        Assert.Null(trace.Move(640, 60, 80));
        Assert.Equal("sideways-travel", trace.Decision);
    }

    [Fact]
    public void TravelAfterEarlyEntryMustStillDominateAccumulatedSidewaysMotion()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(640, 12, 30));
        Assert.Null(trace.Move(650, 35, 60));
        Assert.Null(trace.Move(640, 42, 90));
        Assert.Null(trace.Move(650, 51, 110));
        Assert.Equal("sideways-travel", trace.Decision);
    }

    [Fact]
    public void AnEarlyEntryStillExpiresBeforeALateFinish()
    {
        var trace = Trace(640, 1);
        Assert.Null(trace.Move(640, 10, 35));
        Assert.Null(trace.Move(640, 60, 801));
        Assert.Equal("expired", trace.Decision);
    }

    [Fact]
    public void DisabledEdgesNeverAdmitAContact()
    {
        var trace = new TouchSwipeMonitor.GestureTrace(640, 0, 1280, 800,
            4, false, false, false, false);
        Assert.Null(trace.Move(640, 60, 70));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 8)]
    [InlineData(48, 8)]
    public void ConfiguredStartWidthIsClampedWithoutRewritingTheSavedPreference(int configured, int effective)
    {
        var config = new AppConfig { Gestures = new GestureConfig { StripThickness = configured } };
        var snapshot = new SettingsViewModel(config).SnapshotForPreview();

        Assert.Equal(configured, snapshot.Gestures.StripThickness);
        Assert.Equal(effective, TouchSwipeMonitor.NormalizeStartBand(configured));
        var inside = Trace(640, effective - 1, configured);
        var outside = Trace(640, effective, configured);
        Assert.Equal(ScreenEdge.Top, inside.Move(640, 70, 80));
        Assert.Null(outside.Move(640, 70, 80));
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

    private static TouchSwipeMonitor.GestureTrace Trace(int x, int y, int stripThickness = 4)
    {
        return new TouchSwipeMonitor.GestureTrace(x, y, 1280, 800, stripThickness,
            true, true, true, true);
    }
}
