using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.UiTests;

public sealed class OverlayInteractionTests
{
    [AvaloniaFact]
    public void KeyboardFocusBringsTheLastSteamRowIntoTheViewport()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        CardButton last = UiFixture.Named<CardButton>(window, "RemoveFixesButton");
        UiFixture.Named<CardButton>(window, "DeelevateFixButton").Focus();
        for (int step = 0; step < 12 && !last.IsFocused; step++)
        {
            UiFixture.Key(window, Key.Tab);
        }
        Assert.True(last.IsFocused);
        Dispatcher.UIThread.RunJobs();
        ScrollViewer scroller = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        Avalonia.Point position = last.TranslatePoint(default, scroller)!.Value;
        Assert.InRange(position.Y, 0, scroller.Bounds.Height - 1);
        Assert.True(position.Y + last.Bounds.Height <= scroller.Bounds.Height + 1);
    }

    [AvaloniaFact]
    public void QuickAccessPinsReportIntentThroughPointerAndKeyboard()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        Assert.True(UiFixture.Named<Control>(window, "PanelQuickAccess").IsVisible);
        List<string> pins = [];
        int home = 0;
        window.PinToggleRequested += pins.Add;
        window.HomeAppRequested += () => home++;
        var grid = UiFixture.Named<Panel>(window, "PinnedGrid");
        var card = Assert.IsType<CardButton>(grid.Children[0]);
        UiFixture.Click(window, card);
        Assert.Equal(1, home);
        UiFixture.Click(window, card, MouseButton.Right);
        Assert.Equal(["home.steam"], pins);
        window.SetPins(["home.desktop"]);
        Assert.Single(grid.Children, control => control.IsEnabled);
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        CardButton source = UiFixture.Named<CardButton>(window, "HomeAppButton");
        source.Focus();
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(2, home);
    }

    [AvaloniaFact]
    public void NestedBackRestoresFocusBeforeEscapeRequestsDismissal()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        Dispatcher.UIThread.RunJobs();
        int dismissed = 0;
        window.Dismissed += () => dismissed++;
        CardButton entry = window.GetVisualDescendants().OfType<CardButton>()
            .First(card => card.IsEffectivelyVisible && card.Title == "Overview");
        UiFixture.Click(window, entry);
        Assert.True(UiFixture.Named<Control>(window, "BackButton").IsVisible);
        UiFixture.Key(window, Key.Escape);
        Assert.Equal(0, dismissed);
        Assert.False(UiFixture.Named<Control>(window, "BackButton").IsVisible);
        Assert.Equal(entry.Tag, (window.FocusManager?.GetFocusedElement() as Control)?.Tag);
        UiFixture.Key(window, Key.Escape);
        Assert.True(UiFixture.Named<Control>(window, "PanelQuickAccess").IsVisible);
        UiFixture.Key(window, Key.Escape);
        Assert.Equal(1, dismissed);
    }

    [AvaloniaFact]
    public void ClosingAndReopeningKeepsSessionFocusAndReleasesDeviceSubscriptions()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        for (int i = 0; i < 3; i++)
        {
            OverlayWindow window = fixture.Overlay();
            window.AttachDeviceBridge(device);
            Assert.Equal(1, device.Subscribers);
            UiFixture.Click(window, UiFixture.Tab(window, 1));
            UiFixture.Named<CardButton>(window, "DesktopButton").Focus(NavigationMethod.Directional);
            window.Close();
            Assert.Equal(0, device.Subscribers);
            device.Notify();
        }
        OverlayWindow reopened = fixture.Overlay();
        Assert.True(UiFixture.Named<Control>(reopened, "PanelHome").IsVisible);
        Assert.Equal("home.desktop", (reopened.FocusManager?.GetFocusedElement() as Control)?.Tag);
    }

    [AvaloniaFact]
    public async Task CorePowerSelectionStagesThenAppliesAndShowsFailureWithoutAPlugin()
    {
        using UiFixture fixture = new();
        FakePower api = new();
        using PowerSchemeSelection selection = new(new PowerSchemes(api), _ => { });
        await selection.RefreshAsync();
        OverlayWindow window = fixture.Overlay();
        window.AttachPowerSchemes(selection);
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        Dispatcher.UIThread.RunJobs();
        var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(control => Equals(control.Tag, "system.power-profile.choice"));
        combo.Focus();
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        UiFixture.Key(window, Key.Escape);
        Assert.False(combo.IsDropDownOpen);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(0, api.Writes);
        Button apply = window.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Tag, "system.power-profile.apply"));
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeWrite = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        api.Reject = true;
        TaskCompletionSource finished = new();
        selection.Changed += () => { if (!selection.Busy) { finished.TrySetResult(); } };
        try
        {
            UiFixture.Click(window, apply);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(selection.Busy);
            Assert.False(combo.IsEnabled);
            Assert.False(apply.IsEnabled);
        }
        finally { release.Set(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, api.Writes);
        Assert.Contains("Refresh", selection.Status);
        Assert.False(apply.IsEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == selection.Status);
    }

    internal sealed class FakePower : IPowerSchemeApi
    {
        private static readonly Guid First = new("00000000-0000-0000-0000-000000000001");
        private static readonly Guid Second = new("00000000-0000-0000-0000-000000000002");
        private Guid _active = First;
        internal int Writes { get; private set; }
        internal bool Reject { get; set; }
        internal Action? BeforeWrite { get; set; }
        public Guid? Enumerate(uint index) => index switch { 0 => First, 1 => Second, _ => null };
        public string ReadName(Guid id) => id == First ? "Balanced" : "Power saver";
        public Guid ReadActive() => _active;
        public void SetActive(Guid id)
        {
            BeforeWrite?.Invoke();
            Writes++;
            if (Reject) { throw new Win32Exception(5); }
            _active = id;
        }
    }
}
