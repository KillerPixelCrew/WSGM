using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

/// <summary>The brightness and display mode views on the overlay's Display page.</summary>
public sealed class DisplayPageViewsTests
{
    [AvaloniaFact]
    public async Task ReadbackRetainsSliderAndDisablesUnsupportedDisplayWithoutWriting()
    {
        using UiFixture fixture = new();
        int? brightness = 43;
        var writes = 0;
        using NativeQamBrightnessService service = new(() => true, () => { },
            () => brightness, _ => { writes++; return true; }, Timeout.InfiniteTimeSpan);
        await service.ReadAsync();
        DisplayBrightnessView view = new(service);
        var slider = view.GetLogicalDescendants().OfType<Slider>().Single();
        Window window = new() { Content = view, Width = 500, Height = 200 };
        try
        {
            window.Show();
            Assert.Equal(43, slider.Value);
            brightness = 61;
            await service.ReadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(61, slider.Value);
            Assert.Same(slider, view.GetLogicalDescendants().OfType<Slider>().Single());
            brightness = null;
            await service.ReadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(slider.IsEnabled);
            Assert.Equal(0, writes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ResolutionDraftOffersOnlyItsSupportedRefreshRates()
    {
        using UiFixture fixture = new();
        DisplayTargetIdentity target = new("test", null, null, "Test display", 1, 0, 1);
        DisplayModeSnapshot snapshot = new(new ActiveDisplayPath(target, "test", 0, 120, 1), new DisplayMode(1920, 1080, 120),
            [new DisplayMode(1920, 1080, 60), new DisplayMode(1920, 1080, 120), new DisplayMode(1280, 720, 60)]);
        DisplayModeView view = new(() => Task.FromResult<DisplayModeSnapshot?>(snapshot));
        Window window = new() { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            var resolution = view.GetLogicalDescendants().OfType<ComboBox>().First();
            var refresh = view.GetLogicalDescendants().OfType<ComboBox>().Last();
            Assert.Equal(120, refresh.SelectedItem);
            resolution.SelectedItem = "1280 × 720";
            Assert.Equal(60, refresh.SelectedItem);
            Assert.Single(refresh.Items);
            Assert.Equal(new DisplayMode(1920, 1080, 120), snapshot.Current);
        }
        finally { window.Close(); }
    }
}
