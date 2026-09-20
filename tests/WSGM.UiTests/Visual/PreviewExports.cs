using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Render-only production view exports using simulated sources, without running a test suite.</summary>
public static class PreviewExports
{
    /// <summary>Renders destinations and transient surfaces for visual inspection at an explicit viewport.</summary>
    public static void Export(string directory, int width, int height, double scale = 1)
    {
        Directory.CreateDirectory(directory);
        foreach (var page in new[]
                     { "quick-access", "steam", "tools", "power", "device", "device-power", "power-menu", "keyboard" })
        {
            using var device = new FakeDevice();
            using var host = new SimulatedDeviceOverlaySource();
            using var fixture = new UiFixture();
            var publication = JsonSerializer.Deserialize<Publication>(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "claw-ui-publication.json")))!;
            var sections = DeviceSections.IncludePredefined(publication.Descriptors.Sections);
            var ids = sections.Select(section => section.SectionId).ToHashSet();
            var capabilities = publication.Descriptors.Descriptors.Select(descriptor =>
                new DeviceCapabilityView(descriptor, new CapabilityProjection
                {
                    State = publication.States.Last(state => state.CapabilityId == descriptor.CapabilityId
                                                             && state.InstanceId == descriptor.InstanceId)
                }, null));
            device.SampleSource = host;
            device.State = host.Snapshot() with
            {
                Status = "MSI Claw 8 AI+", Detail = "Device integration active",
                Capabilities = [.. capabilities.Select(view => DeviceOverlayBridge.ToOverlayCapability(view, ids))],
                PluginSections = DeviceOverlayBridge.ProjectSections(sections), Recovery = null
            };
            var window = fixture.Overlay(width, height, scale);
            window.AttachDeviceBridge(device);
            var destination = page switch
            {
                "steam" => 1, "device" or "device-power" => 2, "tools" => 3, "power" => 4, _ => 0
            };
            UiFixture.Tab(window, destination).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (page == "device-power")
            {
                var rail = UiFixture.Named<StackPanel>(window, "SectionRail");
                var button = rail.Children.OfType<Button>().FirstOrDefault(item =>
                    item.Tag is string key && key.Contains("power", StringComparison.OrdinalIgnoreCase));
                button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            if (page == "power-menu")
            {
                window.ShowPowerMenu();
            }

            if (page == "keyboard")
            {
                window.ShowKeyboardSurface(new KeyboardPanel("Profile name", "Balanced", 100));
            }

            Dispatcher.UIThread.RunJobs();
            window.FocusManager.Focus(null);
            foreach (var control in window.GetVisualDescendants().OfType<Animatable>())
            {
                control.Transitions = null;
            }

            window.MouseMove(new Point(-20, -20));
            using var frame = window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("No rendered frame was available.");
            var path = Path.Combine(directory, $"{page}-{width}x{height}-{scale:0.##}.png");
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine(path);
        }
    }

    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);
}
