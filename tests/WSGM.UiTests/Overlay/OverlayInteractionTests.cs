using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayInteractionTests
{
    [AvaloniaFact]
    public void RefusedWakeSelectionRestoresUnchangedReadbackWithoutRetry()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        List<ManualWakeMode> requested = [];
        var vm = Assert.IsType<OverlayViewModel>(window.DataContext);
        window.KeepAwakeSelected += mode =>
        {
            requested.Add(mode);
            // A refused write leaves the old view-model value unchanged.
            window.RefreshPowerEditors(vm);
        };
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        var editor = UiFixture.Named<StackPanel>(window, "KeepAwakeHost")
            .GetVisualDescendants().OfType<ComboBox>().Single();
        UiFixture.Click(window, editor);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal([ManualWakeMode.Standby], requested);
        Assert.Equal(0, editor.SelectedIndex);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(requested);
    }

    [AvaloniaFact]
    public void TimeoutPopupKeepsItsDraftAcrossReadbackAndWritesOnlyAfterConfirmation()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        List<(PowerTimeoutKind Kind, int Seconds)> requested = [];
        window.PowerTimeoutSelected += (kind, seconds) => requested.Add((kind, seconds));
        var vm = Assert.IsType<OverlayViewModel>(window.DataContext);
        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?> { [PowerTimeoutKind.DisplayDc] = 60 };
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerTimeouts));
        var editor = UiFixture.Named<StackPanel>(window, "PowerTimeoutEditors")
            .GetVisualDescendants().OfType<ComboBox>().Single(choice => choice.IsEnabled);
        UiFixture.Click(window, editor);
        Assert.True(editor.IsDropDownOpen);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Down);
        var draft = editor.SelectedItem;
        Assert.Empty(requested);

        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?> { [PowerTimeoutKind.DisplayDc] = 60 };
        Assert.Equal(draft, editor.SelectedItem);
        UiFixture.Key(window, Key.Enter);
        Assert.False(editor.IsDropDownOpen);
        var request = Assert.Single(requested);
        Assert.Equal(PowerTimeoutKind.DisplayDc, request.Kind);
        Assert.Equal(300, request.Seconds);
    }

    [AvaloniaFact]
    public void WakePopupDoesNotApplyIntermediateModesWhileBrowsing()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        List<ManualWakeMode> requested = [];
        window.KeepAwakeSelected += requested.Add;
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        var editor = UiFixture.Named<StackPanel>(window, "KeepAwakeHost")
            .GetVisualDescendants().OfType<ComboBox>().Single();
        UiFixture.Click(window, editor);
        Assert.True(editor.IsDropDownOpen);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Down);
        Assert.Empty(requested);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal([ManualWakeMode.StandbyAndDisplay], requested);
    }

    [AvaloniaFact]
    public void WakeSelectorReportsOneUserChoiceAndNeverWritesItsReadback()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        List<ManualWakeMode> requested = [];
        window.KeepAwakeSelected += requested.Add;
        var vm = Assert.IsType<OverlayViewModel>(window.DataContext);
        vm.KeepAwakeManualMode = ManualWakeMode.Standby;
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        var editor = UiFixture.Named<StackPanel>(window, "KeepAwakeHost")
            .GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal(1, editor.SelectedIndex);
        Assert.Empty(requested);
        UiFixture.Click(window, editor);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal([ManualWakeMode.StandbyAndDisplay], requested);
        vm.KeepAwakeManualMode = ManualWakeMode.Off;
        Assert.Equal(0, editor.SelectedIndex);
        Assert.Single(requested);
    }

    [AvaloniaFact]
    public void TimeoutSelectorsRespectAvailableReadbackAndSendTheSelectedValue()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        List<(PowerTimeoutKind Kind, int Seconds)> requested = [];
        window.PowerTimeoutSelected += (kind, seconds) => requested.Add((kind, seconds));
        var vm = Assert.IsType<OverlayViewModel>(window.DataContext);
        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?> { [PowerTimeoutKind.DisplayDc] = 60 };
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerTimeouts));
        var editors = UiFixture.Named<StackPanel>(window, "PowerTimeoutEditors")
            .GetVisualDescendants().OfType<ComboBox>().ToArray();
        Assert.Equal(4, editors.Length);
        var editor = Assert.Single(editors, choice => choice.IsEnabled);
        Assert.Empty(requested);
        UiFixture.Click(window, editor);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        var selected = Assert.Single(requested);
        Assert.Equal(PowerTimeoutKind.DisplayDc, selected.Kind);
        Assert.NotEqual(60, selected.Seconds);
        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?>
            { [PowerTimeoutKind.DisplayDc] = selected.Seconds };
        Assert.Single(requested);
        Assert.Equal(PowerTimeouts.Describe(selected.Seconds), editor.SelectedItem?.ToString());
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CustomAssignmentIsSelectedOnlyForItsPowerSource(bool ac)
    {
        DevicePowerPresetReference custom = new()
        {
            PluginId = "fixture",
            PresetId = "custom",
            CustomValues = new DevicePowerCustomValues
                { SustainedWatts = 16, SlowWatts = 18, WindowsMode = DevicePowerMode.Balanced }
        };
        DevicePowerPresetReference balanced = new() { PluginId = "fixture", PresetId = "balanced" };
        ProfileConfig config = new()
        {
            Global = new ProfileValues
                { AcPowerPreset = ac ? custom : balanced, BatteryPowerPreset = ac ? balanced : custom }
        };
        DevicePowerPresets service = new(
            () => [Power(CapabilityRole.PowerSustainedLimit, 16), Power(CapabilityRole.PowerSlowLimit, 18)],
            (_, _, _, _, _, _) => throw new InvalidOperationException("Rendering must not write hardware"),
            new WindowsPowerModes(new ReadOnlyPowerModeApi()));
        DevicePowerAssignments assignments = new(service,
            () => new DevicePowerAssignmentContext(new ProfileSnapshot(config, ActiveProfile.None, 1), "fixture", 1,
                true, ac),
            (_, _, _) => throw new InvalidOperationException("Rendering must not save assignments"));
        using DevicePowerPresetSelection model = new(service, false, assignments);
        using UiFixture fixture = new();
        DevicePowerPresetView view = new();
        view.Attach(model);
        await model.RefreshAsync();
        var choices = view.GetLogicalDescendants().OfType<ComboBox>().ToArray();
        foreach (var choice in choices)
        {
            var isAc = Equals(choice.Tag, "device.power-assignment.ac");
            Assert.Equal(isAc == ac ? "custom" : "balanced", Assert.IsType<DevicePowerPreset>(choice.SelectedItem).Id);
            Assert.Equal(isAc == ac, choice.Items.Cast<DevicePowerPreset>().Any(item => item.Id == "custom"));
        }

        Assert.Equal(2, choices.Length);

        return;

        DeviceCapabilityView Power(CapabilityRole role, int watts)
        {
            return new DeviceCapabilityView(new CapabilityDescriptor
            {
                CapabilityId = role.ToString(),
                Role = role,
                InstanceId = null,
                Persistence = CapabilityPersistence.Volatile,
                ValueKind = CapabilityValueKind.Integer,
                Unit = CapabilityUnit.Watt,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                SupportsRead = true,
                SupportsWrite = true,
                Minimum = 8,
                Maximum = 37,
                Step = 1,
                PowerPresets = role == CapabilityRole.PowerSustainedLimit
                    ? [new DevicePowerPreset("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)]
                    : []
            }, new CapabilityProjection
            {
                State = new CapabilityState
                {
                    CapabilityId = role.ToString(),
                    Available = true,
                    Quality = HardwareStateQuality.Verified,
                    CycleGeneration = 1,
                    DescriptorGeneration = 1,
                    ObservedAt = DateTimeOffset.UtcNow,
                    ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts }
                }
            }, null);
        }
    }

    [AvaloniaFact]
    public void LosingIntegrationKeepsWindowsPlansOnTheOpenPowerPage()
    {
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power scheme write"));
        using FakeDevice device = new();
        device.State = device.State with
        {
            PluginSections =
            [
                new DeviceOverlayPluginSection(DeviceSections.PowerId, "Power", "", SectionIcon.Power, [])
                    { Key = SettingSectionKey.Power }
            ]
        };
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Named<StackPanel>(window, "SectionRail").Children.OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Power"));
        var plans = UiFixture.Named<StackPanel>(window, "DeviceWindowsPower");
        Assert.True(plans.IsVisible);
        device.State = device.State with { Visible = false };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.True(plans.IsVisible);
        Assert.True(UiFixture.Named<PowerSchemeView>(window, "DevicePowerSchemeHost").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task BatteryAssignmentDuringRefreshIsSavedOnFirstSelection()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        DeviceCapabilityView[] views =
            [Power(CapabilityRole.PowerSustainedLimit, 17), Power(CapabilityRole.PowerSlowLimit, 18)];
        var service = new DevicePowerPresets(() => views,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Inactive source must not write hardware"),
            new WindowsPowerModes(new ReadOnlyPowerModeApi()));
        ProfileConfig config = new();
        var saves = 0;
        var assignments = new DevicePowerAssignments(service,
            () => new DevicePowerAssignmentContext(new ProfileSnapshot(config, ActiveProfile.None, saves + 1),
                "fixture", 1, true, true),
            (_, ac, reference) =>
            {
                Assert.False(ac);
                config.Global.BatteryPowerPreset = reference;
                saves++;
                return Task.CompletedTask;
            });
        using var model = new DevicePowerPresetSelection(service, false, assignments);
        await model.RefreshAsync();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerPresets(model);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Named<StackPanel>(window, "SectionRail").Children.OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Power"));
        var dropdowns = window.GetVisualDescendants().OfType<ComboBox>().ToArray();
        Assert.DoesNotContain(dropdowns, control => Equals(control.Tag, "device.power-preset.choice"));
        var battery = dropdowns.Single(control => Equals(control.Tag, "device.power-assignment.battery"));
        await service.MutationGate.WaitAsync();
        var refresh = model.RefreshAsync();
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Changed += () =>
        {
            if (!model.Busy && saves > 0)
            {
                finished.TrySetResult();
            }
        };
        try
        {
            Assert.True(battery.IsEnabled);
            battery.SelectedIndex = 1;
            Assert.True(model.Busy);
        }
        finally
        {
            service.MutationGate.Release();
        }

        await Task.WhenAll(refresh, finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, saves);
        Assert.Equal("balanced", config.Global.BatteryPowerPreset?.PresetId);

        return;

        DeviceCapabilityView Power(CapabilityRole role, int watts)
        {
            return new DeviceCapabilityView(new CapabilityDescriptor
            {
                CapabilityId = role.ToString(),
                Role = role,
                Persistence = CapabilityPersistence.Volatile,
                ValueKind = CapabilityValueKind.Integer,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                SupportsRead = true,
                SupportsWrite = true,
                Unit = CapabilityUnit.Watt,
                Minimum = 8,
                Maximum = 37,
                Step = 1,
                PowerPresets = role == CapabilityRole.PowerSustainedLimit
                    ? [new DevicePowerPreset("balanced", "Balanced", 17, 18, DevicePowerMode.Balanced)]
                    : []
            }, new CapabilityProjection
            {
                State = new CapabilityState
                {
                    CapabilityId = role.ToString(),
                    Available = true,
                    Quality = HardwareStateQuality.Verified,
                    ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
                    CycleGeneration = 1,
                    DescriptorGeneration = 1,
                    ObservedAt = DateTimeOffset.UtcNow
                }
            }, null);
        }
    }

    [AvaloniaFact]
    public void KeyboardFocusBringsTheLastSteamRowIntoTheViewport()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamLaunchFixes));
        var last = UiFixture.Named<ActionButton>(window, "RemoveFixesButton");
        UiFixture.Named<ActionButton>(window, "DeelevateFixButton").Focus();
        for (var step = 0; step < 12 && !last.IsFocused; step++)
        {
            UiFixture.Key(window, Key.Tab);
        }

        Assert.True(last.IsFocused);
        Dispatcher.UIThread.RunJobs();
        var scroller = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        var position = last.TranslatePoint(default, scroller)!.Value;
        Assert.InRange(position.Y, 0, scroller.Bounds.Height - 1);
        Assert.True(position.Y + last.Bounds.Height <= scroller.Bounds.Height + 1);
    }

    [AvaloniaFact]
    public void QuickAccessPinsReportIntentThroughPointerAndKeyboard()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        Assert.True(UiFixture.Named<Control>(window, "PanelQuickAccess").IsVisible);
        List<string> pins = [];
        var home = 0;
        window.PinToggleRequested += pins.Add;
        window.HomeAppRequested += () => home++;
        var grid = UiFixture.Named<Panel>(window, "PinnedGrid");
        var card = Assert.IsType<ActionButton>(grid.Children[0]);
        UiFixture.Click(window, card);
        Assert.Equal(1, home);
        UiFixture.Click(window, card, MouseButton.Right);
        Assert.Equal(["home.steam"], pins);
        window.SetPins(["home.desktop"]);
        Assert.Single(grid.Children, control => control.IsEnabled);
        // The source row lives on Power's Session page since the Session tab was absorbed.
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerSession));
        var source = UiFixture.Named<ActionButton>(window, "HomeAppButton");
        source.Focus();
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(2, home);
    }

    [AvaloniaFact]
    public void PrimaryPageBackFocusesTheRailThenReturnsHomeBeforeDismissal()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Dispatcher.UIThread.RunJobs();
        var dismissed = 0;
        window.Dismissed += () => dismissed++;
        var entry = UiFixture.Rail(window, "device.section.overview");
        UiFixture.Click(window, entry);
        var heading = window.GetVisualDescendants().OfType<SectionPinHeader>()
            .Single(header => header.IsEffectivelyVisible).GetVisualDescendants().OfType<Button>().Single();
        Assert.True(heading.Focus());
        UiFixture.Key(window, Key.Escape);
        Assert.Equal(0, dismissed);
        Assert.Same(entry, window.FocusManager.GetFocusedElement());
        Assert.Contains("selected", entry.Classes);
        UiFixture.Key(window, Key.Escape);
        Assert.True(UiFixture.Named<Control>(window, "PanelQuickAccess").IsVisible);
        UiFixture.Key(window, Key.Escape);
        Assert.Equal(1, dismissed);
    }

    [AvaloniaTheory]
    [InlineData(1, "PanelSteam", "SteamLibrary", "PanelSteamLibrary")]
    [InlineData(2, "PanelSystem", "SystemDisplay", "PanelSystemDisplay")]
    [InlineData(3, "PanelPower", "PowerTimeouts", "PanelPowerTimeouts")]
    [InlineData(3, "PanelPower", "PowerSession", "PanelPowerSession")]
    public void ASectionRailKeepsTheSelectedPageVisibleWhileFocusMoves(
        int tab, string root, string section, string page)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, tab));
        Dispatcher.UIThread.RunJobs();
        var selected = UiFixture.Rail(window, section);
        UiFixture.Click(window, selected);
        Assert.False(UiFixture.Named<Control>(window, root).IsVisible);
        Assert.True(UiFixture.Named<Control>(window, page).IsVisible);
        Assert.Contains("selected", selected.Classes);
        var peer = UiFixture.Named<StackPanel>(window, "SectionRail").Children.OfType<Button>()
            .First(button => !ReferenceEquals(button, selected));
        Assert.True(peer.Focus());
        Assert.Contains("selected", selected.Classes);
        Assert.DoesNotContain("selected", peer.Classes);
        Assert.True(UiFixture.Named<Control>(window, page).IsVisible);

        UiFixture.Click(window, UiFixture.Tab(window, 0));
        UiFixture.Click(window, UiFixture.Tab(window, tab));
        Assert.True(UiFixture.Named<Control>(window, page).IsVisible);
        Assert.Contains("selected", UiFixture.Rail(window, section).Classes);
    }

    [AvaloniaFact]
    public void LeavingAPageOpenedInsideACategoryLandsBackOnThatCategory()
    {
        // The category is a level of its own, so backing out of a page reached from inside one has
        // to return to that category. Returning to the destination root instead would drop the user
        // two levels for one press and lose the group they were working in.
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerWake));
        UiFixture.Click(window, VisibleCard(window, "What's keeping this awake"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);

        UiFixture.Key(window, Key.Escape);
        Assert.False(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "PanelPowerWake").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PanelPower").IsVisible);
        UiFixture.Key(window, Key.Escape);
        Assert.True(UiFixture.Rail(window, OverlayPage.PowerWake).IsFocused);
        Assert.True(UiFixture.Named<Control>(window, "PanelPowerWake").IsVisible);
    }

    [AvaloniaFact]
    public void SwitchingDestinationClosesAnOpenCategoryAndItsNestedPage()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerWake));
        UiFixture.Click(window, VisibleCard(window, "What's keeping this awake"));
        Dispatcher.UIThread.RunJobs();

        UiFixture.Click(window, UiFixture.Tab(window, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.False(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PanelPowerWake").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PanelPower").IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "PanelSteamLibrary").IsVisible);
        Assert.Contains("selected", UiFixture.Rail(window, OverlayPage.SteamLibrary).Classes);
    }

    [AvaloniaFact]
    public void ResummoningAnOpenOverlayReturnsToItsSelectedSection()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerWake));
        UiFixture.Click(window, VisibleCard(window, "What's keeping this awake"));
        Assert.True(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);

        window.ResetForResummon();
        Dispatcher.UIThread.RunJobs();

        Assert.False(UiFixture.Named<Control>(window, "WakeLockHost").IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "PanelPowerWake").IsVisible);
        Assert.Contains("selected", UiFixture.Rail(window, OverlayPage.PowerWake).Classes);
    }

    private static ActionButton VisibleCard(OverlayWindow window, string title)
    {
        return window.GetVisualDescendants().OfType<ActionButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == title);
    }

    [AvaloniaFact]
    public void ClosingAndReopeningKeepsTheDestinationAndReleasesDeviceSubscriptions()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        for (var i = 0; i < 3; i++)
        {
            var window = fixture.Overlay();
            window.AttachDeviceBridge(device);
            Assert.Equal(1, device.Subscribers);
            // Section selection survives the window lifetime; device subscriptions do not.
            UiFixture.Click(window, UiFixture.Tab(window, 4));
            UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerSession));
            UiFixture.Named<ActionButton>(window, "DesktopButton").Focus(NavigationMethod.Directional);
            window.Close();
            Assert.Equal(0, device.Subscribers);
            device.Notify();
        }

        var reopened = fixture.Overlay();
        var power = UiFixture.Named<Control>(reopened, "PanelPowerSession");
        Assert.True(power.IsVisible);
        var focused = reopened.FocusManager.GetFocusedElement() as Control;
        Assert.NotNull(focused);
        Assert.Same(UiFixture.Rail(reopened, OverlayPage.PowerSession), focused);
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
        var observed = CancellationToken.None;
        device.State = device.State with
        {
            Capabilities = [device.State.Capabilities[0] with { CanInvoke = true }]
        };
        device.Invoke = async (_, token) =>
        {
            observed = token;
            await using var registration = token.Register(() => operation.TrySetCanceled(token));
            try
            {
                await operation.Task;
            }
            finally
            {
                completed.TrySetResult();
            }
        };
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        window.AttachPowerPresets(presets);
        Assert.NotNull(PrivateField<Delegate>(schemes, "Changed"));
        Assert.NotNull(PrivateField<Delegate>(presets, "Changed"));
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.overview"));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<DeviceSettingRow>()
            .Single(row => row.IsEffectivelyVisible && Equals(row.Content, "Processor temperature")).Editor);
        Assert.True(observed.CanBeCanceled);
        Assert.False(operation.Task.IsCompleted);

        window.SetPins(["home.steam"]);
        Assert.True(UiFixture.Named<Control>(window, "PinToast").IsVisible);
        var timer = Assert.IsType<DispatcherTimer>(PrivateField<DispatcherTimer>(window, "_pinToastTimer"));
        Assert.True(timer.IsEnabled);
        window.Close();

        Assert.False(timer.IsEnabled);
        Assert.Null(PrivateField<Delegate>(schemes, "Changed"));
        Assert.Null(PrivateField<Delegate>(presets, "Changed"));
        Assert.True(observed.IsCancellationRequested);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(operation.Task.IsCanceled);
        Assert.Equal(0, device.Subscribers);
    }

    // Inspect the actual owned resources without adding production-only test accessors.
    private static T? PrivateField<T>(object owner, string name) where T : class
    {
        return (T?)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(owner.GetType().Name, name)).GetValue(owner);
    }

    [AvaloniaFact]
    public async Task CorePowerSelectionStagesThenAppliesAndShowsFailureWithoutAPlugin()
    {
        using UiFixture fixture = new();
        FakePower api = new();
        using PowerSchemeSelection selection = new(new PowerSchemes(api), _ => { });
        await selection.RefreshAsync();
        var window = fixture.Overlay();
        window.AttachPowerSchemes(selection);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Dispatcher.UIThread.RunJobs();
        var combo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => Equals(control.Tag, "system.power-profile.choice"));
        combo.Focus();
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        UiFixture.Key(window, Key.Escape);
        Assert.False(combo.IsDropDownOpen);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(0, api.Writes);
        var apply = window.GetVisualDescendants().OfType<Button>()
            .Single(control => Equals(control.Tag, "system.power-profile.apply"));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeWrite = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(10)));
        };
        api.Reject = true;
        TaskCompletionSource finished = new();
        selection.Changed += () =>
        {
            if (!selection.Busy)
            {
                finished.TrySetResult();
            }
        };
        try
        {
            UiFixture.Click(window, apply);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(selection.Busy);
            Assert.False(combo.IsEnabled);
            Assert.False(apply.IsEnabled);
        }
        finally
        {
            release.TrySetResult();
        }

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, api.Writes);
        Assert.Contains("Refresh", selection.Status);
        Assert.False(apply.IsEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == selection.Status);
    }
}
