using Avalonia.Controls;
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
            if (stage == "Downloading")
            {
                var view = window.GetVisualDescendants().OfType<GpuDriverUpdatesView>().Single();
                var parent = Assert.IsAssignableFrom<Panel>(view.Parent);
                var index = parent.Children.IndexOf(view);
                parent.Children.Remove(view);
                parent.Children.Insert(index, view);
                Dispatcher.UIThread.RunJobs();
                var content = view.Content;
                var progress = view.GetVisualDescendants().OfType<OverlayProgress>().Single();
                source.AdvanceProgress();
                Dispatcher.UIThread.RunJobs();
                Assert.Same(content, view.Content);
                Assert.Same(progress, view.GetVisualDescendants().OfType<OverlayProgress>().Single());
                Assert.Equal(0.78, progress.State!.Fraction);
                using var tick = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No progress frame.");
                tick.Save(Path.Combine(directory, $"driver-updates-Downloading-tick-{width}.png"), new PngBitmapEncoderOptions());
            }
            window.Close();
        }
    }

    private sealed class PreviewSource(string stage) : IGpuDriverUpdateSource
    {
        private int _progress = 42;
        public event Action? Changed
        {
            add
            {
            }
            remove
            {
            }
        }
        public event Action? ProgressChanged;
        internal void AdvanceProgress()
        {
            _progress = 78;
            ProgressChanged?.Invoke();
        }
        public string? DiscoveryDetail => null;
        public string? TakeAnnouncement() => null;
        public bool Automatic => false;
        public GpuDriverInstallHistory? History => null;
        public IReadOnlyList<GpuDriverUpdateState> Snapshot()
        {
            var adapter = new GpuInstalledDriver("fixture", "VEN_8086&DEV_7D55", GpuVendor.Intel,
                "Intel Arc Graphics with a long adapter name", "32.0.101.7092", new DateTime(2026, 9, 25));
            // Immutable fixture metadata only. No catalog, downloader, installer or hardware owner is created.
            var package = stage is not ("Available" or "confirm") ? null : new GpuDriverPackage(adapter,
                "32.0.101.9150", new DateTime(2026, 10, 2), new Uri("https://downloadmirror.intel.com/fixture.exe"),
                new Uri("https://www.intel.com/driver"), "Release notes for this fictional preview package.", 1024L, null);
            var phase = stage is "empty" ? GpuDriverUpdateStage.NotChecked : stage is "confirm" ? GpuDriverUpdateStage.Available
                : Enum.Parse<GpuDriverUpdateStage>(stage.Replace(" ", ""), ignoreCase: true);
            return stage == "empty" ? [] : [new GpuDriverUpdateState(adapter, phase,
                stage == "Failed" ? "The official server is unavailable. Check again when the connection is restored."
                    : "Driver update status from the shared backend.", package, Progress: _progress)];
        }
        public string? InstallRefusal(GpuInstalledDriver adapter) => null;
        public void SetAutomatic(bool enabled)
        {
        }
        public bool Check() => true;
        public bool Install(string instanceId, string confirmedPackageIdentity) => false;
        public void Cancel()
        {
        }
    }
}
