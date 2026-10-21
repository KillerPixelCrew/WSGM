using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Shared detached Display-editor setup for the existing capture and render-only export.</summary>
internal static class PreviewScaledResolutions
{
    internal static SettingsWindow Create(UiFixture fixture, int width, bool intel)
    {
        var window = fixture.Settings(width);
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        var model = (SettingsViewModel)window.DataContext!;
        var display = new GpuDisplayTarget
        {
            DevicePath = "fixture-monitor", EdidIdentity = "fixture-edid", AdapterLowPart = 1,
            AdapterHighPart = 0, TargetId = 2
        };
        var addition = new ScaledResolutionAddition
        {
            DisplayPath = "fixture-monitor", EdidIdentity = "fixture-edid", AdapterIdentity = "PCI\\FIXTURE",
            Width = 1760, Height = 1100
        };
        var catalog = new GpuScaledResolutionCatalog(display, intel ? GpuVendor.Intel : GpuVendor.Amd,
            "PCI\\FIXTURE", intel ? "Intel UHD Graphics" : "AMD Radeon Graphics", intel, true,
            [new GpuScaledResolution(1760, 1100)], intel ? "Available immediately." : "Graphics-driver restart required.");
        model.ScaledResolutions.ApplySnapshot(new ScaledResolutionState(
            [new ScaledResolutionDisplay("fixture-monitor", "Internal display")], "fixture-monitor", catalog,
            new GpuScaledResolution(3840, 2160), [addition], [], catalog.Detail));
        var expander = window.GetVisualDescendants().OfType<Expander>()
            .Single(item => item.Header as string == "GPU-scaled resolutions");
        expander.IsExpanded = true;
        model.ScaledResolutions.PreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static void Export(string directory)
    {
        foreach (var (width, intel) in new[] { (1024, true), (1280, false) })
        {
            using var fixture = new UiFixture();
            var window = Create(fixture, width, intel);
            window.FocusManager.Focus(null);
            foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
            {
                visual.Transitions = null;
            }

            window.MouseMove(new Point(-20, -20));
            using var frame = window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("No scaled-resolution editor frame was available.");
            var path = Path.Combine(directory, "settings-scaled-resolutions-" + width + ".png");
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine(path);
            window.Close();
        }
    }
}
