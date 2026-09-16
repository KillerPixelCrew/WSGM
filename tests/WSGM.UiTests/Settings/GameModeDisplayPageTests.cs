using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class GameModeDisplayPageTests
{
    private static readonly DisplayTargetIdentity Tv =
        new(@"\\?\DISPLAY#TV0001", null, null, "Living room TV", 0, 0, 3);

    private static readonly DisplayTargetIdentity Desk =
        new(@"\\?\DISPLAY#DESK01", null, null, "Desk monitor", 0, 0, 4);

    [AvaloniaFact]
    public void WindowsDisabledDisplayOffersModesAndKeepsTheSelectedResolutionAndRefresh()
    {
        using UiFixture fixture = new();
        fixture.Displays = new DisplayArrangement([new DisplayTargetObservation(Tv, true, false, null)], "disabled",
            DateTimeOffset.UnixEpoch);
        fixture.DisplayFacts[Tv.DevicePath] = new DisplayCatalogFacts(
            [new DisplayMode(3840, 2160, 120), new DisplayMode(1920, 1080, 120), new DisplayMode(1920, 1080, 60)], true,
            225);
        var window = Open(fixture);
        var model = Model(window);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(model.GameLayout.Rows);
        Assert.False(row.Active);
        Assert.True(row.HasModes);
        var resolution = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => control.Name == "ResolutionChoice");
        UiFixture.Click(window, resolution);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(new DisplayResolution(1920, 1080), row.Resolution);
        var refresh = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => control.Name == "RefreshChoice");
        UiFixture.Click(window, refresh);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(60, row.RefreshHz);
        model.RefreshDisplaysCommand.Execute(null);
        Assert.Equal(new DisplayMode(1920, 1080, 60), row.Mode);
        Assert.False(row.Active);
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CheckBox>()
            .Single(control => Equals(control.Content, "Use in this layout")));
        Assert.Equal(new DisplayMode(1920, 1080, 60), row.Mode);
        Assert.True(model.CanSaveLayouts);
    }

    [AvaloniaFact]
    public void FreshCustomLayoutCanBeEditedWithoutCopyingAndDisabledDisplaysKeepTheirInspector()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Tv);
        fixture.DisplayFacts[Tv.DevicePath] =
            new DisplayCatalogFacts([new DisplayMode(3840, 2160, 120), new DisplayMode(1920, 1080, 60)], true, 225);
        var window = Open(fixture);
        var model = Model(window);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(model.GameLayout.Rows);
        Assert.True(row.Active);
        Assert.Equal(100, row.DpiPercent);
        var resolution = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => control.Name == "ResolutionChoice");
        Assert.True(resolution.IsEffectivelyEnabled);
        Assert.Equal(2, resolution.ItemCount);
        var enabled = window.GetVisualDescendants().OfType<CheckBox>()
            .Single(control => Equals(control.Content, "Use in this layout"));
        UiFixture.Click(window, enabled);
        Assert.False(row.Active);
        Assert.True(resolution.IsEffectivelyVisible);
        Assert.True(resolution.IsEffectivelyEnabled);
        Assert.False(model.CanSaveLayouts);
        Assert.Equal(new DisplayResolution(3840, 2160), resolution.SelectedItem);
        UiFixture.Click(window, resolution);
        Assert.True(resolution.IsDropDownOpen);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(new DisplayResolution(1920, 1080), row.Resolution);
        Assert.Equal(60, row.RefreshHz);
        UiFixture.Click(window, enabled);
        Assert.Equal(new DisplayMode(1920, 1080, 60), Assert.Single(model.GameLayout.Rows).Mode);
        Assert.True(model.CanSaveLayouts);
    }

    [AvaloniaFact]
    public void RefreshKeepsBothDraftsSelectionAndUndoIncludingInvalidEdits()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Desk, Tv);
        var model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.GameModeReturnIndex = (int)GameModeReturn.DesktopLayout;
        var tv = model.GameLayout.Rows[1];
        model.GameLayout.Selected = tv;
        tv.X = 100;
        model.DesktopLayout.Rows[0].DpiPercent = 175;
        fixture.Displays = Desktop(Desk);
        model.RefreshDisplaysCommand.Execute(null);
        Assert.Same(tv, model.GameLayout.Selected);
        Assert.False(tv.Present);
        Assert.Equal(100, tv.X);
        Assert.True(model.GameLayout.HasValidationError);
        Assert.Equal(175, model.DesktopLayout.Rows[0].DpiPercent);
        model.GameLayout.Undo();
        Assert.Equal(3840, tv.X);
        Assert.False(model.GameLayout.HasValidationError);
        fixture.ReadDisplays = () => throw new InvalidOperationException("fixture discovery failure");
        model.RefreshDisplaysCommand.Execute(null);
        Assert.Equal(2, model.GameLayout.Rows.Count);
        Assert.Contains("drafts are kept", model.DisplayDiscoveryText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CopyAndUndoAffectOnlyTheSelectedDesktopDraft()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Desk, Tv);
        var model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.GameModeReturnIndex = (int)GameModeReturn.DesktopLayout;
        model.DesktopLayout.Rows[1].Active = false;
        model.EditingDesktopLayout = true;
        model.CopyCurrentLayoutCommand.Execute(null);
        Assert.True(model.DesktopLayout.Rows[1].Active);
        Assert.Equal(100, model.GameLayout.Rows[0].DpiPercent);
        model.DesktopLayout.Undo();
        Assert.False(model.DesktopLayout.Rows[1].Active);
        Assert.Equal(150, model.DesktopLayout.Rows[1].DpiPercent);
    }

    [AvaloniaFact]
    public void DraggingAnArrangementScreenRecordsOneUndoStep()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Desk, Tv);
        var window = Open(fixture);
        var model = Model(window);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        Dispatcher.UIThread.RunJobs();
        var tv = model.GameLayout.Rows[1];
        var screen = window.GetVisualDescendants().OfType<Button>().Single(button =>
            button.Classes.Contains("display-monitor") && ReferenceEquals(button.Tag, tv));
        screen.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var start = screen.TranslatePoint(new Point(screen.Bounds.Width / 2, screen.Bounds.Height / 2), window)!.Value;
        var end = start + new Vector(0, 45);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Assert.NotEqual(0, tv.Y);
        Assert.Same(tv, model.GameLayout.Selected);
        model.GameLayout.Undo();
        Assert.Equal(0, tv.Y);
        Assert.Equal(3840, tv.X);
    }

    private static DisplayLayoutOutput Output(DisplayTargetIdentity target, int x = 0)
    {
        return new DisplayLayoutOutput(target, x, 0, 3840, 2160, DisplayRefresh.FromHertz(120), DpiPercent: 150,
            Hdr: true);
    }

    private static DisplayArrangement Desktop(params DisplayTargetIdentity[] targets)
    {
        return new DisplayArrangement(
            [
                .. targets.Select((target, index) =>
                    new DisplayTargetObservation(target, true, true, Output(target, index * 3840)))
            ],
            "desktop", DateTimeOffset.UnixEpoch);
    }

    private static SettingsWindow Open(UiFixture fixture)
    {
        var window = fixture.Settings();
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        return window;
    }

    private static SettingsViewModel Model(SettingsWindow window)
    {
        return Assert.IsType<SettingsViewModel>(window.DataContext);
    }

    [AvaloniaFact]
    public void TheCustomLayoutFieldsAppearOnlyForACustomLaunch()
    {
        using UiFixture fixture = new();
        var model = Model(Open(fixture));

        Assert.False(model.ShowCustomLaunch);
        Assert.Contains("primary", model.LaunchSummaryText, StringComparison.Ordinal);

        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;

        Assert.True(model.ShowCustomLaunch);
    }

    [AvaloniaFact]
    public void CopyCapturesTheDesktopAndRemembersWhatEachDisplaySupports()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Tv);
        fixture.DisplayFacts[Tv.DevicePath] =
            new DisplayCatalogFacts([new DisplayMode(3840, 2160, 120), new DisplayMode(1920, 1080, 60)], true, 225);
        var model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;

        model.CopyCurrentLayoutCommand.Execute(null);

        var row = Assert.Single(model.GameLayout.Rows);
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
            new KnownDisplay
            {
                Target = Tv, Modes = [new DisplayMode(3840, 2160, 120)], HdrSupported = true, MaximumDpiPercent = 225
            }
        ];

        var model = Model(Open(fixture));
        var row = Assert.Single(model.GameLayout.Rows);
        Assert.False(row.Present);
        Assert.Equal("Not connected right now", row.PresenceText);

        row.Active = true;
        row.Mode = row.Modes[0];

        Assert.True(row.IsPrimary);
        Assert.Empty(model.GameLayout.ValidationText);
        var built = Assert.IsType<DisplayLayout>(model.GameLayout.Build());
        Assert.Equal(3840, Assert.Single(built.Outputs).Width);
    }

    [AvaloniaFact]
    public void ChoosingAPrimaryMovesTheWholeArrangementSoItSitsAtTheOrigin()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Desk, Tv);
        var model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.CopyCurrentLayoutCommand.Execute(null);

        var desk = model.GameLayout.Rows.Single(row => row.DisplayName == "Desk monitor");
        var tv = model.GameLayout.Rows.Single(row => row.DisplayName == "Living room TV");
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
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Desk, Tv);
        var model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.CopyCurrentLayoutCommand.Execute(null);

        model.GameLayout.Rows.Single(row => row.DisplayName == "Living room TV").X = 100;

        Assert.Contains("overlap", model.GameLayout.ValidationText, StringComparison.Ordinal);
        Assert.False(model.CanSaveLayouts);
    }

    [AvaloniaFact]
    public void AMigratedRowIsRefusedUntilItIsPointedAtARealDisplay()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Tv);
        // What the retired per-monitor profiles migrate into: real values, no resolvable identity.
        fixture.Saved.GameModeLaunch.Kind = GameModeLaunchKind.Custom;
        fixture.Saved.GameModeLaunch.GameLayout = new DisplayLayout([
            new DisplayLayoutOutput(new DisplayTargetIdentity("", null, null, "Internal panel", 0, 0, 0), 0, 0, 1280,
                720,
                DisplayRefresh.FromHertz(120))
        ]);

        var model = Model(Open(fixture));
        var row = model.GameLayout.Rows.Single(candidate => candidate.NeedsRebind);
        Assert.Contains("Confirm which display", row.RebindText, StringComparison.Ordinal);
        Assert.Contains("identified", model.GameLayout.ValidationText, StringComparison.Ordinal);
        Assert.False(model.CanSaveLayouts);

        model.RebindChoiceIndex = model.RebindChoices.IndexOf("Living room TV");
        model.RebindDisplayCommand.Execute(row);

        // The television already had its own row, because it is connected and Settings saw it on
        // open. The two merge: one monitor cannot be two rows, and both could never be applied.
        var merged = Assert.Single(model.GameLayout.Rows);
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
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Tv);
        var model = Model(Open(fixture));
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.GameModeReturnIndex = (int)GameModeReturn.DesktopLayout;
        model.CopyCurrentLayoutCommand.Execute(null);
        model.WaitForDisplayIndex = 1;

        model.ForgetDisplayCommand.Execute(model.GameLayout.Rows[0]);

        Assert.Empty(model.GameLayout.Rows);
        Assert.Empty(model.DesktopLayout.Rows);
        Assert.Empty(model.KnownDisplays);
        // The wait target pointed at the display that just went away.
        Assert.Equal(0, model.WaitForDisplayIndex);
    }

    [AvaloniaFact]
    public void SavingForgetPreservesASeparateDisplayDiscoveredByTheRunningSession()
    {
        using UiFixture fixture = new();
        fixture.Saved.GameModeLaunch.KnownDisplays =
            [new KnownDisplay { Target = Tv, Modes = [new DisplayMode(3840, 2160, 120)] }];
        var window = Open(fixture);
        var model = Model(window);
        model.ForgetDisplayCommand.Execute(model.GameLayout.Rows[0]);
        fixture.Saved.GameModeLaunch.KnownDisplays.Add(new KnownDisplay
            { Target = Desk, Modes = [new DisplayMode(1920, 1080, 60)] });
        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Save changes")));
        Assert.Equal("Desk monitor", Assert.Single(fixture.Saved.GameModeLaunch.KnownDisplays).Target!.FriendlyName);
    }

    [AvaloniaFact]
    public void ASavedLayoutAndItsWaitTargetSurviveASave()
    {
        using UiFixture fixture = new();
        fixture.Displays = Desktop(Tv);
        var window = Open(fixture);
        var model = Model(window);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.CopyCurrentLayoutCommand.Execute(null);
        model.WaitForDisplayIndex = 1;

        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Save changes")));

        var saved = fixture.Saved.GameModeLaunch;
        Assert.Equal(GameModeLaunchKind.Custom, saved.Kind);
        Assert.Equal(3840, Assert.Single(saved.GameLayout!.Outputs).Width);
        Assert.Equal("Living room TV", saved.WaitForDisplay!.FriendlyName);
        Assert.Equal("Living room TV", Assert.Single(saved.KnownDisplays).Target!.FriendlyName);
    }

    [AvaloniaFact]
    public void AnActionStepIsAddedWithItsDeclaredDefaultsAndCanBeReordered()
    {
        SettingsViewModel.PluginActionOption[] actions =
        [
            Option("switch-to-pc",
                new PluginSetting("port", "Port", PluginSettingKind.Text, new PluginValue(Text: "1"))),
            Option("tv-on")
        ];
        using UiFixture fixture = new() { PluginActions = actions };
        var model = Model(Open(fixture));
        var enter = model.ActionLists[0];
        Assert.True(enter.CanAdd);

        enter.ChoiceIndex = 0;
        model.AddActionStepCommand.Execute(enter);
        enter.ChoiceIndex = 1;
        model.AddActionStepCommand.Execute(enter);

        Assert.Equal(2, enter.Rows.Count);
        var argument = Assert.Single(enter.Rows[0].Arguments);
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
        SettingsViewModel.PluginActionOption[] actions =
        [
            Option("dwell",
                new PluginSetting("seconds", "Seconds", PluginSettingKind.Number, new PluginValue(Number: 2), 1, 10))
        ];
        using UiFixture fixture = new() { PluginActions = actions };
        var model = Model(Open(fixture));
        var enter = model.ActionLists[0];
        enter.ChoiceIndex = 0;
        model.AddActionStepCommand.Execute(enter);

        Assert.Single(enter.Rows[0].Arguments).TextValue = "99";

        Assert.True(enter.HasValidationError);
        Assert.False(model.CanSaveLayouts);
    }

    [AvaloniaFact]
    public void EditedStepsAreWhatGetsSaved()
    {
        SettingsViewModel.PluginActionOption[] actions =
        [
            Option("remote-press",
                new PluginSetting("remote", "Remote", PluginSettingKind.Text, new PluginValue(Text: "tv")))
        ];
        using UiFixture fixture = new() { PluginActions = actions };
        var window = Open(fixture);
        var model = Model(window);
        var enter = model.ActionLists[0];
        enter.ChoiceIndex = 0;
        model.AddActionStepCommand.Execute(enter);
        Assert.Single(enter.Rows[0].Arguments).TextValue = "hdmi-switch";
        enter.Rows[0].TimeoutSeconds = 45;

        UiFixture.Click(window, window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Save changes")));

        var saved = Assert.Single(fixture.Saved.GameModeLaunch.EnterActions);
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
            new PluginActionStep
            {
                Plugin = new PluginInstanceIdentity("wsgm.ir", "blaster"),
                ActionId = "remote-run",
                Arguments = { ["remote"] = new PluginValue(Text: "hdmi-switch") },
                TimeoutSeconds = 45
            }
        ];

        var model = Model(Open(fixture));
        var enter = model.ActionLists[0];
        var row = Assert.Single(enter.Rows);

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

    private static SettingsViewModel.PluginActionOption Option(string id, params PluginSetting[] arguments)
    {
        return new SettingsViewModel.PluginActionOption(new PluginInstanceIdentity("wsgm.ir", "blaster"),
            new PluginAction(id, id, arguments),
            $"IR / blaster: {id}");
    }
}
