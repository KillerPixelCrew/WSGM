using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Settings;
using WSGM.Themes;

namespace WSGM.UiTests;

public sealed class SettingsInteractionTests
{
    [AvaloniaFact]
    public void SuccessfulSaveMergesTheEditedValueIntoTheIsolatedStore()
    {
        using UiFixture fixture = new();
        SettingsWindow window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        ToggleSwitch toggle = UiFixture.Named<Control>(window, "PageSystem").GetVisualDescendants().OfType<ToggleSwitch>().First();
        UiFixture.Click(window, toggle);
        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes")));
        Assert.Equal(model.GameModeBootEnabled, fixture.Saved.GameModeBootEnabled);
        Assert.Equal(1, fixture.Calls.Count(call => call == "save"));
        Assert.StartsWith("Saved", model.StatusText);
    }

    [AvaloniaFact]
    public void TabsKeepDeviceAndPluginAvailableWithoutIntegrationAndLandFocus()
    {
        using UiFixture fixture = new();
        SettingsWindow window = fixture.Settings();
        foreach ((int index, string name) in new[] { (3, "PageDevice"), (8, "PagePluginSettings"), (5, "PageQuickAccess") })
        {
            UiFixture.Click(window, UiFixture.Tab(window, index));
            Control page = UiFixture.Named<Control>(window, name);
            Assert.True(page.IsVisible);
            Assert.Equal(index, UiFixture.Named<TabStrip>(window, "Tabs").SelectedIndex);
        }
        Button system = UiFixture.Tab(window, 0);
        system.Focus();
        UiFixture.Key(window, Key.Enter);
        Assert.True(UiFixture.Named<Control>(window, "PageSystem").IsVisible);
        Assert.NotNull(window.FocusManager?.GetFocusedElement());
    }

    [AvaloniaFact]
    public async Task SaveDisablesEditorsUntilTheCapturedRequestCompletes()
    {
        using UiFixture fixture = new();
        TaskCompletionSource<SettingsViewModel.SaveResult> completion = new();
        SettingsViewModel.SaveRequest? captured = null;
        fixture.Persist = request => { captured = request; return completion.Task; };
        SettingsWindow window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        ToggleSwitch toggle = UiFixture.Named<Control>(window, "PageSystem").GetVisualDescendants().OfType<ToggleSwitch>().First();
        bool before = model.GameModeBootEnabled;
        UiFixture.Click(window, toggle);
        Assert.Equal(!before, model.GameModeBootEnabled);
        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes")));
        Assert.True(model.IsSaving);
        Assert.False(UiFixture.Named<Control>(window, "SettingsRoot").IsEnabled);
        Assert.NotNull(captured);
        Assert.Equal(!before, captured.Values.GameModeBootEnabled);
        completion.SetResult(new(captured.Values, [], null));
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
        SettingsWindow window = fixture.Settings();
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
        for (int i = 0; i < 3; i++)
        {
            SettingsWindow window = fixture.Settings();
            var model = Assert.IsType<SettingsViewModel>(window.DataContext);

            model.AccentColorHex = "#FF0000";
            AccentPalette.Apply(Avalonia.Application.Current!, AccentPalette.Parse(model.AccentColorHex));
            UiFixture.Key(window, Key.Escape);
            Assert.False(window.IsVisible);
            Assert.Equal("#4CC2FF", fixture.Saved.AccentColor);
            var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(Avalonia.Application.Current!.Resources["HcAccentBrush"]);
            Assert.Equal(AccentPalette.Parse(fixture.Saved.AccentColor), brush.Color);
        }
        Assert.Equal(3, fixture.Calls.Count(call => call == "input-start"));
        Assert.Equal(3, fixture.Calls.Count(call => call == "input-stop"));
        Assert.Equal(3, fixture.Calls.Count(call => call == "window-import-begin"));
        Assert.Equal(3, fixture.Calls.Count(call => call == "window-import-end"));
    }
}
