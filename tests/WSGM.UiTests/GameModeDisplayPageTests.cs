using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Settings;

namespace WSGM.UiTests;

public sealed class GameModeDisplayPageTests
{
    private static readonly DisplayTargetIdentity Tv =
        new(@"\\?\DISPLAY#TV0001", null, null, "Living room TV", 0, 0, 3);

    private static readonly DisplayTargetIdentity Desk =
        new(@"\\?\DISPLAY#DESK01", null, null, "Desk monitor", 0, 0, 4);

    private static DisplayLayoutOutput Output(DisplayTargetIdentity target, int x = 0) =>
        new(target, x, 0, 3840, 2160, DisplayRefresh.FromHertz(120), DpiPercent: 150, Hdr: true);

    private static DisplayArrangement Desktop(params DisplayTargetIdentity[] targets) => new(
        [.. targets.Select((target, index) =>
            new DisplayTargetObservation(target, true, true, Output(target, index * 3840)))],
        "desktop", DateTimeOffset.UnixEpoch);

    private static SettingsWindow Open(UiFixture fixture)
    {
        SettingsWindow window = fixture.Settings();
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        return window;
    }

    private static SettingsViewModel Model(SettingsWindow window) =>
        Assert.IsType<SettingsViewModel>(window.DataContext);

    [AvaloniaFact]
    public void TheCustomLayoutFieldsAppearOnlyForACustomLaunch()
    {
        using UiFixture fixture = new();
        SettingsViewModel model = Model(Open(fixture));

        Assert.False(model.ShowCustomLaunch);
        Assert.Contains("primary", model.LaunchSummaryText, StringComparison.Ordinal);

        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;

        Assert.True(model.ShowCustomLaunch);
    }

    [AvaloniaFact]
    public void SnapshotCapturesTheDesktopAndRemembersWhatEachDisplaySupports()
    {
        using UiFixture fixture = new() { Displays = Desktop(Tv) };
        fixture.DisplayFacts[Tv.DevicePath] = new([new(3840, 2160, 120), new(1920, 1080, 60)], true, 225);
        SettingsViewModel model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;

        model.SnapshotGameLayoutCommand.Execute(null);

        DisplayLayoutEditorRow row = Assert.Single(model.GameLayout.Rows);
        Assert.Equal("Living room TV", row.DisplayName);
        Assert.True(row.Active);
        Assert.True(row.IsPrimary);
        Assert.True(row.Present);
        Assert.Equal(150, row.DpiPercent);
        Assert.True(row.HdrEnabled);
        Assert.Equal(new DisplayMode(3840, 2160, 120), row.Mode);
        // Remembered, so the same display stays configurable and pickable while it is unplugged.
        Assert.Equal(2, row.Modes.Count);
        Assert.Equal(["No display wait", "Living room TV"], model.WaitForDisplayChoices);
    }

    [AvaloniaFact]
    public void ARememberedDisplayCanBeSwitchedOnWhileItIsUnplugged()
    {
        using UiFixture fixture = new();
        // What the catalog looks like after the television was seen once and then unplugged.
        fixture.Saved.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        fixture.Saved.GameModeLaunch.KnownDisplays =
        [
            new() { Target = Tv, Modes = [new(3840, 2160, 120)], HdrSupported = true, MaximumDpiPercent = 225 },
        ];

        SettingsViewModel model = Model(Open(fixture));
        DisplayLayoutEditorRow row = Assert.Single(model.GameLayout.Rows);
        Assert.False(row.Present);
        Assert.Equal("Not connected right now", row.PresenceText);

        row.Active = true;
        row.Mode = row.Modes[0];

        Assert.True(row.IsPrimary);
        Assert.Empty(model.GameLayout.ValidationText);
        DisplayLayout built = Assert.IsType<DisplayLayout>(model.GameLayout.Build());
        Assert.Equal(3840, Assert.Single(built.Outputs).Width);
    }

    [AvaloniaFact]
    public void ChoosingAPrimaryMovesTheWholeArrangementSoItSitsAtTheOrigin()
    {
        using UiFixture fixture = new() { Displays = Desktop(Desk, Tv) };
        SettingsViewModel model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.SnapshotGameLayoutCommand.Execute(null);

        DisplayLayoutEditorRow desk = model.GameLayout.Rows.Single(row => row.DisplayName == "Desk monitor");
        DisplayLayoutEditorRow tv = model.GameLayout.Rows.Single(row => row.DisplayName == "Living room TV");
        Assert.True(desk.IsPrimary);
        Assert.Equal(3840, tv.X);

        tv.IsPrimary = true;

        // Windows puts the primary at 0,0; asking the user to do that subtraction would only make
        // them fail the rule.
        Assert.Equal(0, tv.X);
        Assert.Equal(-3840, desk.X);
        Assert.False(desk.IsPrimary);
        Assert.Empty(model.GameLayout.ValidationText);
    }

    [AvaloniaFact]
    public void AnOverlappingArrangementIsRefusedWithTheReasonTheApplyWouldGive()
    {
        using UiFixture fixture = new() { Displays = Desktop(Desk, Tv) };
        SettingsViewModel model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.SnapshotGameLayoutCommand.Execute(null);

        model.GameLayout.Rows.Single(row => row.DisplayName == "Living room TV").X = 100;

        Assert.Contains("overlap", model.GameLayout.ValidationText, StringComparison.Ordinal);
        Assert.False(model.CanSaveLayouts);
    }

    [AvaloniaFact]
    public void AMigratedRowIsRefusedUntilItIsPointedAtARealDisplay()
    {
        using UiFixture fixture = new() { Displays = Desktop(Tv) };
        // What the retired per-monitor profiles migrate into: real values, no resolvable identity.
        fixture.Saved.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        fixture.Saved.GameModeLaunch.GameLayout = new([
            new(new("", null, null, "Internal panel", 0, 0, 0), 0, 0, 1280, 720,
                DisplayRefresh.FromHertz(120))]);

        SettingsViewModel model = Model(Open(fixture));
        DisplayLayoutEditorRow row = model.GameLayout.Rows.Single(candidate => candidate.NeedsRebind);
        Assert.Contains("Confirm which display", row.RebindText, StringComparison.Ordinal);
        Assert.Contains("identified", model.GameLayout.ValidationText, StringComparison.Ordinal);
        Assert.False(model.CanSaveLayouts);

        model.RebindChoiceIndex = model.RebindChoices.IndexOf("Living room TV");
        model.RebindDisplayCommand.Execute(row);

        // The television already had its own row, because it is connected and Settings saw it on
        // open. The two merge: one monitor cannot be two rows, and both could never be applied.
        DisplayLayoutEditorRow merged = Assert.Single(model.GameLayout.Rows);
        Assert.Equal("Living room TV", merged.DisplayName);
        Assert.False(merged.NeedsRebind);
        Assert.Empty(model.GameLayout.ValidationText);
        Assert.True(model.CanSaveLayouts);
        // The values the migration preserved survive the rebind.
        Assert.Equal(1280, Assert.Single(model.GameLayout.Build()!.Outputs).Width);
    }

    [AvaloniaFact]
    public void ForgettingADisplayRemovesItFromBothLayoutsAndTheCatalog()
    {
        using UiFixture fixture = new() { Displays = Desktop(Tv) };
        SettingsViewModel model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.GameModeReturnIndex = (int)GameModeReturn.DesktopLayout;
        model.SnapshotGameLayoutCommand.Execute(null);
        model.WaitForDisplayIndex = 1;

        model.ForgetDisplayCommand.Execute(model.GameLayout.Rows[0]);

        Assert.Empty(model.GameLayout.Rows);
        Assert.Empty(model.DesktopLayout.Rows);
        Assert.Empty(model.KnownDisplays);
        // The wait target pointed at the display that just went away.
        Assert.Equal(0, model.WaitForDisplayIndex);
    }

    [AvaloniaFact]
    public void ASavedLayoutAndItsWaitTargetSurviveASave()
    {
        using UiFixture fixture = new() { Displays = Desktop(Tv) };
        SettingsWindow window = Open(fixture);
        SettingsViewModel model = Model(window);
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
    public void AnActionStepIsAddedWithItsDeclaredDefaultsAndCanBeReordered()
    {
        using UiFixture fixture = new()
        {
            PluginActions =
            [
                Option("switch-to-pc", new PluginSetting("port", "Port", PluginSettingKind.Text, new(Text: "1"))),
                Option("tv-on"),
            ],
        };
        SettingsViewModel model = Model(Open(fixture));
        PluginActionListEditor enter = model.ActionLists[0];
        Assert.True(enter.CanAdd);

        enter.ChoiceIndex = 0;
        model.AddActionStepCommand.Execute(enter);
        enter.ChoiceIndex = 1;
        model.AddActionStepCommand.Execute(enter);

        Assert.Equal(2, enter.Rows.Count);
        PluginArgumentRow argument = Assert.Single(enter.Rows[0].Arguments);
        Assert.Equal("Port", argument.Label);
        Assert.Equal("1", argument.TextValue);

        // Order is the point: the switch has to select this PC before the television is told on.
        model.MoveActionStepDownCommand.Execute(enter.Rows[0]);
        Assert.Equal(["tv-on", "switch-to-pc"], enter.Rows.Select(row => row.Step.ActionId));

        model.RemoveActionStepCommand.Execute(enter.Rows[0]);
        Assert.Equal("switch-to-pc", Assert.Single(enter.Rows).Step.ActionId);
    }

    [AvaloniaFact]
    public void AnArgumentOutsideItsDeclaredRangeBlocksTheSave()
    {
        using UiFixture fixture = new()
        {
            PluginActions =
            [
                Option("dwell", new PluginSetting("seconds", "Seconds", PluginSettingKind.Number, new(Number: 2), 1, 10)),
            ],
        };
        SettingsViewModel model = Model(Open(fixture));
        PluginActionListEditor enter = model.ActionLists[0];
        enter.ChoiceIndex = 0;
        model.AddActionStepCommand.Execute(enter);

        Assert.Single(enter.Rows[0].Arguments).TextValue = "99";

        Assert.True(enter.HasValidationError);
        Assert.False(model.CanSaveLayouts);
    }

    [AvaloniaFact]
    public void EditedStepsAreWhatGetsSaved()
    {
        using UiFixture fixture = new()
        {
            PluginActions =
            [
                Option("remote-press", new PluginSetting("remote", "Remote", PluginSettingKind.Text, new(Text: "tv"))),
            ],
        };
        SettingsWindow window = Open(fixture);
        SettingsViewModel model = Model(window);
        PluginActionListEditor enter = model.ActionLists[0];
        enter.ChoiceIndex = 0;
        model.AddActionStepCommand.Execute(enter);
        Assert.Single(enter.Rows[0].Arguments).TextValue = "hdmi-switch";
        enter.Rows[0].TimeoutSeconds = 45;

        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Save changes")));

        PluginActionStep saved = Assert.Single(fixture.Saved.GameModeLaunch.EnterActions);
        Assert.Equal("remote-press", saved.ActionId);
        Assert.Equal("hdmi-switch", saved.Arguments["remote"].Text);
        Assert.Equal(45, saved.TimeoutSeconds);
    }

    [AvaloniaFact]
    public void AStepWhosePluginIsNotRunningKeepsItsValuesAndStaysRemovable()
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

        SettingsViewModel model = Model(Open(fixture));
        PluginActionListEditor enter = model.ActionLists[0];
        PluginActionStepEditorRow row = Assert.Single(enter.Rows);

        Assert.False(row.Available);
        Assert.Empty(row.Arguments);
        Assert.Contains("remote=hdmi-switch", row.SavedArguments, StringComparison.Ordinal);
        Assert.Contains("not running", row.UnavailableText, StringComparison.Ordinal);
        Assert.False(enter.CanAdd);
        Assert.Contains("No running plugin", enter.AddHintText, StringComparison.Ordinal);

        // Removing a step you no longer want must not require starting a plugin.
        model.RemoveActionStepCommand.Execute(row);
        Assert.Empty(enter.Rows);
    }

    private static SettingsViewModel.PluginActionOption Option(string id, params PluginSetting[] arguments) =>
        new(new("wsgm.ir", "blaster"), new(id, id, arguments), $"IR / blaster: {id}");
}
