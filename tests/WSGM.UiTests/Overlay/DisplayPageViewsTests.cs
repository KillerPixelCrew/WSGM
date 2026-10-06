using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

/// <summary>The brightness and display mode views on the overlay's Display page.</summary>
public sealed class DisplayPageViewsTests
{
    [AvaloniaFact]
    public async Task ReattachingBrightnessKeepsTheDisplayFoldAndItsMountedBody()
    {
        using var fixture = new UiFixture();
        using var brightness = new NativeQamBrightnessService(() => true, () => 60,
            _ => throw new InvalidOperationException("A Display layout test must not write brightness."),
            Timeout.InfiniteTimeSpan);
        await brightness.ReadAsync();
        var window = fixture.Overlay();
        var modes = Modes("test", 120);
        window.AttachBrightness(brightness, UiFixture.ReadOnlyDisplayModes(modes),
            () => modes.Path.SourceName);
        var panel = UiFixture.Named<StackPanel>(window, "PanelSystemDisplay");
        var fold = Assert.IsType<CollapsibleSection>(Assert.Single(panel.Children));
        fold.IsExpanded = true;
        var body = fold.Body;
        window.AttachBrightness(brightness, UiFixture.ReadOnlyDisplayModes(modes),
            () => modes.Path.SourceName);
        Assert.Same(fold, Assert.Single(panel.Children));
        Assert.Same(body, fold.Body);
        Assert.True(fold.IsExpanded);
        Assert.Equal(2, UiFixture.Named<StackPanel>(window, "DisplayBrightnessHost").Children.Count);
        Assert.Equal(2, panel.GetLogicalDescendants().OfType<ComboBox>().Count());
    }

    [AvaloniaFact]
    public void SelectsOnlyTheNamedDisplayAndLeavesUnknownDisplaysUnavailable()
    {
        using var fixture = new UiFixture();
        var first = Modes("first", 120);
        var second = Modes("second", 120);
        var sourceName = "second";
        List<string?> reads = [];
        List<string> writes = [];
        var view = new DisplayModeView(source =>
        {
            reads.Add(source);
            return Task.FromResult(source == "first" ? first : source == "second" ? second : null);
        }, (snapshot, mode) =>
        {
            writes.Add(snapshot.Path.SourceName);
            second = second with { Current = mode };
            return Task.FromResult(new DisplayModeResult(DisplayModeOutcome.Applied, 0, false, false));
        }, () => sourceName);
        var window = new Window { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            var refresh = view.GetLogicalDescendants().OfType<ComboBox>().Last();
            refresh.SelectedItem = 60;
            Assert.Equal(["second"], writes);
            Assert.All(reads, source => Assert.Equal("second", source));
            sourceName = "missing";
            window.Content = null;
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.All(view.GetLogicalDescendants().OfType<ComboBox>(), selector => Assert.False(selector.IsEnabled));
            Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Display modes unavailable");
            Assert.Single(writes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("missing")]
    public void AReadForAnotherDisplayCannotEnableSelectors(string? source)
    {
        using var fixture = new UiFixture();
        var view = new DisplayModeView(_ => Task.FromResult<DisplayModeSnapshot?>(Modes("first", 120)),
            (_, _) => throw new InvalidOperationException("An unmatched display must not be changed."), () => source);
        var window = new Window { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            Assert.All(view.GetLogicalDescendants().OfType<ComboBox>(), selector => Assert.False(selector.IsEnabled));
            Assert.Contains(view.Children.OfType<TextBlock>(), text => text.Text == "Display modes unavailable");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MovingTheSheetBeforeSelectionRefusesTheOldDisplayWrite()
    {
        using var fixture = new UiFixture();
        var source = "first";
        List<string?> reads = [];
        var view = new DisplayModeView(name =>
            {
                reads.Add(name);
                return Task.FromResult<DisplayModeSnapshot?>(Modes(name!, 120));
            }, (_, _) => throw new InvalidOperationException("A moved sheet must not change the old display."),
            () => source);
        var window = new Window { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            source = "second";
            view.GetLogicalDescendants().OfType<ComboBox>().Last().SelectedItem = 60;
            Assert.Equal(["first", "second"], reads);
            Assert.Contains(view.Children.OfType<TextBlock>(),
                text => text.Text?.StartsWith("second:", StringComparison.Ordinal) == true);
            Assert.Equal(120, view.GetLogicalDescendants().OfType<ComboBox>().Last().SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AttachedDisplayControlsUseBothOperationsFromTheSuppliedSources()
    {
        using var fixture = new UiFixture();
        using var brightness = new NativeQamBrightnessService(() => true, () => 60,
            _ => throw new InvalidOperationException("Mode selection must not write brightness."),
            Timeout.InfiniteTimeSpan);
        await brightness.ReadAsync();
        var snapshot = Modes("second", 120);
        List<string?> reads = [];
        List<string> writes = [];
        var sources = new OverlaySources(Brightness: brightness, DisplayModes: new DisplayModeAccess(source =>
        {
            reads.Add(source);
            return Task.FromResult<DisplayModeSnapshot?>(snapshot);
        }, (observation, mode) =>
        {
            Assert.Same(snapshot, observation);
            writes.Add(observation.Path.SourceName);
            snapshot = snapshot with { Current = mode };
            return Task.FromResult(new DisplayModeResult(DisplayModeOutcome.Applied, 0, false, false));
        }));
        var window = fixture.Overlay();
        window.AttachBrightness(sources.Brightness!, sources.DisplayModes!, () => "second");
        var toolsIndex = UiFixture.Named<TabStrip>(window, "Tabs").Tabs!
            .Select((tab, index) => (tab, index))
            .Single(item => item.tab.Tag == (int)OverlayDestination.System).index;
        UiFixture.Click(window, UiFixture.Tab(window, toolsIndex));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemDisplay));
        UiFixture.OpenSections(window, UiFixture.Named<StackPanel>(window, "PanelSystemDisplay"));
        var view = UiFixture.Named<StackPanel>(window, "DisplayBrightnessHost").Children.OfType<DisplayModeView>()
            .Single();
        var refresh = view.GetLogicalDescendants().OfType<ComboBox>().Last();
        Assert.Equal(120, refresh.SelectedItem);
        UiFixture.Click(window, refresh);
        Assert.True(refresh.IsDropDownOpen);
        UiFixture.Key(window, Key.Home);
        Assert.Empty(writes);
        UiFixture.Key(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();
        Assert.False(refresh.IsDropDownOpen);
        Assert.Equal(["second"], writes);
        Assert.Equal(new DisplayMode(1920, 1080, 60), snapshot.Current);
        Assert.NotEmpty(reads);
        Assert.All(reads, source => Assert.Equal("second", source));
    }

    [AvaloniaFact]
    public async Task PreviewDisplayOperationsCannotApplyARealMode()
    {
        using var fixture = new UiFixture();
        Assert.Null(await DisplayModeAccess.Unavailable.Read("first"));
        var snapshot = Modes("first", 120);
        var result = await DisplayModeAccess.Unavailable.Apply(snapshot, new DisplayMode(1920, 1080, 60));
        Assert.False(result.Applied);
        Assert.Equal(DisplayModeOutcome.Refused, result.Outcome);
    }

    [AvaloniaFact]
    public async Task AnApplyFromThePreviousAttachmentCannotReplaceTheNewDisplaysStatus()
    {
        using var fixture = new UiFixture();
        var snapshot = Modes("first", 120);
        var apply = new TaskCompletionSource<DisplayModeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new DisplayModeView(_ => Task.FromResult<DisplayModeSnapshot?>(snapshot), (_, _) => apply.Task,
            () => snapshot.Path.SourceName);
        var window = new Window { Content = view, Width = 500, Height = 400 };
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = view.Children.OfType<TextBlock>().Last();
        status.PropertyChanged += (_, _) =>
        {
            if (status.Text?.StartsWith("second:", StringComparison.Ordinal) == true)
            {
                shown.TrySetResult();
            }
        };
        try
        {
            window.Show();
            view.GetLogicalDescendants().OfType<ComboBox>().Last().SelectedItem = 60;
            window.Content = null;
            snapshot = Modes("second", 60);
            window.Content = view;
            apply.SetResult(new DisplayModeResult(DisplayModeOutcome.Refused, -1, false, false));
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("second: 1920 × 1080, 60 Hz", status.Text);
            Assert.All(view.GetLogicalDescendants().OfType<ComboBox>(), selector => Assert.True(selector.IsEnabled));
        }
        finally
        {
            window.Close();
        }
    }

    private static DisplayModeSnapshot Modes(string source, int refresh)
    {
        var target = new DisplayTargetIdentity(source, null, null, source, 1, 0, 1);
        return new DisplayModeSnapshot(new ActiveDisplayPath(target, source, 0, (uint)refresh, 1),
            new DisplayMode(1920, 1080, refresh),
            [new DisplayMode(1920, 1080, 60), new DisplayMode(1920, 1080, 120)]);
    }

    [AvaloniaFact]
    public async Task BrightnessFailureHasItsOwnMessageAndNeverReplacesThePercentage()
    {
        using var fixture = new UiFixture();
        var brightness = 50;
        using var service = new NativeQamBrightnessService(() => true,
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
        using NativeQamBrightnessService service = new(() => true,
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
    public void RegroupingDisplayControlsReadsModesAgainWithoutWriting()
    {
        using UiFixture fixture = new();
        DisplayTargetIdentity target = new("test", null, null, "Test display", 1, 0, 1);
        DisplayModeSnapshot snapshot = new(new ActiveDisplayPath(target, "test", 0, 120, 1),
            new DisplayMode(1920, 1080, 120),
            [new DisplayMode(1920, 1080, 60), new DisplayMode(1920, 1080, 120)]);
        var reads = 0;
        DisplayModeView view = new(_ =>
            {
                reads++;
                return Task.FromResult<DisplayModeSnapshot?>(snapshot);
            }, (_, _) => throw new InvalidOperationException("Regrouping must not apply a display mode."),
            () => snapshot.Path.SourceName);
        Window window = new() { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            window.Content = null;
            snapshot = snapshot with { Current = new DisplayMode(1920, 1080, 60) };
            window.Content = new Border { Child = view };
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, reads);
            Assert.Equal(60, view.GetLogicalDescendants().OfType<ComboBox>().Last().SelectedItem);
            Assert.All(view.GetLogicalDescendants().OfType<ComboBox>(), selector => Assert.True(selector.IsEnabled));
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
        DisplayModeView view = new(_ => Task.FromResult<DisplayModeSnapshot?>(snapshot), (_, mode) =>
        {
            writes.Add(mode);
            snapshot = snapshot with { Current = mode };
            return Task.FromResult(new DisplayModeResult(DisplayModeOutcome.Applied, 0, false, false));
        }, () => snapshot.Path.SourceName);
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
