using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.Tests;

/// <summary>Pure taskbar logic: edge-swipe routing and the in-place tile
/// reconciliation that keeps the focused button alive across refreshes.</summary>
public sealed class TaskbarTests
{
    [Theory]
    [InlineData(ScreenEdge.Bottom, EdgeAction.Taskbar, false, true)]
    [InlineData(ScreenEdge.Bottom, EdgeAction.Taskbar, true, false)] // desktop mode → quick access
    [InlineData(ScreenEdge.Bottom, EdgeAction.QuickAccess, false, false)]
    [InlineData(ScreenEdge.Right, EdgeAction.Taskbar, false, false)] // right edge is always quick access
    public void BottomSwipeOpensTheTaskbarOnlyWhenConfiguredAndInGameMode(
        ScreenEdge edge, EdgeAction bottomAction, bool explorerRunning, bool expected)
        => Assert.Equal(expected, OverlayController.OpensTaskbar(edge, bottomAction, explorerRunning));

    [Fact]
    public void NewConfigurationsDefaultTheBottomEdgeToTheTaskbar()
        => Assert.Equal(EdgeAction.Taskbar, new GestureConfig().BottomEdgeAction);

    [Theory]
    [InlineData(150, 100u, 100u, 150u)] // saved desktop scaling wins
    [InlineData(null, 100u, 150u, 150u)] // desktop already ran 100% → panel's recommended
    [InlineData(null, 175u, 150u, 175u)] // live desktop scaling beats recommended
    [InlineData(null, 100u, 100u, 100u)] // nothing known → no upscale
    [InlineData(99, 100u, 150u, 150u)] // garbage snapshot value is ignored
    [InlineData(600, 100u, 150u, 150u)]
    public void UiScaleUsesTheSavedDesktopScalingElseTheRecommendedPanelScale(
        int? saved, uint current, uint recommended, uint expected)
        => Assert.Equal(expected, DisplayScale.PickUiScalePercent(saved, current, recommended));

    private static WindowFinder.AppWindow Window(nint hwnd, string title, bool minimized = false)
        => new(hwnd, title, (uint)hwnd) { IsMinimized = minimized };

    private static TaskbarEntry Create(WindowFinder.AppWindow window)
        => new(window.Hwnd, window.Title, isSteam: false, icon: null);

    [Fact]
    public void ReconcileKeepsSurvivingTileInstancesAndUpdatesTheirStateInPlace()
    {
        var vm = new TaskbarViewModel();
        vm.Reconcile([Window(1, "Game"), Window(2, "Tool")], activeHwnd: 1, Create);
        var game = vm.Entries[0];
        var tool = vm.Entries[1];

        vm.Reconcile([Window(2, "Tool v2", minimized: true), Window(1, "Game")], activeHwnd: 2, Create);

        // Same instances (a rebuild would destroy the focused button), same stable
        // order (first-seen, not Z-order), fresh presentation state.
        Assert.Same(game, vm.Entries[0]);
        Assert.Same(tool, vm.Entries[1]);
        Assert.Equal("Tool v2", tool.Title);
        Assert.True(tool.IsMinimized);
        Assert.True(tool.IsActive);
        Assert.False(game.IsActive);
    }

    [Fact]
    public void ReconcileRemovesClosedWindowsAndAppendsNewOnesInEnumerationOrder()
    {
        var vm = new TaskbarViewModel();
        vm.Reconcile([Window(1, "A"), Window(2, "B")], activeHwnd: 0, Create);

        vm.Reconcile([Window(3, "C"), Window(2, "B"), Window(4, "D")], activeHwnd: 0, Create);

        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal((nint)2, vm.Entries[0].Hwnd); // survivor keeps its slot
        Assert.Equal((nint)3, vm.Entries[1].Hwnd); // new windows append in order
        Assert.Equal((nint)4, vm.Entries[2].Hwnd);
        Assert.True(vm.HasEntries);
    }

    [Fact]
    public void ReconcileWithNoWindowsEmptiesTheBarAndFlagsTheEmptyState()
    {
        var vm = new TaskbarViewModel();
        vm.Reconcile([Window(1, "A")], activeHwnd: 1, Create);

        vm.Reconcile([], activeHwnd: 0, Create);

        Assert.Empty(vm.Entries);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public void TaskbarEntryRaisesChangeNotificationsOnlyWhenValuesActuallyChange()
    {
        var entry = new TaskbarEntry(1, "Title", isSteam: false, icon: null);
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        entry.Title = "Title";
        entry.IsMinimized = false;
        Assert.Empty(changed);

        entry.Title = "New";
        entry.IsMinimized = true;
        entry.IsActive = true;
        Assert.Equal([nameof(TaskbarEntry.Title), nameof(TaskbarEntry.IsMinimized), nameof(TaskbarEntry.IsActive)], changed);
    }
}
