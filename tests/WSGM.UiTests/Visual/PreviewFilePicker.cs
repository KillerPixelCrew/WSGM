using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Visual;

internal static class PreviewFilePicker
{
    internal static void Export(string directory, int width, int height)
    {
        foreach (var folder in new[] { false, true })
        {
            using var fixture = new UiFixture();
            var window = fixture.Overlay(width, height);
            var entries = new[]
            {
                new SteamFileEntry("PlayStation 2", @"C:\Emulation\bios\ps2", true),
                new SteamFileEntry("Dreamcast", @"C:\Emulation\bios\dc", true),
                new SteamFileEntry("Nintendo Switch", @"C:\Emulation\bios\switch", true)
            }.Concat(folder
                ? []
                : Enumerable.Range(0, 150).Select(index =>
                    new SteamFileEntry(index == 0 ? "scph5501.bin" : $"console-firmware-{index:000}.bin",
                        $@"C:\Emulation\bios\console-firmware-{index:000}.bin", false))).ToArray();
            var picker = new OverlayFilePicker(folder, [],
                _ => Task.FromResult(new SteamFilePlaces(
                [
                    new SteamFilePlace(@"C:\Emulation\bios", "Local disk (C:)", "drive", "325 GB free of 953 GB"),
                    new SteamFilePlace(@"D:\", "SD card (D:)", "drive", "13 GB free of 477 GB"),
                    new SteamFilePlace(@"C:\Users\Example\Desktop", "Desktop", "folder", ""),
                    new SteamFilePlace(@"C:\Users\Example\Documents", "Documents", "folder", ""),
                    new SteamFilePlace(@"C:\Users\Example\Downloads", "Downloads", "folder", "")
                ])),
                (path, _, _) => Task.FromResult(new SteamFileListing(path, @"C:\Emulation", entries, null)));
            _ = window.PickLocalPathAsync(picker, folder);
            Dispatcher.UIThread.RunJobs();
            window.FocusManager!.Focus(null);
            window.MouseMove(new Point(-20, -20));
            using var image = window.CaptureRenderedFrame() ??
                              throw new InvalidOperationException("No picker preview.");
            image.Save(Path.Combine(directory, $"picker-{(folder ? "folder" : "file")}-{width}x{height}.png"),
                new PngBitmapEncoderOptions());
            window.Close();
        }
    }
}
