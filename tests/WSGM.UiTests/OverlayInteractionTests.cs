using System.ComponentModel;
using System.Reflection;
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
using WSGM.Shell;

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
    public async Task ClosingStopsTheToastDetachesPowerHostsAndCancelsTheDeviceAction()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power scheme write"));
        DevicePowerPresets service = new(() => [],
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected preset write"),
            new WindowsPowerModes(new UnusedPowerModeApi()));
        using DevicePowerPresetSelection presets = new(service, false);
        TaskCompletionSource operation = new();
        TaskCompletionSource completed = new();
        CancellationToken observed = default;
        device.State = device.State with
        {
            Capabilities = [device.State.Capabilities[0] with { CanInvoke = true }],
        };
        device.Invoke = async (_, token) =>
        {
            observed = token;
            using var registration = token.Register(() => operation.TrySetCanceled(token));
            try { await operation.Task; }
            finally { completed.TrySetResult(); }
        };
        OverlayWindow window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        window.AttachPowerPresets(presets);
        Assert.NotNull(PrivateField<Delegate>(schemes, "Changed"));
        Assert.NotNull(PrivateField<Delegate>(presets, "Changed"));
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Overview"));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Processor temperature"));
        Assert.True(observed.CanBeCanceled);
        Assert.False(operation.Task.IsCompleted);

        window.SetPins(["home.steam"]);
        Assert.True(UiFixture.Named<Control>(window, "PinToast").IsVisible);
        DispatcherTimer timer = Assert.IsType<DispatcherTimer>(PrivateField<DispatcherTimer>(window, "_pinToastTimer"));
        Assert.True(timer.IsEnabled);
        window.Close();

        Assert.False(timer.IsEnabled);
        Assert.Null(PrivateField<Delegate>(schemes, "Changed"));
        Assert.Null(PrivateField<Delegate>(presets, "Changed"));
        Assert.True(observed.IsCancellationRequested);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(operation.Task.IsCanceled);
        Assert.Equal(0, device.Subscribers);
    }

    // Inspect the actual owned resources without adding production-only test accessors.
    private static T? PrivateField<T>(object owner, string name) where T : class =>
        (T?)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().Name, name)).GetValue(owner);

    private sealed class UnusedPowerModeApi : IPowerModeApi
    {
        public Guid Read() => throw new InvalidOperationException("Unexpected Windows power-mode read");
        public void Set(Guid mode) => throw new InvalidOperationException("Unexpected Windows power-mode write");
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
