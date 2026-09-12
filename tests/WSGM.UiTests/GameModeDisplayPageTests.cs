using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Settings;

namespace WSGM.UiTests;

public sealed class GameModeDisplayPageTests
{
    private static readonly DisplayTargetIdentity Tv =
        new(@"\\?\DISPLAY#TV0001", null, null, "Living room TV", 0, 0, 3);

    private static DisplayArrangement Desktop() => new(
        [new(Tv, true, true, new(Tv, 0, 0, 3840, 2160, DisplayRefresh.FromHertz(120), DpiPercent: 150, Hdr: true))],
        "one-tv", DateTimeOffset.UnixEpoch);

    private static SettingsWindow Open(UiFixture fixture)
    {
        SettingsWindow window = fixture.Settings();
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        return window;
    }

    [AvaloniaFact]
    public void TheCustomLayoutFieldsAppearOnlyForACustomLaunch()
    {
        using UiFixture fixture = new();
        SettingsWindow window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);

        Assert.False(model.ShowCustomLaunch);
        Assert.Contains("primary", model.LaunchSummaryText, StringComparison.Ordinal);

        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;

        Assert.True(model.ShowCustomLaunch);
    }

    [AvaloniaFact]
    public void SnapshotCapturesTheDesktopAndRemembersItsDisplayForLater()
    {
        using UiFixture fixture = new() { Displays = Desktop() };
        SettingsWindow window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;

        model.SnapshotGameLayoutCommand.Execute(null);

        DisplayLayoutRow row = Assert.Single(model.GameLayoutRows);
        Assert.Equal("Living room TV", row.DisplayName);
        Assert.Contains("3840x2160", row.Summary, StringComparison.Ordinal);
        Assert.Contains("120 Hz", row.Summary, StringComparison.Ordinal);
        Assert.Contains("150% scaling", row.Summary, StringComparison.Ordinal);
        Assert.Contains("HDR on", row.Summary, StringComparison.Ordinal);
        Assert.False(row.NeedsConfirmation);
        // Remembered, so the same display can be chosen as the wait target while it is unplugged.
        Assert.Equal("Living room TV", Assert.Single(model.KnownDisplays).Target!.FriendlyName);
        Assert.Equal(["No display wait", "Living room TV"], model.WaitForDisplayChoices);
    }

    [AvaloniaFact]
    public void ASavedLayoutAndItsWaitTargetSurviveASave()
    {
        using UiFixture fixture = new() { Displays = Desktop() };
        SettingsWindow window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.SnapshotGameLayoutCommand.Execute(null);
        model.WaitForDisplayIndex = 1;

        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Save changes")));

        GameModeLaunchConfiguration saved = fixture.Saved.GameModeLaunch;
        Assert.Equal(GameModeLaunchKind.Custom, saved.Kind);
        Assert.Equal(3840, Assert.Single(saved.GameLayout!.Outputs).Width);
        Assert.Equal("Living room TV", saved.WaitForDisplay!.FriendlyName);
        Assert.Equal("Living room TV", Assert.Single(saved.KnownDisplays).Target!.FriendlyName);
    }

    [AvaloniaFact]
    public void AMigratedLayoutSaysWhichDisplaysStillNeedConfirming()
    {
        using UiFixture fixture = new();
        // What the retired per-monitor profiles migrate into: real values, no resolvable identity.
        fixture.Saved.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        fixture.Saved.GameModeLaunch.GameLayout = new([
            new(new("", null, null, "Internal panel", 0, 0, 0), 0, 0, 1280, 720,
                DisplayRefresh.FromHertz(120))]);

        SettingsWindow window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);

        DisplayLayoutRow row = Assert.Single(model.GameLayoutRows);
        Assert.True(row.NeedsConfirmation);
        Assert.Contains("Confirm which display", row.ConfirmationText, StringComparison.Ordinal);
        Assert.Contains("need confirming", model.LaunchSummaryText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ADesktopThatCannotBeALayoutIsRefusedRatherThanSaved()
    {
        using UiFixture fixture = new()
        {
            // No active display: nothing to capture, and an empty layout would blank the desktop.
            Displays = new([new(Tv, true, false, null)], "none-active", DateTimeOffset.UnixEpoch),
        };
        SettingsWindow window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);

        model.SnapshotGameLayoutCommand.Execute(null);

        Assert.Empty(model.GameLayoutRows);
        Assert.Contains("cannot be saved as a layout", model.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SavedActionStepsAreListedWithTheirArgumentsAndDeadline()
    {
        using UiFixture fixture = new();
        fixture.Saved.GameModeLaunch.EnterActions =
        [
            new()
            {
                Plugin = new("wsgm.ir", "blaster"),
                ActionId = "remote-run",
                Arguments = { ["remote"] = new(Text: "hdmi-switch") },
                TimeoutSeconds = 45,
            },
        ];

        SettingsWindow window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);

        PluginActionStepRow row = Assert.Single(model.EnterActionRows);
        Assert.Equal("wsgm.ir / blaster: remote-run", row.Title);
        Assert.Contains("remote=hdmi-switch", row.Summary, StringComparison.Ordinal);
        Assert.Contains("45 s", row.Summary, StringComparison.Ordinal);
        // Settings owns no plugin host, so a saved step's plugin is named but never resolved.
        Assert.Contains("not running", row.Summary, StringComparison.Ordinal);
    }
}
