using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

/// <summary>Settings window layout at handheld sizes.</summary>
public sealed class SettingsLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1024)]
    [InlineData(1280)]
    public async Task DisplaySettingsShowsReadableModesAndLabeledScaling(int width)
    {
        DisplayTargetIdentity target = new("fixture", null, null, "Internal display", 0, 0, 1);
        using UiFixture fixture = new();
        var colorDepth = DriverChoice(target, "display.color.depth", "Output bit depth",
            [("v00000002", "8 bpc"), ("v00000003", "10 bpc")], "v00000003");
        var dithering = DriverChoice(target, "display.dithering.state", "Dithering",
            [("v00000000", "Auto"), ("v00000001", "Enabled"), ("v00000002", "Disabled")], "v00000001");
        var vrr = new DisplayGpuCapability
        {
            PluginId = "wsgm.gpu.nvidia", ObservedValue = new PluginValue(true),
            Descriptor = new CapabilityDescriptor
            {
                CapabilityId = "graphics.gsync", InstanceId = "driver", Role = CapabilityRole.GenericToggle,
                ValueKind = CapabilityValueKind.Boolean,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "G-SYNC enabled" },
                SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
            }
        };
        fixture.Saved.GameModeLaunch.KnownDisplays =
        [
            new KnownDisplay
            {
                Target = target, Modes = [new DisplayMode(1920, 1200, 120)], HdrSupported = true,
                MaximumDpiPercent = 225, GpuCapabilities = [colorDepth, dithering]
            }
        ];
        fixture.Saved.GameModeLaunch.KnownGpuCapabilities = [colorDepth, dithering, vrr];
        fixture.Saved.GameModeLaunch.GameDisplayGpu =
        [
            Preference(colorDepth, new PluginValue(Text: "v00000003")),
            Preference(dithering, new PluginValue(Text: "v00000001")),
            Preference(vrr, new PluginValue(true))
        ];
        fixture.Displays = new DisplayArrangement([
                new DisplayTargetObservation(target, true, true,
                    new DisplayLayoutOutput(target, 0, 0, 1920, 1200, DisplayRefresh.FromHertz(120), DpiPercent: 150,
                        Hdr: true))
            ],
            "fixture", DateTimeOffset.UnixEpoch);
        fixture.DisplayFacts[target.DevicePath] =
            new DisplayCatalogFacts([new DisplayMode(1920, 1200, 120), new DisplayMode(1280, 800, 60)], true, 225);
        var window = fixture.Settings(width);
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        await GameModeDisplayPageTests.ExecuteDisplayCommandAsync(model.CopyCurrentLayoutCommand);
        Dispatcher.UIThread.RunJobs();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Where(control => control.IsEffectivelyVisible)
            .ToArray();
        Assert.DoesNotContain(text, control => control.Text?.Contains("DisplayMode {") == true);
        Assert.Contains(text, control => control.Text == "Scale (%)");
        Assert.Contains(text, control => control.Text == "Output bit depth");
        Assert.Contains(text, control => control.Text == "Dithering");
        Assert.Contains(text, control => control.Text == "G-SYNC enabled");
        Assert.Equal(2, model.GameLayout.Selected?.GpuControls.Count);
        Assert.Single(model.GameLayout.GlobalGpuControls);
        VisualBaseline.Verify(window, "settings-display-custom-" + width);
    }

    private static DisplayGpuCapability DriverChoice(DisplayTargetIdentity target, string id, string label,
        (string Value, string Label)[] choices, string observed)
    {
        return new DisplayGpuCapability
        {
            Target = target, PluginId = "wsgm.gpu.nvidia", ObservedValue = new PluginValue(Text: observed),
            Descriptor = new CapabilityDescriptor
            {
                CapabilityId = id, InstanceId = "internal-output", Role = CapabilityRole.GenericChoice,
                ValueKind = CapabilityValueKind.Choice,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
                SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent,
                ProfileScope = CapabilityProfileScope.GlobalOnly,
                Choices =
                [
                    .. choices.Select(choice => new CapabilityChoice(choice.Value,
                        new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = choice.Label }))
                ]
            }
        };
    }

    private static DisplayGpuPreference Preference(DisplayGpuCapability capability, PluginValue value)
    {
        return new DisplayGpuPreference
        {
            Target = capability.Target, PluginId = capability.PluginId,
            CapabilityId = capability.Descriptor!.CapabilityId, InstanceId = capability.Descriptor.InstanceId,
            Value = value
        };
    }

    [AvaloniaFact]
    public void SettingsTabsScrollToTheirFullLabels()
    {
        using UiFixture fixture = new();
        Window window = fixture.Settings(1024, 700);
        var tabs = UiFixture.Named<TabStrip>(window, "Tabs");
        for (var i = 0; i < tabs.Tabs!.Count; i++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, i));
            Dispatcher.UIThread.RunJobs();
            var button = UiFixture.Tab(window, i);
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.Equal(tabs.Tabs[i].Label, label.Text);
            Assert.True(button.Bounds.Width >= label.Bounds.Width + 12);
            var origin = button.TranslatePoint(default, tabs)!.Value;
            Assert.InRange(origin.X, 0, tabs.Bounds.Width - button.Bounds.Width);
        }
    }
}
