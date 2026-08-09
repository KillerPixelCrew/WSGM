using Avalonia.Input;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Tests;

public sealed class InputTests
{
    [Theory]
    [InlineData(0x24, "Home")]
    [InlineData(0x41, "A")]
    [InlineData(0x69, "Numpad 9")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0xFF, "Key 0xFF")]
    public void KeyNamesCoverNamedRangesAndFallbacks(int virtualKey, string expected)
        => Assert.Equal(expected, KeyRecorder.KeyName(virtualKey));

    [Fact]
    public void HotkeyDescriptionIncludesEnabledModifiersInDisplayOrder()
    {
        var text = KeyRecorder.Describe(new HotkeyConfig { Ctrl = true, Alt = true, Win = true, VirtualKey = 0x24 });

        Assert.Equal("Ctrl + Alt + Win + Home", text);
    }

    [Fact]
    public void DisabledHotkeyHasNoDescription()
        => Assert.Equal("None", KeyRecorder.Describe(new HotkeyConfig { Enabled = false, VirtualKey = 0x41 }));

    [Fact]
    public void GamepadDescriptionUsesStableButtonOrdering()
    {
        var buttons = GamepadButtons.Start | GamepadButtons.LeftShoulder | GamepadButtons.A;

        Assert.Equal("Hold A + LB + Start", GamepadService.Describe(buttons, hold: true));
    }

    [Theory]
    [InlineData(GamepadButtons.DPadUp, NavigationDirection.Up)]
    [InlineData(GamepadButtons.DPadDown, NavigationDirection.Down)]
    [InlineData(GamepadButtons.DPadLeft, NavigationDirection.Left)]
    [InlineData(GamepadButtons.DPadRight, NavigationDirection.Right)]
    public void DpadMapsToMatchingSpatialDirection(
        GamepadButtons buttons,
        NavigationDirection expected)
        => Assert.Equal(expected, GamepadNavigation.DirectionForButtons(buttons));

    [Fact]
    public void NonDirectionalButtonHasNoNavigationDirection()
        => Assert.Null(GamepadNavigation.DirectionForButtons(GamepadButtons.A));

    [Theory]
    [InlineData(50, 0, 100, 1, true, 51)]
    [InlineData(50, 0, 100, 5, false, 45)]
    [InlineData(100, 0, 100, 1, true, 100)]
    [InlineData(0, 0, 100, 1, false, 0)]
    [InlineData(50, 0, 100, 0, true, 51)]
    public void SliderControllerStepUsesTickAndClampsToRange(
        double value,
        double minimum,
        double maximum,
        double tickFrequency,
        bool increase,
        double expected)
        => Assert.Equal(
            expected,
            GamepadNavigation.AdjustSliderValue(value, minimum, maximum, tickFrequency, increase));

    [Theory]
    [InlineData(-1, 3, true, 0)]
    [InlineData(-1, 3, false, 2)]
    [InlineData(0, 3, false, 0)]
    [InlineData(1, 3, true, 2)]
    [InlineData(2, 3, true, 2)]
    [InlineData(0, 0, true, -1)]
    public void OpenDropdownControllerStepSelectsNearestItemAndStopsAtEdges(
        int selectedIndex,
        int itemCount,
        bool increase,
        int expected)
        => Assert.Equal(
            expected,
            GamepadNavigation.AdjustComboBoxIndex(selectedIndex, itemCount, increase));

    [Fact]
    public void ChordTrackerKeepsEachPadIndependentAndUnionsUntilRelease()
    {
        using var tracker = new ChordTracker();
        var released = new List<GamepadButtons>();
        tracker.Released += pad => released.Add(pad.Union);

        tracker.OnState(1, GamepadButtons.A);
        tracker.OnState(2, GamepadButtons.B);
        tracker.OnState(1, GamepadButtons.A | GamepadButtons.Start);
        tracker.OnState(1, 0);
        tracker.OnState(2, 0);

        Assert.Equal(
            [GamepadButtons.A | GamepadButtons.Start, GamepadButtons.B],
            released);
    }

    [Fact]
    public void ResetClearsAnInFlightChord()
    {
        using var tracker = new ChordTracker();
        GamepadButtons released = GamepadButtons.A;
        tracker.Released += pad => released = pad.Union;

        tracker.OnState(7, GamepadButtons.A | GamepadButtons.B);
        tracker.Reset();
        tracker.OnState(7, 0);

        Assert.Equal(0u, (uint)released);
    }
}
