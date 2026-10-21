using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Settings;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Shared detached setup for the collapsed custom Display settings captures.</summary>
internal static class PreviewDisplaySettings
{
    internal static SettingsWindow Create(UiFixture fixture, int width)
    {
        DisplayTargetIdentity target = new("fixture", null, null, "Internal display", 0, 0, 1);
        var colorDepth = DriverChoice(target, "display.color.depth", "Output bit depth",
            [("v00000002", "8 bpc"), ("v00000003", "10 bpc")], "v00000003");
        var dithering = DriverChoice(target, "display.dithering.state", "Dithering",
            [("v00000000", "Auto"), ("v00000001", "Enabled"), ("v00000002", "Disabled")], "v00000001");
        var vrr = new DisplayGpuCapability
        {
            PluginId = "wsgm.gpu.nvidia", Target = target, ObservedValue = new PluginValue(true),
            Descriptor = new CapabilityDescriptor
            {
                CapabilityId = "display.gsync", InstanceId = "output-fixture", Role = CapabilityRole.GenericToggle,
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
        var model = (SettingsViewModel)window.DataContext!;
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        if (!model.CopyCurrentLayoutCommand.CanExecute(null))
        {
            throw new InvalidOperationException("The fixture display-copy command is unavailable.");
        }

        model.CopyCurrentLayoutCommand.Execute(null);
        var started = Stopwatch.GetTimestamp();
        while (!model.CopyCurrentLayoutCommand.CanExecute(null))
        {
            if (Stopwatch.GetElapsedTime(started) > AsyncConditions.TimeLimit)
            {
                throw new TimeoutException("The fixture display-copy command did not finish.");
            }

            Thread.Yield();
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static void Export(string directory)
    {
        foreach (var width in new[] { 1024, 1280 })
        {
            using var fixture = new UiFixture();
            var window = Create(fixture, width);
            window.FocusManager.Focus(null);
            foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
            {
                visual.Transitions = null;
            }

            window.MouseMove(new Point(-20, -20));
            using var frame = window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("No custom Display settings frame was available.");
            var path = Path.Combine(directory, "settings-display-custom-" + width + ".png");
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine(path);
            window.Close();
        }
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
}
