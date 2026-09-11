using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class OverlayInteractionTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomAssignmentIsSelectedOnlyForItsPowerSource(bool ac)
    {
        DeviceCapabilityView Power(CapabilityRole role, int watts) => new(new CapabilityDescriptor
        {
            CapabilityId = role.ToString(),
            Role = role,
            InstanceId = null,
            Persistence = CapabilityPersistence.Volatile,
            ValueKind = CapabilityValueKind.Integer,
            Unit = CapabilityUnit.Watt,
            Display = new() { Key = DisplayKey.SustainedPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 37,
            Step = 1,
            PowerPresets = role == CapabilityRole.PowerSustainedLimit
                ? [new("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)] : [],
        }, new CapabilityProjection
        {
            State = new()
            {
                CapabilityId = role.ToString(),
                Available = true,
                Quality = HardwareStateQuality.Verified,
                CycleGeneration = 1,
                DescriptorGeneration = 1,
                ObservedAt = DateTimeOffset.UtcNow,
                ObservedValue = new() { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
            },
        }, null);
        DevicePowerPresetReference custom = new()
        {
            PluginId = "fixture",
            PresetId = "custom",
            CustomValues = new() { SustainedWatts = 16, SlowWatts = 18, WindowsMode = DevicePowerMode.Balanced },
        };
        DevicePowerPresetReference balanced = new() { PluginId = "fixture", PresetId = "balanced" };
        PerformanceConfig config = new() { AcPowerPreset = ac ? custom : balanced, BatteryPowerPreset = ac ? balanced : custom };
        DevicePowerPresets service = new(() => [Power(CapabilityRole.PowerSustainedLimit, 16), Power(CapabilityRole.PowerSlowLimit, 18)],
            (_, _, _, _, _, _) => throw new InvalidOperationException("Rendering must not write hardware"),
            new WindowsPowerModes(new BalancedModeApi()));
        DevicePowerAssignments assignments = new(service, () => new(config, null, "fixture", 1, true, ac),
            (_, _, _) => throw new InvalidOperationException("Rendering must not save assignments"));
        using DevicePowerPresetSelection model = new(service, false, assignments);
        using UiFixture fixture = new();
        DevicePowerPresetView view = new();
        view.Attach(model);
        await model.RefreshAsync();
        var choices = view.GetLogicalDescendants().OfType<ComboBox>().ToArray();
        foreach (var choice in choices)
        {
            bool isAc = Equals(choice.Tag, "device.power-assignment.ac");
            Assert.Equal(isAc == ac ? "custom" : "balanced", Assert.IsType<DevicePowerPreset>(choice.SelectedItem).Id);
            Assert.Equal(isAc == ac, choice.Items.Cast<DevicePowerPreset>().Any(item => item.Id == "custom"));
        }
        Assert.Equal(2, choices.Length);
    }

    [AvaloniaFact]
    public void LosingIntegrationExpandsWindowsPlansOnTheOpenPowerPage()
    {
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power scheme write"));
        using FakeDevice device = new();
        device.State = device.State with
        {
            PluginSections = [new DeviceOverlayPluginSection(DeviceSections.PowerId, "Power", "", SectionIcon.Power, [])
            { Key = WSGM.Device.Sdk.Settings.SettingSectionKey.Power }],
        };
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Power"));
        Expander plans = UiFixture.Named<Expander>(window, "DeviceWindowsPower");
        Assert.True(plans.IsVisible);
        plans.IsExpanded = false;
        device.State = device.State with { Visible = false };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.True(plans.IsVisible);
        Assert.True(plans.IsExpanded);
    }

    [AvaloniaFact]
    public async Task BatteryAssignmentDuringRefreshIsSavedOnFirstSelection()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        DeviceCapabilityView Power(CapabilityRole role, int watts) => new(new CapabilityDescriptor
        {
            CapabilityId = role.ToString(),
            Role = role,
            Persistence = CapabilityPersistence.Volatile,
            ValueKind = CapabilityValueKind.Integer,
            Display = new() { Key = DisplayKey.SustainedPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Unit = CapabilityUnit.Watt,
            Minimum = 8,
            Maximum = 37,
            Step = 1,
            PowerPresets = role == CapabilityRole.PowerSustainedLimit
                ? [new("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)] : [],
        }, new CapabilityProjection
        {
            State = new CapabilityState
            {
                CapabilityId = role.ToString(),
                Available = true,
                Quality = HardwareStateQuality.Verified,
                ObservedValue = new() { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
                CycleGeneration = 1,
                DescriptorGeneration = 1,
                ObservedAt = DateTimeOffset.UtcNow,
            }
        }, null);
        DeviceCapabilityView[] views = [Power(CapabilityRole.PowerSustainedLimit, 17), Power(CapabilityRole.PowerSlowLimit, 18)];
        var service = new DevicePowerPresets(() => views,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Inactive source must not write hardware"),
            new WindowsPowerModes(new BalancedModeApi()));
        PerformanceConfig config = new();
        int saves = 0;
        var assignments = new DevicePowerAssignments(service, () => new(config, null, "fixture", 1, true, true),
            (_, ac, reference) => { Assert.False(ac); config.BatteryPowerPreset = reference; saves++; return Task.CompletedTask; });
        using var model = new DevicePowerPresetSelection(service, false, assignments);
        await model.RefreshAsync();
        OverlayWindow window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerPresets(model);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Power"));
        var dropdowns = window.GetVisualDescendants().OfType<ComboBox>().ToArray();
        Assert.DoesNotContain(dropdowns, control => Equals(control.Tag, "device.power-preset.choice"));
        var battery = dropdowns.Single(control => Equals(control.Tag, "device.power-assignment.battery"));
        await service.MutationGate.WaitAsync();
        Task refresh = model.RefreshAsync();
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Changed += () => { if (!model.Busy && saves > 0) { finished.TrySetResult(); } };
        try
        {
            Assert.True(battery.IsEnabled);
            battery.SelectedIndex = 1;
            Assert.True(model.Busy);
        }
        finally { service.MutationGate.Release(); }
        await Task.WhenAll(refresh, finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, saves);
        Assert.Equal("balanced", config.BatteryPowerPreset?.PresetId);
    }

    private sealed class BalancedModeApi : IPowerModeApi
    {
        public Guid Read() => Guid.Empty;
        public void Set(Guid mode) => throw new InvalidOperationException("Inactive source must not change Windows mode");
    }

    [AvaloniaFact]
    public void KeyboardFocusBringsTheLastSteamRowIntoTheViewport()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        // The Steam root is a menu now; the launch fixes are one level down.
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Per-game launch fixes"));
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
        // The source row lives on Power's Session page since the Session tab was absorbed.
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, VisibleCard(window, "Session"));
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
        UiFixture.Click(window, UiFixture.Tab(window, 2));
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

    [AvaloniaTheory]
    [InlineData(1, "PanelSteam", "Steam library", "PanelSteamLibrary")]
    [InlineData(2, "PanelSystem", "Display", "PanelSystemDisplay")]
    [InlineData(3, "PanelPower", "Idle timeouts", "PanelPowerTimeouts")]
    [InlineData(3, "PanelPower", "Session", "PanelPowerSession")]
    public void AGroupedTabOffersItsControlsBehindACategory(
        int tab, string root, string category, string page)
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, tab));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, root).IsVisible);
        Assert.False(UiFixture.Named<Control>(window, page).IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "BackButton").IsVisible);

        UiFixture.Click(window, VisibleCard(window, category));
        Assert.False(UiFixture.Named<Control>(window, root).IsVisible);
        Assert.True(UiFixture.Named<Control>(window, page).IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "BackButton").IsVisible);

        UiFixture.Key(window, Key.Escape);
        Assert.True(UiFixture.Named<Control>(window, root).IsVisible);
        Assert.False(UiFixture.Named<Control>(window, page).IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "BackButton").IsVisible);
    }

    [AvaloniaFact]
    public void LeavingAPageOpenedInsideACategoryLandsBackOnThatCategory()
    {
        // The category is a level of its own, so backing out of a page reached from inside one has
        // to return to that category. Returning to the destination root instead would drop the user
        // two levels for one press and lose the group they were working in.
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, VisibleCard(window, "Wake"));
        UiFixture.Click(window, VisibleCard(window, "What's keeping this awake"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);

        UiFixture.Key(window, Key.Escape);
        Assert.False(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "PanelPowerWake").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PanelPower").IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "BackButton").IsVisible);

        UiFixture.Key(window, Key.Escape);
        Assert.True(UiFixture.Named<Control>(window, "PanelPower").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "BackButton").IsVisible);
    }

    [AvaloniaFact]
    public void SwitchingDestinationClosesAnOpenCategoryAndItsNestedPage()
    {
        using UiFixture fixture = new();
        OverlayWindow window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, VisibleCard(window, "Wake"));
        UiFixture.Click(window, VisibleCard(window, "What's keeping this awake"));
        Dispatcher.UIThread.RunJobs();

        UiFixture.Click(window, UiFixture.Tab(window, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.False(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PanelPowerWake").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PanelPower").IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "PanelSteam").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "BackButton").IsVisible);
    }

    private static CardButton VisibleCard(OverlayWindow window, string title) =>
        window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == title);

    [AvaloniaFact]
    public void ClosingAndReopeningKeepsTheDestinationAndReleasesDeviceSubscriptions()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        for (int i = 0; i < 3; i++)
        {
            OverlayWindow window = fixture.Overlay();
            window.AttachDeviceBridge(device);
            Assert.Equal(1, device.Subscribers);
            // Power, then its Session page: the rows this used to reach on a root tab of their
            // own. Reopening restores the destination, not a nested page, so the assertion below
            // is on the Power root and on focus landing inside it.
            UiFixture.Click(window, UiFixture.Tab(window, 4));
            UiFixture.Click(window, VisibleCard(window, "Session"));
            UiFixture.Named<CardButton>(window, "DesktopButton").Focus(NavigationMethod.Directional);
            window.Close();
            Assert.Equal(0, device.Subscribers);
            device.Notify();
        }
        OverlayWindow reopened = fixture.Overlay();
        Control power = UiFixture.Named<Control>(reopened, "PanelPower");
        Assert.True(power.IsVisible);
        Control? focused = reopened.FocusManager?.GetFocusedElement() as Control;
        Assert.NotNull(focused);
        Assert.Contains(power, focused.GetVisualAncestors());
    }

    [AvaloniaFact]
    public async Task ClosingStopsTheToastDetachesPowerHostsAndCancelsTheDeviceAction()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power scheme write"));
        DevicePowerPresets service = new(() => [],
            (_, _, _, _, _, _) => throw new InvalidOperationException("Unexpected preset write"),
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
        UiFixture.Click(window, UiFixture.Tab(window, 2));
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
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Power"));
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
