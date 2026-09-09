using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WindowsDeviceControl;
using WSGM.Overlay;

namespace WSGM.UiTests;

public sealed class DisplayModeTests
{
    [AvaloniaFact]
    public void ResolutionDraftOffersOnlyItsSupportedRefreshRates()
    {
        using UiFixture fixture = new();
        DisplayTargetIdentity target = new("test", null, null, "Test display", 1, 0, 1);
        DisplayModeSnapshot snapshot = new(new(target, "test", 0, 120, 1), new(1920, 1080, 120),
            [new(1920, 1080, 60), new(1920, 1080, 120), new(1280, 720, 60)]);
        DisplayModeView view = new(() => Task.FromResult<DisplayModeSnapshot?>(snapshot));
        Window window = new() { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            var resolution = Assert.IsType<ComboBox>(view.Children[2]);
            var refresh = Assert.IsType<ComboBox>(view.Children[3]);
            Assert.Equal(120, refresh.SelectedItem);
            resolution.SelectedItem = "1280 × 720";
            Assert.Equal(60, refresh.SelectedItem);
            Assert.Single(refresh.Items);
            Assert.Equal(new(1920, 1080, 120), snapshot.Current);
        }
        finally { window.Close(); }
    }
}
