using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reflection;
using LibGPUDriverInteract;
using WSGM.Core;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

/// <summary>Renders the production driver-update view with detached state and no hardware or network owner.</summary>
public static class PreviewDriverUpdates
{
    /// <summary>Exports empty, checking, downloading, installing, up-to-date and recoverable failure states.</summary>
    public static void Export(string directory, int width, int height)
    {
        Directory.CreateDirectory(directory);
        foreach (var stage in new[] { "empty", "Checking", "Available", "confirm", "Downloading", "Installing", "Up to date", "Failed" })
        {
            using var fixture = new UiFixture();
            var source = new PreviewSource(stage);
            var window = fixture.Overlay(width, height);
            using var device = new SimulatedDeviceOverlaySource();
            using var graphics = new SimulatedGraphicsOverlaySource();
            window.AttachSources(new OverlaySources(Device: device, Graphics: graphics, DriverUpdates: source));
            UiFixture.Click(window, UiFixture.Tab(window, 2));
            UiFixture.Click(window, UiFixture.Rail(window, DevicePages.GpuKey));
            if (stage == "confirm")
            {
                UiFixture.Click(window, window.GetVisualDescendants().OfType<ActionButton>()
                    .Single(button => Equals(button.Tag, "gpu-updates.install:fixture")));
            }
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No driver-update frame.");
            frame.Save(Path.Combine(directory, $"driver-updates-{stage.Replace(' ', '-')}-{width}.png"), new PngBitmapEncoderOptions());
            window.Close();
        }
    }

    private sealed class PreviewSource(string stage) : IGpuDriverUpdateSource
    {
        public event Action? Changed { add { } remove { } }
        public bool Automatic => false;
        public GpuDriverInstallHistory? History => null;
        public IReadOnlyList<GpuDriverUpdateState> Snapshot()
        {
            var adapter = new GpuInstalledDriver("fixture", "VEN_8086&DEV_7D55", GpuVendor.Intel,
                "Intel Arc Graphics with a long adapter name", "32.0.101.7092", new DateTime(2026, 9, 25));
            // Immutable fixture metadata only. No catalog, downloader, installer or hardware owner is created.
            var package = stage is not ("Available" or "confirm") ? null : (GpuDriverPackage)Activator.CreateInstance(typeof(GpuDriverPackage),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: [adapter, "32.0.101.9150", new DateTime(2026, 10, 2),
                    new Uri("https://downloadmirror.intel.com/fixture.exe"), new Uri("https://www.intel.com/driver"),
                    "Release notes for this fictional preview package.", 1024L, null, "-s"], culture: null)!;
            return stage == "empty" ? [] : [new GpuDriverUpdateState(adapter, stage == "confirm" ? "Available" : stage,
                stage == "Failed" ? "The official server is unavailable. Check again when the connection is restored."
                    : "Driver update status from the shared backend.", package, Progress: 42)];
        }
        public string? InstallRefusal(GpuInstalledDriver? adapter = null) => null;
        public void SetAutomatic(bool enabled) { }
        public bool Check() => true;
        public bool Install(string instanceId) => false;
        public void Cancel() { }
    }
}
