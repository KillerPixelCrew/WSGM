using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class DisplayBrightnessTests
{
    [AvaloniaFact]
    public async Task ReadbackRetainsSliderAndDisablesUnsupportedDisplayWithoutWriting()
    {
        using UiFixture fixture = new();
        int? brightness = 43;
        int writes = 0;
        using NativeQamBrightnessService service = new(() => true, () => { },
            () => brightness, _ => { writes++; return true; }, Timeout.InfiniteTimeSpan);
        await service.ReadAsync();
        DisplayBrightnessView view = new(service);
        var slider = Assert.IsType<Slider>(view.Children[2]);
        Window window = new() { Content = view, Width = 500, Height = 200 };
        try
        {
            window.Show();
            Assert.Equal(43, slider.Value);
            brightness = 61;
            await service.ReadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(61, slider.Value);
            Assert.Same(slider, view.Children[2]);
            brightness = null;
            await service.ReadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(slider.IsEnabled);
            Assert.Equal(0, writes);
        }
        finally { window.Close(); }
    }
}
