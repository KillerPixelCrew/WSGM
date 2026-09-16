using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Settings;
using WSGM.Themes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsInteractionTests
{
    [AvaloniaFact]
    public void SuccessfulSaveMergesTheEditedValueIntoTheIsolatedStore()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var toggle = UiFixture.Named<Control>(window, "PageSystem").GetVisualDescendants().OfType<ToggleSwitch>().First();
        UiFixture.Click(window, toggle);
        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes")));
        Assert.Equal(model.StartAtSignIn, fixture.Saved.StartAtSignIn);
        Assert.Equal(1, fixture.Calls.Count(call => call == "save"));
        Assert.StartsWith("Saved", model.StatusText);
    }

    [AvaloniaFact]
    public void TabsKeepDeviceAndPluginAvailableWithoutIntegrationAndLandFocus()
    {
        using UiFixture fixture = new();
        var window = fixture.Settings();
        foreach (var (index, name) in new[] { (3, "PageDevice"), (8, "PagePluginSettings"), (5, "PageQuickAccess") })
        {
            UiFixture.Click(window, UiFixture.Tab(window, index));
            var page = UiFixture.Named<Control>(window, name);
            Assert.True(page.IsVisible);
            Assert.Equal(index, UiFixture.Named<TabStrip>(window, "Tabs").SelectedIndex);
        }
        var system = UiFixture.Tab(window, 0);
        system.Focus();
        UiFixture.Key(window, Key.Enter);
        Assert.True(UiFixture.Named<Control>(window, "PageSystem").IsVisible);
        Assert.NotNull(window.FocusManager.GetFocusedElement());
    }

    [AvaloniaFact]
    public async Task SaveDisablesEditorsUntilTheCapturedRequestCompletes()
    {
        using UiFixture fixture = new();
        TaskCompletionSource<SettingsViewModel.SaveResult> completion = new();
        SettingsViewModel.SaveRequest? captured = null;
        fixture.Persist = request => { captured = request; return completion.Task; };
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var toggle = UiFixture.Named<Control>(window, "PageSystem").GetVisualDescendants().OfType<ToggleSwitch>().First();
        var before = model.StartAtSignIn;
        UiFixture.Click(window, toggle);
        Assert.Equal(!before, model.StartAtSignIn);
        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes")));
        Assert.True(model.IsSaving);
        Assert.False(UiFixture.Named<Control>(window, "SettingsRoot").IsEnabled);
        Assert.NotNull(captured);
        Assert.Equal(!before, captured.Values.StartAtSignIn);
        completion.SetResult(new SettingsViewModel.SaveResult(captured.Values, [], null));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(model.IsSaving);
        Assert.True(UiFixture.Named<Control>(window, "SettingsRoot").IsEnabled);
        Assert.StartsWith("Saved", model.StatusText);
        Assert.Equal(1, fixture.Calls.Count(call => call == "reconcile"));
    }

    [AvaloniaFact]
    public void FailedSaveReportsTheErrorAndDoesNotReconcileExternalState()
    {
        using UiFixture fixture = new();
        fixture.Persist = _ => throw new IOException("fixture disk full");
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes")));
        Assert.Contains("fixture disk full", model.StatusText);
        Assert.False(model.IsSaving);
        Assert.DoesNotContain("reconcile", fixture.Calls);
        Assert.Equal(1, fixture.Calls.Count(call => call == "save-import-end"));
    }

    [AvaloniaFact]
    public void ClosingRestoresTheSavedAccentAndPairsEachWindowLifetime()
    {
        using UiFixture fixture = new();
        for (var i = 0; i < 3; i++)
        {
            var window = fixture.Settings();
            var model = Assert.IsType<SettingsViewModel>(window.DataContext);

            model.AccentColorHex = "#FF0000";
            AccentPalette.Apply(Application.Current!, AccentPalette.Parse(model.AccentColorHex));
            UiFixture.Key(window, Key.Escape);
            Assert.False(window.IsVisible);
            Assert.Equal("#4CC2FF", fixture.Saved.AccentColor);
            var brush = Assert.IsType<ISolidColorBrush>(Application.Current!.Resources["HcAccentBrush"], exactMatch: false);
            Assert.Equal(AccentPalette.Parse(fixture.Saved.AccentColor), brush.Color);
        }
        Assert.Equal(3, fixture.Calls.Count(call => call == "input-start"));
        Assert.Equal(3, fixture.Calls.Count(call => call == "input-stop"));
        Assert.Equal(3, fixture.Calls.Count(call => call == "window-import-begin"));
        Assert.Equal(3, fixture.Calls.Count(call => call == "window-import-end"));
    }
}
