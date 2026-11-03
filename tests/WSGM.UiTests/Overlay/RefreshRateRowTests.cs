using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class RefreshRateRowTests
{
    [AvaloniaFact]
    public void TheSliderMovesAlongTheListedRatesAndNamesEachOne()
    {
        using UiFixture fixture = new();
        int[] rates = [48, 60, 75, 100, 120];
        List<int> committed = [];
        var row = new DescriptorRow("refresh-rate", "Refresh rate", string.Empty, "120 Hz", true,
                DescriptorStatus.Available) with
            {
                Range = new DescriptorRange(0, rates.Length - 1, 1, "Hz", Stops: rates),
                Value = 4
            };
        var control = new DeviceCapabilityControl(row, "refresh-rate",
            _ => throw new InvalidOperationException("A slider row is not pressed"), committed.Add);
        var window = new Window
        {
            Width = 720, Height = 160, Content = new StackPanel { Margin = new Thickness(16), Children = { control } }
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var slider = window.GetVisualDescendants().OfType<Slider>().Single();
        Assert.Equal(0, slider.Minimum);
        Assert.Equal(4, slider.Maximum);
        Assert.Equal(4, slider.Value);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "120 Hz");
        VisualBaseline.Verify(window, "refresh-rate-row");

        slider.Value = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "60 Hz");

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, Assert.Single(committed));
    }

    [Theory]
    [InlineData(0, "48 Hz")]
    [InlineData(4, "120 Hz")]
    [InlineData(5, "")]
    public void AStopLabelIsTheRateItStandsForAndNeverOff(int position, string label)
    {
        Assert.Equal(label, new DescriptorRange(0, 4, 1, "Hz", Stops: [48, 60, 75, 100, 120]).Label(position));
    }
}
