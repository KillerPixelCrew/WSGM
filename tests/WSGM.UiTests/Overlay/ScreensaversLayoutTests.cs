using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class ScreensaversLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    public async Task SourcesHaveVisualCoversAndFocusedSlideshowSettings(int width, int height)
    {
        using var fixture = new UiFixture();
        var images = Path.Combine(fixture.Store.Context.Root, "screensaver-images");
        Directory.CreateDirectory(images);
        WriteImage(Path.Combine(images, "one.png"));
        var config = new ScreensaverConfig
        {
            Sources =
            [
                new ScreensaverSourceConfig { Id = "local", Name = "Blue collection", Folder = images },
                new ScreensaverSourceConfig { Id = "second", Name = "Daily favourites", Folder = images },
                new ScreensaverSourceConfig { Id = "offline", Name = "Travel photos", Folder = Path.Combine(images, "missing") }
            ]
        };
        await using var service = new ScreensaverService(fixture.Store.Context, () => config, change => change(config), () => false);
        Assert.Equal(3, service.ReadState().Sources.Count);
        await service.ExecuteAsync(new SteamScreensaverContentCommand
        {
            Action = "playback", Id = "local", SelectorId = ScreensaverContent.SelectorId("Blue collection"), Active = false
        }, CancellationToken.None);
        var window = fixture.Overlay(width, height, session: new OverlayWindow.SessionState
        {
            Destination = OverlayDestination.System
        });
        UiFixture.Named<ScreensaversView>(window, "ScreensaversHost").Attach(service);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.Screensavers));
        Dispatcher.UIThread.RunJobs();
        var page = UiFixture.Named<ScreensaversView>(window, "ScreensaversHost");
        Assert.True(page.IsEffectivelyVisible);
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "source.name"));
        var folder = page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "source:local"));
        var cover = page.GetVisualDescendants().OfType<OverlayPreviewImage>().First();
        await Decoded(cover);
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"screensavers-{width}x{height}");
        Directory.CreateDirectory(directory);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "sources.png"));
        UiFixture.Click(window, folder);
        Dispatcher.UIThread.RunJobs();
        await Decoded(page.GetVisualDescendants().OfType<OverlayPreviewImage>().First());
        Assert.Contains(page.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "source.preview"));
        Assert.Contains(page.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "source.rename"));
        Assert.Contains(page.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "source.folder"));
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "options.png"));
        var detailCover = page.GetVisualDescendants().OfType<OverlayPreviewImage>().First();
        var rendered = Assert.IsType<Image>(detailCover.Child);
        window.Height = height < 740 ? 800 : 640;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(detailCover, page.GetVisualDescendants().OfType<OverlayPreviewImage>().First());
        Assert.Same(rendered, detailCover.Child);
        Assert.Equal(height < 740 ? 190 : 130, detailCover.Height);
        UiFixture.Named<ScreensaversView>(window, "ScreensaversHost").Attach(null);
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    public async Task EmptyLibraryHasOneFolderFirstEntryAndStockSelection(int width, int height)
    {
        using var fixture = new UiFixture();
        var config = new ScreensaverConfig();
        await using var service = new ScreensaverService(fixture.Store.Context, () => config, change => change(config), () => false);
        var window = fixture.Overlay(width, height, session: new OverlayWindow.SessionState { Destination = OverlayDestination.System });
        UiFixture.Named<ScreensaversView>(window, "ScreensaversHost").Attach(service);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.Screensavers));
        Dispatcher.UIThread.RunJobs();
        var page = UiFixture.Named<ScreensaversView>(window, "ScreensaversHost");
        Assert.Single(page.GetVisualDescendants().OfType<Button>(), button => Equals(button.Tag, "source.images"));
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "source.name"));
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"screensavers-{width}x{height}");
        Directory.CreateDirectory(directory);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "empty.png"));
        UiFixture.Named<ScreensaversView>(window, "ScreensaversHost").Attach(null);
    }

    private static async Task Decoded(OverlayPreviewImage preview)
    {
        if (preview.Child is Image)
        {
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        preview.PropertyChanged += Changed;
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (preview.Child is Image)
            {
                return;
            }
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            preview.PropertyChanged -= Changed;
        }
        return;
        void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Decorator.ChildProperty && preview.Child is Image)
            {
                completion.TrySetResult();
            }
        }
    }

    private static void WriteImage(string path)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(480, 300), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            for (var y = 0; y < buffer.Size.Height; y++)
            {
                for (var x = 0; x < buffer.Size.Width; x++)
                {
                    var offset = y * buffer.RowBytes + x * 4;
                    pixels[offset] = (byte)(160 + x % 90);
                    pixels[offset + 1] = (byte)(40 + y / 3);
                    pixels[offset + 2] = (byte)(20 + x / 6);
                    pixels[offset + 3] = 255;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
