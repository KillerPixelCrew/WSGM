using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Labs.Panels;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class DeviceRowReconciliationTests
{
    [AvaloniaFact]
    public void ChoiceEditorAndPopupUseTheCompactDeckTheme()
    {
        using var fixture = new UiFixture();
        var row = DeviceControlRows.Choice("choice", "Fan mode", "", Choices(), "first", true, _ => { });
        var window = new Window { Content = row, Width = 600, Height = 200, Classes = { "command-deck" } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var combo = Assert.IsType<ComboBox>(row.Editor);
            Assert.Equal(36, combo.Bounds.Height);
            Assert.Equal(VerticalAlignment.Center, combo.VerticalContentAlignment);
            Assert.Equal(Color.Parse("#303741"),
                Assert.IsAssignableFrom<ISolidColorBrush>(combo.Background).Color);
            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            var border = Assert.IsType<Border>(popup.Child);
            Assert.Equal(Color.Parse("#303741"),
                Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
            combo.IsDropDownOpen = false;
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FanPresetsBecomeInteractiveWhenCurveReadbackBecomesAvailable()
    {
        using var fixture = new UiFixture();
        var writes = 0;
        CurvePoint[] points = [new(40, 20), new(90, 100)];
        var row = new DeviceCurveRow("fan", "Fan curve", "", points, null, false, _ => writes++);
        var window = new Window { Content = row, Width = 600, Height = 400, Classes = { "command-deck" } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var presets = row.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(3, presets.Length);
            Assert.All(presets, button => Assert.False(button.IsEffectivelyEnabled));
            row.RefreshReadback(points, 50, true);
            Assert.All(presets, button => Assert.True(button.IsEffectivelyEnabled));
            UiFixture.Click(window, presets[0]);
            window.Content = null;
            Assert.Equal(1, writes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AnActionSubclassUsesTheStandardButtonTemplate()
    {
        using var fixture = new UiFixture();
        var row = new ThemeProbeRow();
        Assert.Equal(typeof(Button), row.ResolvedStyleKey);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DetachingFlushesAnEditOnceUnlessAvailabilityWasWithdrawn(bool curve, bool withdrawn)
    {
        using var fixture = new UiFixture();
        var writes = 0;
        Control row = curve
            ? new DeviceCurveRow("edit", "Fan curve", "", [new CurvePoint(40, 20), new CurvePoint(90, 100)],
                null, true, _ => writes++)
            : new DeviceSliderRow("edit", "Power", "", 8, 37, 1, CapabilityUnit.Watt, 15, true, _ => writes++);
        var window = new Window { Content = row, Width = 800, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            if (row is DeviceCurveRow fan)
            {
                UiFixture.Click(window, fan.GetVisualDescendants().OfType<Button>()
                    .First(button => button.Tag is string tag && tag.Contains(".preset.")));
                if (withdrawn)
                {
                    fan.RefreshReadback([], null, false);
                }
            }
            else
            {
                var slider = Assert.IsType<DeviceSliderRow>(row);
                Assert.IsType<Slider>(slider.FocusTarget).Value = 25;
                if (withdrawn)
                {
                    slider.RefreshReadback(8, 37, 1, 15, false);
                }
            }

            Assert.Equal(0, writes);
            window.Content = null;
            Assert.Equal(withdrawn ? 0 : 1, writes);
            window.Content = row;
            window.Content = null;
            Assert.Equal(withdrawn ? 0 : 1, writes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RebuildingDeviceRowsFlushesOnlyStillValidUserIntent(int change)
    {
        using var fixture = new UiFixture();
        using var device = new FakeDevice();
        List<DeviceOverlayCapability> writes = [];
        device.Invoke = (requested, _) =>
        {
            writes.Add(requested);
            return Task.CompletedTask;
        };
        var capability = new DeviceOverlayCapability("power.test", null, DeviceOverlaySection.PowerAndThermals,
            DescriptorStatus.Available, "Power limit", "", "15 W", true, CapabilityValue.Integer(15))
        {
            Role = CapabilityRole.PowerSustainedLimit, PluginSectionId = "power",
            ValueKind = CapabilityValueKind.Integer, Writable = true, Minimum = 8, Maximum = 37,
            DescriptorGeneration = 1, CycleGeneration = 1
        };
        device.State = device.State with { Capabilities = [capability] };
        var window = fixture.Overlay(1280, 720);
        window.AttachDeviceBridge(device);
        try
        {
            UiFixture.Click(window, UiFixture.Tab(window, 2));
            UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
            window.GetVisualDescendants().OfType<Slider>()
                .Single(slider => Equals(slider.Tag, "power.test")).Value = 25;
            device.State = device.State with
            {
                Capabilities = change switch
                {
                    0 => [capability, capability with { CapabilityId = "power.other" }],
                    1 => [],
                    2 => [capability with { DescriptorGeneration = 2 }],
                    3 => [capability with { CanInvoke = false }],
                    _ => [capability with { CycleGeneration = 2 }]
                }
            };
            device.Notify();
            Dispatcher.UIThread.RunJobs();
            if (change == 0)
            {
                Assert.Equal(25, Assert.Single(writes).NextValue!.IntegerValue);
            }
            else
            {
                Assert.Empty(writes);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TelemetryRefreshKeepsTheSameEditorAndGenerationReplacementRebuildsIt()
    {
        using var fixture = new UiFixture();
        using var device = new FakeDevice();
        var capability = new DeviceOverlayCapability("power.test", null, DeviceOverlaySection.PowerAndThermals,
            DescriptorStatus.Available, "Power limit", "", "15 W", true, CapabilityValue.Integer(15))
        {
            Role = CapabilityRole.PowerSustainedLimit, PluginSectionId = "power",
            ValueKind = CapabilityValueKind.Integer,
            Writable = true, Minimum = 8, Maximum = 37, DescriptorGeneration = 1, CycleGeneration = 1
        };
        device.State = device.State with { Capabilities = [capability] };
        var window = fixture.Overlay(1280, 720);
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
        var original = window.GetVisualDescendants().OfType<Slider>()
            .Single(slider => Equals(slider.Tag, "power.test"));
        device.State = device.State with
        {
            Capabilities = [capability with { CurrentValue = CapabilityValue.Integer(22) }]
        };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        var updated = window.GetVisualDescendants().OfType<Slider>().Single(slider => Equals(slider.Tag, "power.test"));
        Assert.Same(original, updated);
        Assert.Equal(22, updated.Value);
        updated.Value = 25;
        device.State = device.State with
        {
            Capabilities = [capability with { CurrentValue = CapabilityValue.Integer(20) }]
        };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(updated,
            window.GetVisualDescendants().OfType<Slider>().Single(slider => Equals(slider.Tag, "power.test")));
        Assert.Equal(25, updated.Value);
        device.State = device.State with
        {
            Capabilities = [capability with { DescriptorGeneration = 2, Maximum = 30 }]
        };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        var replacement = window.GetVisualDescendants().OfType<Slider>()
            .Single(slider => Equals(slider.Tag, "power.test"));
        Assert.NotSame(original, replacement);
        Assert.Equal(30, replacement.Maximum);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingFooterOwnsFocusAndReadbackCannotIssueAWrite()
    {
        var writes = 0;
        var row = DeviceControlRows.Toggle("toggle", "Lighting", "", false, true, _ => writes++);
        Assert.False(row.Focusable);
        Assert.False(row.IsClickEnabled);
        Assert.IsType<ToggleSwitch>(row.Footer);
        row.Refresh(new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = true }, true, "Observed");
        Assert.True(((ToggleSwitch)row.Footer!).IsChecked);
        Assert.Equal(0, writes);
    }

    [AvaloniaFact]
    public void ChoicePopupKeepsReadbackOutOfTheDraftAndCommitsOnlyItsFinalSelection()
    {
        using var fixture = new UiFixture();
        List<string> requested = [];
        var row = DeviceControlRows.Choice("choice", "Fan mode", "", Choices(), "first", true, requested.Add);
        var editor = Assert.IsType<ComboBox>(row.Footer);
        var window = new Window { Content = row, Width = 600, Height = 240 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            UiFixture.Click(window, editor);
            Assert.True(editor.IsDropDownOpen);
            UiFixture.Key(window, Key.Down);
            UiFixture.Key(window, Key.Down);
            var draft = editor.SelectedItem;
            Assert.Empty(requested);
            row.Refresh(CapabilityValue.Choice("first"), true, "Observed");
            Assert.Same(draft, editor.SelectedItem);
            UiFixture.Key(window, Key.Enter);
            Assert.False(editor.IsDropDownOpen);
            Assert.Equal(["third"], requested);
            UiFixture.Click(window, editor);
            UiFixture.Key(window, Key.Enter);
            Assert.Equal(["third"], requested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void InitiallyUnavailableEditorsCanReceiveFocusWhenReadbackEnablesThem()
    {
        using var fixture = new UiFixture();
        var toggle = DeviceControlRows.Toggle("toggle", "Lighting", "", false, false,
            _ => throw new InvalidOperationException("Readback must not write."));
        var choice = DeviceControlRows.Choice("choice", "Mode", "", Choices(), "first", false,
            _ => throw new InvalidOperationException("Readback must not write."));
        var slider = new DeviceSliderRow("slider", "Power", "", 8, 37, 1, CapabilityUnit.Watt, 15, false,
            _ => throw new InvalidOperationException("Readback must not write."));
        var window = new Window
            { Content = new StackPanel { Children = { toggle, choice, slider } }, Width = 600, Height = 500 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(toggle.Editor.IsEnabled);
            Assert.False(choice.Editor.IsEnabled);
            Assert.False(slider.FocusTarget.IsEnabled);
            toggle.Refresh(new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = true }, true, "");
            choice.Refresh(CapabilityValue.Choice("second"), true, "");
            slider.RefreshReadback(8, 37, 1, 20, true);
            Assert.True(toggle.Editor.Focus());
            Assert.True(choice.Editor.Focus());
            Assert.True(slider.FocusTarget.Focus());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(1280, 720, 1.0, 1)]
    [InlineData(3840, 2160, 1.0, 2)]
    [InlineData(3840, 2160, 3.0, 1)]
    public void DeviceGroupsUseTheScaledDetailViewport(int width, int height, double scale, int expectedColumns)
    {
        using var fixture = new UiFixture();
        using var device = new FakeDevice();
        device.State = device.State with
        {
            Capabilities =
            [
                new DeviceOverlayCapability("power.test", null, DeviceOverlaySection.PowerAndThermals,
                    DescriptorStatus.Available, "Power", "", "15 W", true, CapabilityValue.Integer(15))
                {
                    Role = CapabilityRole.PowerSustainedLimit, PluginSectionId = "power",
                    ValueKind = CapabilityValueKind.Integer,
                    Writable = true, Minimum = 8, Maximum = 37, DescriptorGeneration = 1, CycleGeneration = 1
                }
            ]
        };
        var window = fixture.Overlay(width, height, scale);
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
        var detail = UiFixture.Named<StackPanel>(window, "DeviceCapabilityList");
        var groups = Assert.Single(detail.Children.OfType<FlexPanel>());
        Assert.Equal(expectedColumns, groups.Children.Count);
        Assert.All(groups.Children, column => Assert.InRange(column.Bounds.Width, 0, detail.Bounds.Width));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackAndPinnedSectionsKeepTheirGroupAndEditorAcrossReadback(bool pinned)
    {
        using var fixture = new UiFixture();
        using var device = new FakeDevice();
        var capability = new DeviceOverlayCapability("lighting.test", null, DeviceOverlaySection.Overview,
            DescriptorStatus.Available, "Lighting", "", "On", true,
            new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = true })
        {
            ValueKind = CapabilityValueKind.Boolean, Writable = true, DescriptorGeneration = 1, CycleGeneration = 1
        };
        device.State = device.State with { Capabilities = [capability], PluginSections = [] };
        var window = fixture.Overlay(1280, 720);
        window.AttachDeviceBridge(device);
        if (pinned)
        {
            window.SetPins(["section.device.overview"]);
        }
        else
        {
            UiFixture.Click(window, UiFixture.Tab(window, 2));
            UiFixture.Click(window, UiFixture.Rail(window, "device.section.overview"));
        }

        Dispatcher.UIThread.RunJobs();
        var host = UiFixture.Named<Panel>(window, pinned ? "PinnedSectionsGrid" : "DeviceCapabilityList");
        var group = Assert.Single(host.Children.OfType<Border>());
        Assert.Contains("device-group", group.Classes);
        Assert.Equal((pinned ? "pin:" : "") + "section.device.overview", group.Tag);
        var body = Assert.IsType<StackPanel>(group.Child);
        Assert.Equal(8, body.Spacing);
        Assert.Single(body.Children.OfType<SectionPinHeader>());
        var editor = Assert.Single(group.GetVisualDescendants().OfType<ToggleSwitch>());
        Assert.True(editor.Focus());
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(group, Assert.Single(host.Children.OfType<Border>()));
        Assert.Same(editor, Assert.Single(group.GetVisualDescendants().OfType<ToggleSwitch>()));
        Assert.True(editor.IsFocused);
        window.Close();
    }

    private static CapabilityChoice[] Choices()
    {
        return
        [
            new CapabilityChoice("first", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "First" }),
            new CapabilityChoice("second", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Second" }),
            new CapabilityChoice("third", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Third" })
        ];
    }

    private sealed class ThemeProbeRow : ActionButton
    {
        internal Type ResolvedStyleKey => StyleKeyOverride;
    }
}
