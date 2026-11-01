using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class EmulatorManagerViewTests
{
    [AvaloniaTheory]
    [InlineData("overlay-emulators-installed-1280", "installed", 1280, 800)]
    [InlineData("overlay-emulators-installed-980", "installed", 980, 640)]
    [InlineData("overlay-emulators-catalog-1280", "catalog", 1280, 800)]
    [InlineData("overlay-emulators-catalog-980", "catalog", 980, 640)]
    [InlineData("overlay-emulators-cores-1280", "cores", 1280, 800)]
    [InlineData("overlay-emulators-cores-980", "cores", 980, 640)]
    [InlineData("overlay-emulators-defaults-1280", "defaults", 1280, 800)]
    [InlineData("overlay-emulators-defaults-980", "defaults", 980, 640)]
    [InlineData("overlay-emulators-bios-1280", "bios", 1280, 800)]
    [InlineData("overlay-emulators-bios-980", "bios", 980, 640)]
    [InlineData("overlay-emulators-detail-1280", "detail", 1280, 800)]
    [InlineData("overlay-emulators-detail-980", "detail", 980, 640)]
    [InlineData("overlay-emulators-remove-1280", "remove", 1280, 800)]
    [InlineData("overlay-emulators-remove-980", "remove", 980, 640)]
    [InlineData("overlay-emulators-installing-1280", "installing", 1280, 800)]
    [InlineData("overlay-emulators-installing-980", "installing", 980, 640)]
    public void Scene(string name, string scene, int width, int height)
    {
        using UiFixture fixture = new();
        var (window, _) = PreviewEmulatorPage.Open(fixture, scene, width, height);
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        VisualBaseline.Verify(window, name);
    }

    [AvaloniaFact]
    public void InstalledAndGetEmulatorsSeparateInstallationsAndBackReturnsToTheCard()
    {
        using var fixture = new UiFixture();
        var (window, _) = PreviewEmulatorPage.Open(fixture, "installed", 1280, 800);
        Assert.Contains(Visible(window), button => button.Title == "PCSX2");
        Assert.DoesNotContain(Visible(window), button => button.Title == "RPCS3");
        PreviewEmulatorPage.Click(window, "Get emulators");
        Assert.Contains(Visible(window), button => button.Title == "RPCS3");
        Assert.Contains(Visible(window), button => button.Description == "Needs after install: PlayStation 3 firmware");
        PreviewEmulatorPage.Click(window, "Installed 6");
        PreviewEmulatorPage.Click(window, "PCSX2");
        Assert.Contains(Visible(window), button => button.Title == "Remove PCSX2…");
        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(Visible(window), button => button.Title == "Remove PCSX2…");
        Assert.Equal("emu.installed:pcsx2", (window.FocusManager!.GetFocusedElement() as ActionButton)?.Tag);
    }

    [AvaloniaFact]
    public async Task BusyInstallationKeepsCancellationReachableAndDisablesPackageWrites()
    {
        using var fixture = new UiFixture();
        var (window, source) = PreviewEmulatorPage.Open(fixture, "installing", 1280, 800);
        Assert.False(UiFixture.Tagged<ActionButton>(window, "emu.external").IsEnabled);
        Assert.Contains(Visible(window), button => button.Title == "Stop operation");
        Assert.Single(window.GetVisualDescendants().OfType<OverlayProgress>(), progress => progress.IsVisible);
        UiFixture.Click(window, Visible(window).Single(button => button.Title == "Stop operation"));
        Assert.Equal("CancelAsync", await source.CommandCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("InstallEmulatorAsync", source.Commands);
    }

    [AvaloniaFact]
    public async Task UpdateBothAppliesEveryPendingUpdateThroughTheBackend()
    {
        using var fixture = new UiFixture();
        var (window, source) = PreviewEmulatorPage.Open(fixture, "installed", 1280, 800);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "2 updates ready: RetroArch 1.22.0 and Cemu 2.7");
        PreviewEmulatorPage.Click(window, "Update both");
        Assert.Equal("UpdateAllEmulatorsAsync", await source.CommandCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public void ShouldersSwitchViewsAndFocusTheSelectedSegment()
    {
        using var fixture = new UiFixture();
        var (window, _) = PreviewEmulatorPage.Open(fixture, "installed", 1280, 800);
        var view = UiFixture.Named<EmulatorManagerView>(window, "EmulatorManagerHost");
        Assert.True(view.SelectAdjacentView(true));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("selected", UiFixture.Tagged<ActionButton>(window, "emu.view:catalog").Classes);
        Assert.Equal("emu.view:catalog", (window.FocusManager!.GetFocusedElement() as ActionButton)?.Tag);
        Assert.True(view.SelectAdjacentView(false));
        Assert.True(view.SelectAdjacentView(false));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("selected", UiFixture.Tagged<ActionButton>(window, "emu.view:bios").Classes);
    }

    [AvaloniaFact]
    public async Task RemoveAsksWithCancelFocusedAndNamesWhatStays()
    {
        using var fixture = new UiFixture();
        var (window, source) = PreviewEmulatorPage.Open(fixture, "remove", 1280, 800);
        var cancel = UiFixture.Tagged<ActionButton>(window, "confirm.cancel");
        Assert.Same(cancel, window.FocusManager!.GetFocusedElement());
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains(@"stay in D:\EmulatorData\pcsx2", StringComparison.Ordinal) == true);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Surfaces.Closed += () => closed.TrySetResult();
        UiFixture.Click(window, cancel);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Surfaces.HasActive);
        Assert.DoesNotContain("RemoveEmulatorAsync", source.Commands);
        PreviewEmulatorPage.Click(window, "Remove PCSX2…");
        UiFixture.Click(window, Assert.Single(Visible(window), button => button.Title == "Remove"));
        Assert.Equal("RemoveEmulatorAsync", await source.CommandCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public void YOpensDetailsOnGetEmulatorsAndTheSearchOnCoresAndIsNamedOnlyThere()
    {
        using var fixture = new UiFixture();
        var (window, _) = PreviewEmulatorPage.Open(fixture, "installed", 1280, 800);
        var view = UiFixture.Named<EmulatorManagerView>(window, "EmulatorManagerHost");
        Assert.Equal("", view.SecondaryHint);
        Assert.False(view.SecondaryAction(), "Installed leaves Y to the sheet");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Check for updates" && text.IsEffectivelyVisible && text.Classes.Contains("caption"));

        PreviewEmulatorPage.Click(window, "Get emulators");
        Assert.Equal("Details", view.SecondaryHint);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Details" && text.IsEffectivelyVisible);
        UiFixture.Tagged<ActionButton>(window, "emu.catalog:rpcs3.install").Focus();
        Assert.True(view.SecondaryAction());
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Emulators  ›  Get emulators  ›  RPCS3");
        Assert.Equal("", view.SecondaryHint);

        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        PreviewEmulatorPage.Click(window, "RetroArch cores");
        Assert.Equal("Search", view.SecondaryHint);
        Assert.True(view.SecondaryAction());
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Surfaces.HasActive, "Y asks for the core search");
    }

    [AvaloniaFact]
    public void ASystemWithGamesAndNoEmulatorLinksToTheCatalogueForIt()
    {
        using var fixture = new UiFixture();
        var (window, _) = PreviewEmulatorPage.Open(fixture, "defaults", 1280, 800);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "9 games have no emulator yet");
        UiFixture.Click(window, UiFixture.Tagged<ActionButton>(window, "default.get:ps3"));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("selected", UiFixture.Tagged<ActionButton>(window, "emu.view:catalog").Classes);
        Assert.Contains(Visible(window), button => button.Title == "RPCS3");
        Assert.DoesNotContain(Visible(window), button => button.Title == "Dolphin");
    }

    private static ActionButton[] Visible(OverlayWindow window)
    {
        return window.GetVisualDescendants().OfType<ActionButton>().Where(button => button.IsEffectivelyVisible)
            .ToArray();
    }
}
