using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

/// <summary>The brightness and display mode views on the overlay's Display page.</summary>
public sealed class DisplayPageViewsTests
{
    [AvaloniaFact]
    public async Task BrightnessFailureHasItsOwnMessageAndNeverReplacesThePercentage()
    {
        using var fixture = new UiFixture();
        var brightness = 50;
        using var service = new NativeQamBrightnessService(() => true, () => { },
            () => brightness, _ => false, Timeout.InfiniteTimeSpan);
        await service.ReadAsync();
        var view = new DisplayBrightnessView(service);
        var window = new Window { Content = view, Width = 600, Height = 240 };
        try
        {
            window.Show();
            var texts = view.GetLogicalDescendants().OfType<TextBlock>().ToArray();
            var percent = texts.Single(text => text.Text == "50%");
            var status = texts.Single(text => !text.IsVisible);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            status.PropertyChanged += (_, _) =>
            {
                if (status.IsVisible)
                {
                    shown.TrySetResult();
                }
            };
            view.GetLogicalDescendants().OfType<Slider>().Single().Value = 31;
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("50%", percent.Text);
            Assert.Contains("refused", status.Text);
            brightness = 65;
            await service.ReadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("65%", percent.Text);
            Assert.True(status.IsVisible);
            Assert.Contains("refused", status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ReadbackRetainsSliderAndDisablesUnsupportedDisplayWithoutWriting()
    {
        using UiFixture fixture = new();
        int? brightness = 43;
        var writes = 0;
        using NativeQamBrightnessService service = new(() => true, () => { },
            () => brightness, _ =>
            {
                writes++;
                return true;
            }, Timeout.InfiniteTimeSpan);
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
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResolutionAndRefreshSelectionsApplyOnceWithSupportedRates()
    {
        using UiFixture fixture = new();
        DisplayTargetIdentity target = new("test", null, null, "Test display", 1, 0, 1);
        DisplayModeSnapshot snapshot = new(new ActiveDisplayPath(target, "test", 0, 120, 1),
            new DisplayMode(1920, 1080, 120),
            [new DisplayMode(1920, 1080, 60), new DisplayMode(1920, 1080, 120), new DisplayMode(1280, 720, 60)]);
        List<DisplayMode> writes = [];
        DisplayModeView view = new(() => Task.FromResult<DisplayModeSnapshot?>(snapshot), (_, mode) =>
        {
            writes.Add(mode);
            snapshot = snapshot with { Current = mode };
            return Task.FromResult(new DisplayProfileResult(true, 0, false, false, "Applied"));
        });
        Window window = new() { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            var resolution = view.GetLogicalDescendants().OfType<ComboBox>().First();
            var refresh = view.GetLogicalDescendants().OfType<ComboBox>().Last();
            Assert.Equal(120, refresh.SelectedItem);
            Assert.Empty(writes);
            refresh.SelectedItem = 60;
            Assert.Equal(new DisplayMode(1920, 1080, 60), Assert.Single(writes));
            resolution.IsDropDownOpen = true;
            resolution.SelectedItem = "1280 × 720";
            Assert.Equal(60, refresh.SelectedItem);
            Assert.Single(refresh.Items);
            Assert.Single(writes);
            resolution.IsDropDownOpen = false;
            Assert.Equal(new DisplayMode(1280, 720, 60), snapshot.Current);
            Assert.Equal(2, writes.Count);
            Assert.DoesNotContain(view.GetLogicalDescendants(), control => control is Button);
        }
        finally
        {
            window.Close();
        }
    }
}
