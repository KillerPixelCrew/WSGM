using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class ApplicationProfilesViewTests
{
    [AvaloniaFact]
    public async Task HeaderScopePersistsAndRemainsVisibleAcrossDestinations()
    {
        var profiles = Profiles();
        await using var service = Service(profiles);
        await service.ApplyProfilesAsync(
            profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:42", 42, "game.exe")), true);
        using var bridge = new PerformanceOverlayBridge(service, profiles);
        using var fixture = new UiFixture();
        var window = fixture.Overlay(1280, 720);
        window.AttachPerformanceSource(bridge);
        var selector = UiFixture.Named<ComboBox>(window, "HeaderProfile");
        selector.SelectedIndex = 1;
        await WaitAsync(() => profiles.Current.EditsGame && selector.IsEnabled);
        var created = Assert.Single(profiles.Current.Config.Games);
        Assert.True(created.Enabled);
        // Opting in creates an empty profile: nothing is copied from Global.
        Assert.Equal(0, created.Values.Count());
        for (var tab = 0; tab < 4; tab++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, tab));
            Assert.True(selector.IsEffectivelyVisible);
            Assert.Equal(1, selector.SelectedIndex);
            Assert.True(UiFixture.Named<Button>(window, "ManageProfiles").IsEffectivelyVisible);
        }

        selector.SelectedIndex = 0;
        await WaitAsync(() => !profiles.Current.EditsGame && selector.IsEnabled);
        Assert.False(Assert.Single(profiles.Current.Config.Games).Enabled);
    }

    [AvaloniaTheory]
    [InlineData(1280, 720, 1.0, "overlay-profiles-720p")]
    [InlineData(3840, 2160, 2.0, "overlay-profiles-4k-scaled")]
    public async Task EditorCreatesEditsAndDeletesProfilesWithoutARunningApplication(int width, int height,
        double scale, string baseline)
    {
        var profiles = Profiles();
        await using var service = Service(profiles);
        using var bridge = new PerformanceOverlayBridge(service, profiles);
        using var fixture = new UiFixture();
        var window = fixture.Overlay(width, height, scale);
        window.AttachPerformanceSource(bridge);
        UiFixture.Click(window, UiFixture.Named<Button>(window, "ManageProfiles"));
        var editor = window.GetVisualDescendants().OfType<ApplicationProfilesView>().Single();

        Button Button(string name)
        {
            return editor.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, name));
        }

        // The name and process rows are press-to-edit: each opens the overlay keyboard.
        async Task EnterAsync(Button row, string text)
        {
            UiFixture.Click(window, row);
            Dispatcher.UIThread.RunJobs();
            var keyboard = window.GetVisualDescendants().OfType<KeyboardPanel>().Single();
            UiFixture.Named<TextBox>(keyboard, "Input").Text = text;
            UiFixture.Click(window, keyboard.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, Key.Enter)));
            await WaitAsync(() => !window.GetVisualDescendants().OfType<KeyboardPanel>().Any());
        }

        var name = editor.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Profile name");
        await EnterAsync(name, "My game");
        Assert.Equal("My game", name.Content);
        await EnterAsync(Button("Add process"), "game.exe");
        await EnterAsync(Button("Add process"), "launcher.exe");
        await EnterAsync(Button("Add process"), "old.exe");
        await EnterAsync(Button("old.exe"), "");
        UiFixture.Click(window, Button("Save profile"));
        await WaitAsync(() => editor.IsEnabled && profiles.Current.Config.Games.Count == 1);
        var entry = Assert.Single(profiles.Current.Config.Games);
        Assert.Equal("My game", entry.Name);
        Assert.Equal(["game.exe", "launcher.exe"], entry.ProcessNames);
        Assert.Equal(0, entry.Values.Count());
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, baseline);
        await EnterAsync(Button("launcher.exe"), "newgame.exe");
        UiFixture.Click(window, Button("Save profile"));
        await WaitAsync(() =>
            editor.IsEnabled && profiles.Current.Config.Games[0].ProcessNames.Contains("newgame.exe"));
        Assert.Equal(entry.Id, profiles.Current.Config.Games[0].Id);
        Assert.Equal(["game.exe", "newgame.exe"], profiles.Current.Config.Games[0].ProcessNames);
        UiFixture.Click(window, Button("Delete profile"));
        Assert.Single(profiles.Current.Config.Games);
        UiFixture.Click(window, Button("Confirm delete"));
        await WaitAsync(() => editor.IsEnabled && profiles.Current.Config.Games.Count == 0);
    }

    internal static ProfileService Profiles()
    {
        var store = new ProfileConfig();
        var gate = new Lock();
        return new ProfileService(store, (edit, _) =>
        {
            lock (gate)
            {
                edit(store);
                return Task.FromResult(ConfigJson.Clone(store, ConfigJsonContext.Tolerant.ProfileConfig));
            }
        });
    }

    internal static PerformanceService Service(ProfileService profiles)
    {
        return new PerformanceService(new SimulatedRtssAdapter(),
            new RtssLauncher(static _ => Task.FromResult(false), static (_, _) => null),
            (field, value, token) => profiles.SetAsync(field, value, token), profiles.Current);
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        await AsyncConditions.WaitForAsync(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return predicate();
        });
        Dispatcher.UIThread.RunJobs();
    }
}
