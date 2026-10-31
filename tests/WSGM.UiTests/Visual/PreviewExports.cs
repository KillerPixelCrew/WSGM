using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Overlay;

namespace WSGM.UiTests.Visual;

/// <summary>Render-only production view exports using simulated sources, without running a test suite.</summary>
public static class PreviewExports
{
    /// <summary>Renders destinations and transient surfaces for visual inspection at an explicit viewport.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="width">The viewport width in DIPs.</param>
    /// <param name="height">The viewport height in DIPs.</param>
    /// <param name="scale">The render scale.</param>
    /// <param name="publicationPath">
    ///     Another device's publication. It exports only the device pages, skips sections that device lacks
    ///     and adds one capture per expanded group.
    /// </param>
    /// <param name="deviceName">The device name shown, the Claw fixture's by default.</param>
    public static void Export(string directory, int width, int height, double scale = 1,
        string? publicationPath = null, string? deviceName = null)
    {
        Directory.CreateDirectory(directory);
        var devicePagesOnly = publicationPath is not null;
        foreach (var page in new[]
                 {
                     "quick-access", "steam", "steam-launch", "tools",
                     "tools-storage", "tools-display", "tools-controller", "power", "power-idle",
                     "power-actions", "power-session", "device", "device-power", "device-rgb",
                     "device-controller", "device-info", "device-gpu", "power-menu", "keyboard"
                 })
        {
            if (devicePagesOnly && !page.StartsWith("device", StringComparison.Ordinal))
            {
                continue;
            }

            using var device = new FakeDevice();
            using var host = new SimulatedDeviceOverlaySource();
            using var graphics = FixtureGraphicsSource.Load();
            var profiles = ApplicationProfilesViewTests.Profiles();
            var performance = ApplicationProfilesViewTests.Service(profiles);
            using var performanceBridge = new PerformanceOverlayBridge(performance, profiles);
            performance.RefreshAsync().GetAwaiter().GetResult();
            using var fixture = new UiFixture();
            var publication = JsonSerializer.Deserialize<Publication>(File.ReadAllText(
                publicationPath ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "claw-ui-publication.json")))!;
            var sections = DeviceSectionLayout.IncludePredefined(publication.Descriptors.Sections);
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
                Status = deviceName ?? "MSI Claw 8 AI+", Detail = "Device integration active",
                Capabilities =
                [
                    .. capabilities.Select(view => CapabilityRowProjection.ToOverlayCapability(view, ids)),
                    graphics.VariableRefreshRow(ids)
                ],
                PluginSections = CapabilityRowProjection.ProjectSections(sections), Recovery = null
            };
            var window = fixture.Overlay(width, height, scale);
            window.AttachDeviceBridge(device);
            window.AttachGraphicsSource(graphics);
            window.AttachPerformanceSource(performanceBridge);
            var destination = page switch
            {
                _ when page.StartsWith("steam", StringComparison.Ordinal) => 1,
                _ when page.StartsWith("device", StringComparison.Ordinal) => 2,
                _ when page.StartsWith("tools", StringComparison.Ordinal) => 3,
                _ when page.StartsWith("power", StringComparison.Ordinal) && page != "power-menu" => 4,
                _ => 0
            };
            UiFixture.Tab(window, destination).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var section = page switch
            {
                "steam-launch" => "SteamLaunchFixes",
                "tools-storage" => "SystemStorage",
                "tools-display" => "SystemDisplay",
                "tools-controller" => "SystemController",
                "power-idle" => "PowerTimeouts",
                "power-actions" => "PowerActions",
                "power-session" => "PowerSession",
                "device-power" => "power",
                "device-rgb" => "rgb",
                "device-controller" => "controller",
                "device-info" => "info",
                "device-gpu" => "device.gpu",
                _ => null
            };
            if (section is not null)
            {
                var rail = UiFixture.Named<StackPanel>(window, "SectionRail");
                var button = rail.Children.OfType<Button>().FirstOrDefault(item =>
                    item.Tag is string key && key.Contains(section, StringComparison.OrdinalIgnoreCase));
                if (button is null)
                {
                    if (devicePagesOnly)
                    {
                        window.Close();
                        performance.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        continue;
                    }

                    throw new InvalidOperationException($"No {page} section. Available: "
                                                        + string.Join(", ", rail.Children.OfType<Button>()
                                                            .Select(button => button.Tag)));
                }
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            var folds = window.GetVisualDescendants().OfType<CollapsibleSection>()
                .Where(section => section.IsEffectivelyVisible).ToArray();
            if (folds.Length > 0)
            {
                foreach (var fold in folds)
                {
                    fold.IsExpanded = true;
                }

                Dispatcher.UIThread.RunJobs();
                window.FocusManager.Focus(null);
                using var expanded = window.CaptureRenderedFrame()
                                     ?? throw new InvalidOperationException("No expanded frame was available.");
                expanded.Save(Path.Combine(directory, $"{page}-expanded-{width}x{height}-{scale:0.##}.png"),
                    new PngBitmapEncoderOptions());
                if (devicePagesOnly)
                {
                    for (var index = 0; index < folds.Length; index++)
                    {
                        foreach (var fold in folds)
                        {
                            fold.IsExpanded = false;
                        }

                        var selected = folds[index];
                        selected.IsExpanded = true;
                        Dispatcher.UIThread.RunJobs();
                        var scroll = selected.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                        if (scroll?.Content is Avalonia.Visual content && selected.TranslatePoint(default, content) is { } point)
                        {
                            scroll.Offset = new Vector(x: 0, point.Y);
                        }

                        Dispatcher.UIThread.RunJobs();
                        using var group = window.CaptureRenderedFrame()
                                          ?? throw new InvalidOperationException("No rendered group was available.");
                        group.Save(Path.Combine(directory, $"{page}-group-{index}-{width}x{height}-{scale:0.##}.png"),
                            new PngBitmapEncoderOptions());
                    }
                }
            }

            window.Close();
            performance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (devicePagesOnly)
        {
            return;
        }

        PreviewAudioPanel.Export(directory);
        PreviewTools.Export(directory, width, height);
        PreviewFilePicker.Export(directory, width, height);
        PreviewScaledResolutions.Export(directory);
        PreviewDisplaySettings.Export(directory);
    }

    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);
}
