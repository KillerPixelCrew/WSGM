using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsUnsavedChangesTests
{
    [AvaloniaFact]
    public void OpeningCountsNothingAndAnEditIsCountedMarkedAndClearedBySaving()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("No unsaved changes", model.UnsavedChangesText);
        Assert.Empty(PendingRows(window));

        UiFixture.Click(window, UiFixture.Named<Control>(window, "PageGeneral").GetVisualDescendants()
            .OfType<ToggleSwitch>().First());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1 unsaved change", model.UnsavedChangesText);
        var pending = Assert.Single(PendingRows(window));
        Assert.Contains(pending.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Start WSGM at sign-in");

        UiFixture.Click(window, Save(window));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("No unsaved changes", model.UnsavedChangesText);
        Assert.Empty(PendingRows(window));
        Assert.Equal(1, fixture.Calls.Count(call => call == "save"));
    }

    [AvaloniaFact]
    public void PuttingAValueBackIsNotAChange()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        Dispatcher.UIThread.RunJobs();
        var toggle = UiFixture.Named<Control>(window, "PageGeneral").GetVisualDescendants().OfType<ToggleSwitch>().First();

        UiFixture.Click(window, toggle);
        UiFixture.Click(window, toggle);
        Dispatcher.UIThread.RunJobs();

        Assert.False(model.HasUnsavedChanges);
        Assert.Empty(PendingRows(window));
    }

    [AvaloniaFact]
    public void SaveWithNothingToWriteSaysSoAndWritesNothing()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        Dispatcher.UIThread.RunJobs();

        UiFixture.Click(window, Save(window));

        Assert.Equal("Nothing to save.", model.StatusText);
        Assert.DoesNotContain("save", fixture.Calls);
    }

    [AvaloniaFact]
    public void EveryEditCountsOnce()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        Dispatcher.UIThread.RunJobs();

        model.StartAtSignIn = !model.StartAtSignIn;
        model.VerboseLogging = !model.VerboseLogging;
        model.CheckForUpdates = !model.CheckForUpdates;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("3 unsaved changes", model.UnsavedChangesText);
    }

    private static Button Save(Window window)
    {
        return window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes"));
    }

    private static IEnumerable<Border> PendingRows(Window window)
    {
        return window.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("setting-row") && border.Classes.Contains("pending"));
    }
}
