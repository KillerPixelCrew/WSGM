using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class ApplicationProfilesViewTests
{
    [AvaloniaFact]
    public async Task HeaderScopePersistsAndRemainsVisibleAcrossDestinations()
    {
        PerformancePolicy? saved = null;
        await using var service = new PerformanceService(new SimulatedRtssAdapter(), (policy, _) =>
        {
            saved = policy;
            return Task.CompletedTask;
        });
        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:42", 42, "game.exe"));
        using var bridge = new PerformanceOverlayBridge(service);
        using var fixture = new UiFixture();
        var window = fixture.Overlay(1280, 720);
        window.AttachPerformanceSource(bridge);
        var selector = UiFixture.Named<ComboBox>(window, "HeaderProfile");
        selector.SelectedIndex = 1;
        await WaitAsync(() => service.Current.ApplicationProfileEnabled && selector.IsEnabled);
        Assert.True(Assert.Single(saved!.Applications).Enabled);
        for (var tab = 0; tab < 4; tab++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, tab));
            Assert.True(selector.IsEffectivelyVisible);
            Assert.Equal(1, selector.SelectedIndex);
            Assert.True(UiFixture.Named<Button>(window, "ManageProfiles").IsEffectivelyVisible);
        }

        selector.SelectedIndex = 0;
        await WaitAsync(() => !service.Current.ApplicationProfileEnabled && selector.IsEnabled);
        Assert.False(Assert.Single(saved!.Applications).Enabled);
    }

    [AvaloniaTheory]
    [InlineData(1280, 720, 1.0, "overlay-profiles-720p")]
    [InlineData(3840, 2160, 2.0, "overlay-profiles-4k-scaled")]
    public async Task EditorCreatesEditsAndDeletesProfilesWithoutARunningApplication(int width, int height,
        double scale, string baseline)
    {
        await using var service = new PerformanceService(new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        using var bridge = new PerformanceOverlayBridge(service);
        using var fixture = new UiFixture();
        var window = fixture.Overlay(width, height, scale);
        window.AttachPerformanceSource(bridge);
        UiFixture.Click(window, UiFixture.Named<Button>(window, "ManageProfiles"));
        var editor = window.GetVisualDescendants().OfType<ApplicationProfilesView>().Single();

        TextBox Text(string name)
        {
            return editor.GetVisualDescendants().OfType<TextBox>()
                .Single(box => AutomationProperties.GetName(box) == name);
        }

        Button Button(string name)
        {
            return editor.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, name));
        }

        Text("Profile name").Text = "My game";
        Text("Activation processes").Text = "game.exe\nlauncher.exe";
        Text("Profile frame limit").Text = "40";
        UiFixture.Click(window, Button("Save profile"));
        await WaitAsync(() => editor.IsEnabled && service.Profiles.Count == 1);
        var entry = Assert.Single(service.Profiles);
        Assert.Equal(["game.exe", "launcher.exe"], entry.ProcessNames);
        Assert.Equal(40, entry.Values.FrameLimit);
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, baseline);
        Text("Activation processes").Text = "newgame.exe";
        UiFixture.Click(window, Button("Save profile"));
        await WaitAsync(() => editor.IsEnabled && service.Profiles[0].ProcessNames.Contains("newgame.exe"));
        Assert.Equal(entry.ApplicationId, service.Profiles[0].ApplicationId);
        UiFixture.Click(window, Button("Delete profile"));
        Assert.Single(service.Profiles);
        UiFixture.Click(window, Button("Confirm delete"));
        await WaitAsync(() => editor.IsEnabled && service.Profiles.Count == 0);
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
    }
}
