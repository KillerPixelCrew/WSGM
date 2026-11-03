using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

public sealed class SettingsNavigationTests
{
    [AvaloniaFact]
    public void TheProfileEditorNestsUnderDeviceAndBackReturnsToItsRow()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Named<Button>(UiFixture.Named<Control>(window, "PageDevice"), "OpenProfiles")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(UiFixture.Named<Control>(window, "PageDeviceProfiles").IsVisible);
        Assert.False(UiFixture.Named<Control>(window, "PageDevice").IsVisible);
        Assert.True(UiFixture.Tab(window, 2).Classes.Contains("selected"));
        Assert.Contains("›", UiFixture.Named<TextBlock>(window, "SectionTitle").Text);

        UiFixture.Key(window, Key.Escape);

        Assert.True(window.IsVisible);
        Assert.True(UiFixture.Named<Control>(window, "PageDevice").IsVisible);
        Assert.Same(UiFixture.Named<Button>(UiFixture.Named<Control>(window, "PageDevice"), "OpenProfiles"), window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void SearchSuggestsAsYouTypeAndChoosingOneJumpsToTheRow()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var search = UiFixture.Named<TextBox>(window, "SearchBox");
        var results = UiFixture.Named<Border>(window, "SearchResults");
        Assert.False(results.IsVisible);

        search.Text = "verbose";
        Dispatcher.UIThread.RunJobs();

        Assert.True(results.IsVisible);
        var row = Assert.Single(UiFixture.Named<StackPanel>(window, "SearchList").Children.OfType<Button>());
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Diagnostics › Log");

        UiFixture.Click(window, row);
        Dispatcher.UIThread.RunJobs();

        Assert.False(results.IsVisible);
        Assert.Empty(search.Text ?? string.Empty);
        Assert.True(UiFixture.Named<Control>(window, "PageDiagnostics").IsVisible);
        Assert.True(UiFixture.Tab(window, 9).Classes.Contains("selected"));
        var focused = Assert.IsAssignableFrom<Control>(window.FocusManager!.GetFocusedElement());
        Assert.IsType<ToggleSwitch>(focused);
    }

    [AvaloniaFact]
    public void BackClosesSuggestionsBeforeItClosesTheWindow()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var search = UiFixture.Named<TextBox>(window, "SearchBox");
        search.Text = "update";
        Dispatcher.UIThread.RunJobs();

        UiFixture.Key(window, Key.Escape);

        Assert.True(window.IsVisible);
        Assert.False(UiFixture.Named<Border>(window, "SearchResults").IsVisible);
    }

    [AvaloniaFact]
    public void ARowHiddenByItsSettingIsNotOffered()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var search = UiFixture.Named<TextBox>(window, "SearchBox");
        var list = UiFixture.Named<StackPanel>(window, "SearchList");

        model.CefEnabled = true;
        Dispatcher.UIThread.RunJobs();
        search.Text = "carousel";
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(list.Children);

        model.CefEnabled = false;
        Dispatcher.UIThread.RunJobs();
        search.Text = "carousel ";
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(list.Children);
    }

    [AvaloniaFact]
    public void SearchSuggestionsRender()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        UiFixture.Named<TextBox>(window, "SearchBox").Text = "start";
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, "settings-search-1024");
    }
}
