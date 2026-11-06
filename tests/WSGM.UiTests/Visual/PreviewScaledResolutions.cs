using Avalonia;
using Avalonia.Animation;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibGPUDriverInteract;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>
///     Shared Tools › Display › Custom resolutions setup for the captures and the render-only export: an editor with
///     no driver owner, given a fixed snapshot, so nothing reads or writes a real driver.
/// </summary>
internal static class PreviewScaledResolutions
{
    /// <summary>What the editor shows.</summary>
    internal enum Case
    {
        /// <summary>Intel: changes apply immediately, a custom size can be removed.</summary>
        Intel,

        /// <summary>AMD: adapter-wide lists that need the driver restarted, with sizes missing after an update.</summary>
        Amd,

        /// <summary>No supported driver drives the display.</summary>
        Unsupported
    }

    internal static OverlayWindow Create(UiFixture fixture, int width, Case shown,
        out ScaledResolutionEditor editor)
    {
        var window = fixture.Overlay(width, width == 1024 ? 640 : 800);
        editor = new ScaledResolutionEditor(null);
        editor.ApplySnapshot(State(shown));
        window.AttachScaledResolutions(editor);
        var toolsIndex = UiFixture.Named<TabStrip>(window, "Tabs").Tabs!
            .Select((tab, index) => (tab, index))
            .Single(item => item.tab.Tag == (int)OverlayDestination.System).index;
        UiFixture.Click(window, UiFixture.Tab(window, toolsIndex));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemDisplay));
        UiFixture.Click(window, window.PanelSystemDisplay.CustomResolutionsButton);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static ScaledResolutionState State(Case shown)
    {
        var display = new GpuDisplayTarget
        {
            DevicePath = "fixture-monitor", EdidIdentity = "fixture-edid", AdapterLowPart = 1,
            AdapterHighPart = 0, TargetId = 2
        };
        var native = new GpuScaledResolution(3840, 2160);
        var intel = shown == Case.Intel;
        var catalog = new GpuScaledResolutionCatalog(display, intel ? GpuVendor.Intel : GpuVendor.Amd,
            "PCI\\FIXTURE", intel ? "Intel UHD Graphics" : "AMD Radeon Graphics", intel, shown != Case.Unsupported,
            ScaledResolutionService.PercentagePreview(native).Append(new GpuScaledResolution(1760, 1100)).ToArray(),
            shown switch
            {
                Case.Intel => "Available immediately.",
                Case.Amd => "Graphics-driver restart required.",
                _ => "This display's graphics driver does not offer an editable resolution list."
            });
        IReadOnlyList<ScaledResolutionAddition> missing = shown == Case.Amd
            ?
            [
                new ScaledResolutionAddition
                {
                    DisplayPath = "fixture-monitor", AdapterIdentity = @"PCI\FIXTURE", Width = 1600, Height = 900
                }
            ]
            : [];
        return new ScaledResolutionState(
            [new ScaledResolutionDisplay("fixture-monitor", "Internal display")], "fixture-monitor", catalog,
            native, [], missing, catalog.Detail)
        {
            Available = [new GpuScaledResolution(800, 600)]
        };
    }

    internal static void Export(string directory)
    {
        foreach (var (width, shown) in new[] { (1024, Case.Intel), (1280, Case.Amd) })
        {
            using var fixture = new UiFixture();
            var window = Create(fixture, width, shown, out _);
            window.FocusManager?.Focus(null);
            foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
            {
                visual.Transitions = null;
            }

            window.MouseMove(new Point(-20, -20));
            using var frame = window.CaptureRenderedFrame()
                              ?? throw new InvalidOperationException("No custom resolutions frame was available.");
            var path = Path.Combine(directory, "overlay-custom-resolutions-" + width + ".png");
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine(path);
            window.Close();
        }
    }
}
