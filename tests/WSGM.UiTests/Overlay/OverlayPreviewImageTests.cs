using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Overlay;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayPreviewImageTests
{
    [Fact]
    public async Task CacheRetainsMoreThan64SmallPreviewsWithinItsByteBudget()
    {
        using var directory = new TemporaryDirectory();
        var firstPath = directory.GetPath("0.png");
        byte[]? first = null;
        for (var index = 0; index < 65; index++)
        {
            var path = directory.GetPath($"{index}.png");
            await File.WriteAllBytesAsync(path, [(byte)index], TestContext.Current.CancellationToken);
            var bytes = await OverlayPreviewImage.ReadAsync(path, CancellationToken.None);
            first ??= bytes;
        }

        File.Delete(firstPath);
        Assert.Same(first, await OverlayPreviewImage.ReadAsync(firstPath, CancellationToken.None));
    }

    [Fact]
    public async Task CachedPreviewStillHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("preview.png");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        await OverlayPreviewImage.ReadAsync(path, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OverlayPreviewImage.ReadAsync(path, cancellation.Token));
    }

    [Fact]
    public async Task OversizedLocalPreviewIsRefusedBeforeReadingItsBody()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("large.png");
        using (var file = File.Create(path))
        {
            file.SetLength(16 * 1024 * 1024 + 1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OverlayPreviewImage.ReadAsync(path, CancellationToken.None));
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LoadsOnlyWhenItsPageOrScrollViewportShowsIt(bool scrolled, bool hiddenParent)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("preview.png");
        var image = OverlayToolFixtures.Image;
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(image[(image.IndexOf(',') + 1)..]));
        using var fixture = new UiFixture();
        var preview = new OverlayPreviewImage(path, 100) { IsVisible = scrolled || hiddenParent };
        var body = new StackPanel { IsVisible = !hiddenParent };
        if (scrolled)
        {
            body.Children.Add(new Border { Height = 500 });
        }

        body.Children.Add(preview);
        var scroll = new ScrollViewer { Content = body };
        var window = new Window { Content = scroll, Width = 600, Height = 300 };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        preview.PropertyChanged += (_, change) =>
        {
            if (change.Property == Decorator.ChildProperty && preview.Child is Image)
            {
                loaded.TrySetResult();
            }
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<TextBlock>(preview.Child);
            if (scrolled)
            {
                scroll.Offset = new Vector(0, 500);
            }
            else if (hiddenParent)
            {
                body.IsVisible = true;
            }
            else
            {
                preview.IsVisible = true;
            }

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rendered = Assert.IsType<Image>(preview.Child);
            Assert.NotNull(rendered.Source);
            window.Content = null;
            Assert.Null(rendered.Source);
        }
        finally
        {
            window.Close();
        }
    }
}
