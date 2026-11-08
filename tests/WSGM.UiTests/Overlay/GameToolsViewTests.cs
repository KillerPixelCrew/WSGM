using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class GameToolsViewTests
{
    [AvaloniaTheory]
    [InlineData("overlay-game-tools-list-1280", "list", 1280, 800)]
    [InlineData("overlay-game-tools-list-980", "list", 980, 640)]
    [InlineData("overlay-game-tools-game-1280", "game", 1280, 800)]
    [InlineData("overlay-game-tools-game-980", "game", 980, 640)]
    [InlineData("overlay-game-tools-installed-1280", "installed", 1280, 800)]
    [InlineData("overlay-game-tools-installed-980", "installed", 980, 640)]
    [InlineData("overlay-game-tools-addons-1280", "addons", 1280, 800)]
    [InlineData("overlay-game-tools-addons-980", "addons", 980, 640)]
    [InlineData("overlay-game-tools-installing-1280", "installing", 1280, 800)]
    [InlineData("overlay-game-tools-installing-980", "installing", 980, 640)]
    [InlineData("overlay-game-tools-program-1280", "program", 1280, 800)]
    [InlineData("overlay-game-tools-program-980", "program", 980, 640)]
    [InlineData("overlay-game-tools-anticheat-1280", "anticheat", 1280, 800)]
    [InlineData("overlay-game-tools-anticheat-980", "anticheat", 980, 640)]
    [InlineData("overlay-game-tools-nightly-1280", "nightly", 1280, 800)]
    [InlineData("overlay-game-tools-nightly-980", "nightly", 980, 640)]
    [InlineData("overlay-game-tools-injectors-1280", "injectors", 1280, 800)]
    [InlineData("overlay-game-tools-injectors-980", "injectors", 980, 640)]
    [InlineData("overlay-game-tools-disabled-1280", "disabled", 1280, 800)]
    [InlineData("overlay-game-tools-disabled-980", "disabled", 980, 640)]
    [InlineData("overlay-game-tools-config-dg-1280", "config-dgvoodoo", 1280, 800)]
    [InlineData("overlay-game-tools-config-dg-980", "config-dgvoodoo", 980, 640)]
    [InlineData("overlay-game-tools-config-directx-1280", "config-directx", 1280, 800)]
    [InlineData("overlay-game-tools-config-directx-980", "config-directx", 980, 640)]
    [InlineData("overlay-game-tools-config-glide-1280", "config-glide", 1280, 800)]
    [InlineData("overlay-game-tools-config-glide-980", "config-glide", 980, 640)]
    [InlineData("overlay-game-tools-config-reshade-1280", "config-reshade", 1280, 800)]
    [InlineData("overlay-game-tools-config-reshade-980", "config-reshade", 980, 640)]
    [InlineData("overlay-game-tools-config-sk-1280", "config-special-k", 1280, 800)]
    [InlineData("overlay-game-tools-config-sk-980", "config-special-k", 980, 640)]
    [InlineData("overlay-game-tools-config-dxvk-1280", "config-dxvk", 1280, 800)]
    [InlineData("overlay-game-tools-config-dxvk-980", "config-dxvk", 980, 640)]
    [InlineData("overlay-game-tools-key-1280", "hotkey", 1280, 800)]
    [InlineData("overlay-game-tools-key-980", "hotkey", 980, 640)]
    [InlineData("overlay-game-tools-dlls-1280", "dlls", 1280, 800)]
    [InlineData("overlay-game-tools-dlls-980", "dlls", 980, 640)]
    public void Scene(string name, string scene, int width, int height)
    {
        using UiFixture fixture = new();
        var (window, _) = PreviewGameTools.Open(fixture, scene, width, height);
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaFact]
    public async Task DisabledToolEnablesThroughItsOwnAction()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "disabled", 1280, 800);
        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "tool:special-k.primary"));
        await source.NextCommandAsync();
        var command = Assert.Single(source.Commands);
        Assert.Equal("enable", command.Command);
        Assert.Equal([GameToolsFixtures.AlanWake, "special-k"], command.Arguments);
    }

    [AvaloniaFact]
    public async Task ChosenPresetDraftIsSentWithTheSelectedFileSet()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "config-directx", 1280, 800);
        var memory = UiFixture.Tagged<ComboBox>(window, "tool:dgvoodoo2.setting:dxVram.editor");
        memory.SelectedIndex = 3;
        memory.IsDropDownOpen = false;
        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "tool:dgvoodoo2.primary"));
        await source.NextCommandAsync();
        var request = Assert.IsType<GameToolsInstallRequest>(Assert.Single(source.Commands).Arguments[0]);
        Assert.Equal("512", request.Settings["dxVram"]);
        Assert.False(request.ReplaceExisting);
    }

    [AvaloniaFact]
    public async Task ShortcutSilentDefaultAndExplicitUserOverrideUseTheSharedSettingsCommand()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "config-special-k", 1280, 800);
        var toggle = UiFixture.Tagged<ToggleSwitch>(window, "tool:special-k.setting:steamSilent.editor");
        Assert.True(toggle.IsChecked);
        toggle.IsChecked = false;
        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "tool:special-k.primary"));
        await source.NextCommandAsync();
        var request = Assert.IsType<GameToolsInstallRequest>(Assert.Single(source.Commands).Arguments[0]);
        Assert.Equal("off", request.Settings["steamSilent"]);
    }

    [AvaloniaFact]
    public void DownloadTicksUpdateTheCardInPlaceAndANewPhaseRedrawsItsSteps()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "installing", 1280, 800);
        var progress = UiFixture.Tagged<OverlayProgress>(window, "game.busy");
        var cancel = UiFixture.Tagged<ActionButton>(window, "game.cancel");
        var busy = GameToolsFixtures.Installing.Busy!;

        source.Publish(GameToolsFixtures.Installing with { Busy = busy with { Bytes = 26_004_684 } });
        Dispatcher.UIThread.RunJobs();
        Assert.Same(progress, UiFixture.Tagged<OverlayProgress>(window, "game.busy"));
        Assert.Same(cancel, UiFixture.Tagged<ActionButton>(window, "game.cancel"));
        Assert.Equal("Downloading OptiScaler 0.9.4 from GitHub… · 25 MB of 31 MB", progress.State?.Text);
        var bar = progress.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.Equal(0.8, bar.Value, 3);

        // Writing game files can't be cancelled: the phase change redraws the steps and drops Cancel.
        source.Publish(GameToolsFixtures.Installing with
        {
            Busy = busy with
            {
                Phase = "copy", Detail = "Backing up game files and copying OptiScaler", Bytes = 0, Total = 0,
                Cancellable = false
            }
        });
        Dispatcher.UIThread.RunJobs();
        Assert.False(UiFixture.Tagged<OverlayProgress>(window, "game.busy").State?.Cancel is not null);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ActionButton>(),
            button => Equals(button.Tag, "game.cancel") && button.IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text?.StartsWith("Files are being written.",
                StringComparison.Ordinal) == true);

        // Every setting stays visible and locked while the work runs.
        Assert.False(UiFixture.Tagged<ComboBox>(window, "tool:optiscaler.setting:menuScale.editor").IsEnabled);
    }

    [AvaloniaFact]
    public async Task NightlyDraftLeadsTheInstallSurvivesPublicationAndIsSent()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "nightly", 1280, 800);
        Assert.Equal("Install OptiScaler nightly 10.0.0-pre1",
            UiFixture.Tagged<ActionButton>(window, "tool:optiscaler.primary").Title);

        source.Publish(GameToolsFixtures.NotInstalled with { Notice = "Checked for updates." });
        Dispatcher.UIThread.RunJobs();
        var primary = UiFixture.Tagged<ActionButton>(window, "tool:optiscaler.primary");
        Assert.Equal("Install OptiScaler nightly 10.0.0-pre1", primary.Title);
        Assert.Equal(1, UiFixture.Tagged<ComboBox>(window, "tool:optiscaler.channel.editor").SelectedIndex);

        UiFixture.Click(window, primary);
        await source.NextCommandAsync();
        var request = Assert.IsType<GameToolsInstallRequest>(Assert.Single(source.Commands).Arguments[0]);
        Assert.Equal("nightly", request.Channel);
        Assert.False(request.AcknowledgeRisk);
    }

    [AvaloniaFact]
    public async Task OptiPatcherFollowsItsRecommendationUntilTurnedOffAndTheDraftIsSent()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "game", 1280, 800);
        var toggle = UiFixture.Tagged<ToggleSwitch>(window, "tool:optiscaler.addon:optipatcher.editor");
        Assert.True(toggle.IsChecked);

        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        source.Publish(GameToolsFixtures.NotInstalled with { Notice = "Checked for updates." });
        Dispatcher.UIThread.RunJobs();
        Assert.False(UiFixture.Tagged<ToggleSwitch>(window, "tool:optiscaler.addon:optipatcher.editor").IsChecked);
        Assert.Empty(source.Commands);

        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "tool:optiscaler.primary"));
        await source.NextCommandAsync();
        var request = Assert.IsType<GameToolsInstallRequest>(Assert.Single(source.Commands).Arguments[0]);
        Assert.False(request.Addons!["optipatcher"]);
    }

    [AvaloniaFact]
    public async Task AnInstalledOptiPatcherIsRemovedAndUpdatedAtOnce()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "addons", 1280, 800);
        UiFixture.Click(window,
            UiFixture.Tagged<ActionButton>(window, "tool:optiscaler.addon-update:optipatcher"));
        await source.NextCommandAsync();
        Assert.Equal("setAddon", source.Commands[^1].Command);
        Assert.Equal([GameToolsFixtures.Cyberpunk, "optiscaler", "optipatcher", true], source.Commands[^1].Arguments);

        UiFixture.Tagged<ToggleSwitch>(window, "tool:optiscaler.addon:optipatcher.editor").IsChecked = false;
        await source.NextCommandAsync();
        Assert.Equal("setAddon", source.Commands[^1].Command);
        Assert.Equal([GameToolsFixtures.Cyberpunk, "optiscaler", "optipatcher", false], source.Commands[^1].Arguments);
    }

    [AvaloniaFact]
    public async Task AntiCheatConfirmFocusesCancelAndInstallsOnlyWhenAccepted()
    {
        using UiFixture fixture = new();
        var (window, source) = PreviewGameTools.Open(fixture, "anticheat", 1280, 800);
        Assert.True(window.Surfaces.HasActive);
        var cancel = UiFixture.Tagged<ActionButton>(window, "confirm.cancel");
        Assert.True(cancel.IsFocused);
        Assert.Equal("Install anyway", UiFixture.Tagged<ActionButton>(window, "confirm.accept").Title);

        await CloseAsync(window, () => UiFixture.Click(window, cancel));
        Assert.Empty(source.Commands);

        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "tool:optiscaler.primary"));
        Dispatcher.UIThread.RunJobs();
        await CloseAsync(window,
            () => UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "confirm.accept")));
        await source.NextCommandAsync();
        var request = Assert.IsType<GameToolsInstallRequest>(Assert.Single(source.Commands).Arguments[0]);
        Assert.True(request.AcknowledgeRisk);
        Assert.Null(request.Channel);
    }

    [AvaloniaFact]
    public void UpdatesFilterShowsOnlyGamesWithAKnownUpdate()
    {
        using UiFixture fixture = new();
        var (window, _) = PreviewGameTools.Open(fixture, "list", 1280, 800);
        Assert.Equal("Updates 1", UiFixture.Tagged<ActionButton>(window, "games.filter:updates").Title);

        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "games.filter:updates"));
        Dispatcher.UIThread.RunJobs();
        var rows = window.GetVisualDescendants().OfType<OverlayMediaRow>().Where(row => row.IsEffectivelyVisible)
            .ToArray();
        Assert.Equal("Cyberpunk 2077", Assert.Single(rows).Title);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(),
            control => control.Tag is string tag && tag.StartsWith("games.grid", StringComparison.Ordinal));
    }

    // The surface closes after its touch grace, which the dispatcher's timer runs while the test awaits.
    private static async Task CloseAsync(OverlayWindow window, Action close)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Surfaces.Closed += Closed;
        try
        {
            close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            window.Surfaces.Closed -= Closed;
        }

        Assert.False(window.Surfaces.HasActive);
        return;

        void Closed()
        {
            closed.TrySetResult();
        }
    }
}
