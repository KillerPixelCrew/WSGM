using WSGM.Core;
using WSGM.Overlay;
using WSGM.Settings;

namespace WSGM.Tests;

/// <summary>Pure taskbar logic: edge-swipe routing and the in-place tile
/// reconciliation that keeps the focused button alive across refreshes.</summary>
public sealed class TaskbarTests
{
    [Theory]
    [InlineData(ScreenEdge.Bottom, EdgeAction.Taskbar, false, OverlayController.SwipeAction.Taskbar)]
    [InlineData(ScreenEdge.Bottom, EdgeAction.Taskbar, true, OverlayController.SwipeAction.None)] // desktop: explorer owns the edge
    [InlineData(ScreenEdge.Bottom, EdgeAction.QuickAccess, false, OverlayController.SwipeAction.QuickAccess)]
    [InlineData(ScreenEdge.Bottom, EdgeAction.QuickAccess, true, OverlayController.SwipeAction.QuickAccess)]
    [InlineData(ScreenEdge.Right, EdgeAction.Taskbar, false, OverlayController.SwipeAction.QuickAccess)] // right edge is always quick access
    [InlineData(ScreenEdge.Right, EdgeAction.Taskbar, true, OverlayController.SwipeAction.QuickAccess)]
    [InlineData(ScreenEdge.Left, EdgeAction.Taskbar, false, OverlayController.SwipeAction.SteamMenu)]
    [InlineData(ScreenEdge.Left, EdgeAction.Taskbar, true, OverlayController.SwipeAction.SteamMenu)]
    [InlineData(ScreenEdge.Top, EdgeAction.Taskbar, false, OverlayController.SwipeAction.SteamQuickAccess)]
    [InlineData(ScreenEdge.Top, EdgeAction.Taskbar, true, OverlayController.SwipeAction.SteamQuickAccess)]
    public void EdgeSwipeRoutesToItsConfiguredSurface(
        ScreenEdge edge, EdgeAction bottomAction, bool explorerRunning, OverlayController.SwipeAction expected)
        => Assert.Equal(expected, OverlayController.DecideSwipe(edge, bottomAction, explorerRunning));

    [Fact]
    public void NewConfigurationsEnableEveryEdgeAndDefaultTheBottomToTheTaskbar()
    {
        var gestures = new GestureConfig();

        Assert.True(gestures.BottomEdge);
        Assert.True(gestures.RightEdge);
        Assert.True(gestures.LeftEdgeSteamMenu);
        Assert.True(gestures.TopEdgeSteamQuickAccess);
        Assert.Equal(EdgeAction.Taskbar, gestures.BottomEdgeAction);
    }

    [Theory]
    [InlineData(ScreenEdge.Bottom, 100, 100, 125, 35, 65)]
    [InlineData(ScreenEdge.Right, 100, 100, 35, 125, 65)]
    [InlineData(ScreenEdge.Left, 100, 100, 165, 35, 65)]
    [InlineData(ScreenEdge.Top, 100, 100, 35, 165, 65)]
    public void InwardDistanceUsesTheDirectionOppositeEachScreenEdge(
        ScreenEdge edge, int startX, int startY, int x, int y, int expected)
        => Assert.Equal(expected, TouchSwipeMonitor.InwardDistance(edge, startX, startY, x, y));

    [Theory]
    [InlineData(true, false, true, false, 100, 100, 165, 100, ScreenEdge.Left)]
    [InlineData(true, false, true, false, 100, 100, 100, 35, ScreenEdge.Bottom)]
    [InlineData(false, true, false, true, 100, 100, 35, 100, ScreenEdge.Right)]
    [InlineData(false, true, false, true, 100, 100, 100, 165, ScreenEdge.Top)]
    public void CornerSwipeUsesTheEdgeMatchingTheContactsDirection(
        bool bottom, bool right, bool left, bool top,
        int startX, int startY, int x, int y, ScreenEdge expected)
        => Assert.Equal(
            expected,
            TouchSwipeMonitor.PickTriggeredEdge(
                bottom, right, left, top, startX, startY, x, y, triggerDistance: 48));

    [Fact]
    public void CornerSwipeWaitsUntilOneDirectionCrossesTheTriggerDistance()
        => Assert.Null(
            TouchSwipeMonitor.PickTriggeredEdge(
                bottomCandidate: true,
                rightCandidate: false,
                leftCandidate: true,
                topCandidate: false,
                startX: 100,
                startY: 100,
                x: 140,
                y: 70,
                triggerDistance: 48));

    // Each switch is exercised at its NON-default value in one of the two cases:
    // both default to true, so asserting a true round trip would also pass if the
    // snapshot never read the view model at all.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingsSnapshotPersistsLeftAndTopSteamGestureSwitchesIndependently(
        bool left, bool top)
    {
        var viewModel = new SettingsViewModel(new AppConfig());
        viewModel.GestureLeftSteamMenu = left;
        viewModel.GestureTopSteamQuickAccess = top;

        var snapshot = viewModel.SnapshotForPreview();

        Assert.Equal(left, snapshot.Gestures.LeftEdgeSteamMenu);
        Assert.Equal(top, snapshot.Gestures.TopEdgeSteamQuickAccess);
    }

    [Theory]
    [InlineData(BigPictureShortcut.SteamMenu, 0x31)]
    [InlineData(BigPictureShortcut.QuickAccess, 0x32)]
    public void BigPictureMenuShortcutsMatchSteamsKeyboardSimulator(
        BigPictureShortcut shortcut, ushort expected)
        => Assert.Equal(expected, Steam.ShortcutVirtualKey(shortcut));

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

    [Theory]
    [InlineData(100, 100)]
    [InlineData(113, 125)]
    [InlineData(275, 250)]
    [InlineData(490, 500)]
    public void ConfiguredDpiUsesAValueSupportedByTheDisplayConfigPacket(int requested, int expected)
        => Assert.Equal(expected, DisplayScale.NormalizeConfiguredPercent(requested));

    [Fact]
    public void ANewDockDisplayIsNotLoweredWhileAnotherDisplaysRecoverySnapshotSurvives()
        => Assert.False(DisplayScale.ShouldLowerDisplay(
            freshCapture: false,
            [new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }],
            @"\\.\DISPLAY2"));

    [Fact]
    public void ADisplayAlreadyOwnedByTheRecoverySnapshotCanBeLoweredAgain()
        => Assert.True(DisplayScale.ShouldLowerDisplay(
            freshCapture: false,
            [new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }],
            @"\\.\display1"));

    [Fact]
    public void AFreshCaptureCanLowerEveryIdentifiedDisplay()
        => Assert.True(DisplayScale.ShouldLowerDisplay(
            freshCapture: true,
            [],
            @"\\.\DISPLAY2"));

    // ---- Tray width budget: the right zone must never grow past the bar ----

    [Theory]
    [InlineData(1280.0, 1.0, 379.2)] // 1280 px bar, no touch transform
    [InlineData(1920.0, 1.0, 571.2)]
    [InlineData(1280.0, 1.5, 251.2)] // RootScale 1.5x shrinks the inner layout width
    [InlineData(0.0, 1.0, 40.0)] // degenerate inputs never fall below one tray tile
    [InlineData(1280.0, 0.0, 40.0)]
    [InlineData(double.NaN, 1.0, 40.0)]
    public void TheTrayStripIsCappedAtAFractionOfTheBarsInnerWidth(double width, double scale, double expected)
        => Assert.Equal(expected, TaskbarWindow.ComputeTrayMaxWidth(width, scale), 3);

    [Fact]
    public void TheCappedTrayLeavesTheFixedStatusClusterAndAUsableTileStrip()
    {
        // Fixed right-zone cost at 1280 px logical, added up from the XAML:
        // Audio 36 + Wi-Fi 36 + Bluetooth 36 + battery ~57 + clock ~67 + 4x4
        // spacing = ~248, plus the 9 px separator and the two 4 px gaps around it
        // = ~265. The home
        // button and the bar's 2x8 padding take ~92 more.
        const double bar = 1280;
        const double statusCluster = 265;
        const double homeAndPadding = 92;

        var tray = TaskbarWindow.ComputeTrayMaxWidth(bar, 1.0);
        var tiles = bar - tray - statusCluster - homeAndPadding;

        // Before the cap an unbounded tray (17+ icons ≈ 646 px) pushed the status
        // cluster off the right edge. Capped, the cluster always fits and the tile
        // strip still shows a useful number of 48 px tiles.
        Assert.True(tiles > 0, $"status cluster does not fit (tile band {tiles:0.#} px)");
        Assert.True(tiles > 48 * 8, $"tile strip squeezed to {tiles:0.#} px");
    }

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
