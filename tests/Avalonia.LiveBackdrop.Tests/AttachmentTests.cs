using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LiveBackdrop.Tests;
using Avalonia.Media;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]

namespace Avalonia.LiveBackdrop.Tests;

public sealed class TestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

public sealed class AttachmentTests
{
    [AvaloniaFact]
    public void DuplicateAttachmentIsRejectedUntilDisposed()
    {
        var window = new Window { Background = Brushes.Transparent };
        var first = LiveBackdrop.Attach(window);
        Assert.Throws<InvalidOperationException>(() => LiveBackdrop.Attach(window));
        first.Dispose();
        first.Dispose();
        using var second = LiveBackdrop.Attach(window);
        Assert.False(second.IsActive);
    }

    [AvaloniaFact]
    public void InvalidBlurNeverReplacesTheLastValidValue()
    {
        var window = new Window();
        Assert.Throws<ArgumentOutOfRangeException>(() => LiveBackdrop.Attach(window, double.NaN));
        using var backdrop = LiveBackdrop.Attach(window);
        backdrop.BlurRadius = 12;
        foreach (var value in new[] { -1d, 61d, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => backdrop.BlurRadius = value);
            Assert.Equal(12, backdrop.BlurRadius);
        }
    }

    [AvaloniaFact]
    public void FallbackAndDisposalRestoreBackgroundWithoutNativeWindow()
    {
        var original = new SolidColorBrush(Colors.Transparent);
        var fallback = new SolidColorBrush(Colors.Navy);
        var window = new Window { Background = original };
        var backdrop = LiveBackdrop.Attach(window, fallback: fallback);
        backdrop.IsEnabled = false;
        Assert.Same(fallback, window.Background);
        Assert.False(backdrop.IsActive);
        backdrop.Dispose();
        Assert.Same(original, window.Background);
        Assert.Throws<ObjectDisposedException>(() => backdrop.Retry());
        Assert.Throws<ObjectDisposedException>(() => backdrop.BlurRadius = 8);
    }

    [AvaloniaFact]
    public void ClosingWindowDisposesAttachmentAndUnsubscribes()
    {
        var window = new Window { Background = Brushes.Transparent };
        var backdrop = LiveBackdrop.Attach(window);
        window.Show();
        window.Close();
        Assert.False(backdrop.IsActive);
        Assert.Throws<ObjectDisposedException>(() => backdrop.IsEnabled = false);
        backdrop.Dispose();
    }
}
