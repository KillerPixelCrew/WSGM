using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

public sealed class SettingsStartupAppTests
{
    private const string Discord = @"C:\Users\Test\AppData\Local\Discord\Update.exe";
    private const string Lossless = @"C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling\LosslessScaling.exe";
    private const string OldTool = @"D:\Tools\OldTool\OldTool.exe";

    [AvaloniaFact]
    public void CardsSayWhatEachAppRunsAndWhyOneIsSkipped()
    {
        using var fixture = Fixture();
        var window = fixture.Settings(1024, 640);
        UiFixture.Click(window, UiFixture.Tab(window, 6));

        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        Assert.Equal([1, 2, 3], model.StartupApps.Select(static app => app.Number));
        Assert.Equal("File not found: " + OldTool, model.StartupApps[2].Detail);
        Assert.Equal(Discord + "  ·  --processStart Discord.exe", model.StartupApps[1].Detail);
        VisualBaseline.Verify(window, "settings-startup-apps-1024");
    }

    [AvaloniaFact]
    public void MoreShowsTheActionsInTheCardAndEditOpensTheEditorThatBackLeaves()
    {
        using var fixture = Fixture();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        var discord = model.StartupApps[1];

        UiFixture.Click(window, Button(window, discord, "more"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(discord.ShowActions);
        Assert.Same(Button(window, discord, "edit"), window.FocusManager!.GetFocusedElement());
        Assert.True(UiFixture.Named<Control>(window, "SecondaryHint").IsVisible);

        UiFixture.Click(window, Button(window, discord, "down"));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(discord, model.StartupApps[2]);
        Assert.Equal(3, discord.Number);
        Assert.Same(Button(window, discord, "down"), window.FocusManager!.GetFocusedElement());

        UiFixture.Click(window, Button(window, discord, "edit"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, "PageStartupApp").IsVisible);
        Assert.Equal("Startup  ›  Edit app", UiFixture.Named<TextBlock>(window, "SectionTitle").Text);
        Assert.Same(discord, model.EditedStartupApp);
        UiFixture.Named<TextBox>(UiFixture.Named<Control>(window, "PageStartupApp"), "ArgsBox").Text = "--quiet";
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, "settings-startup-edit-1024");

        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, "PageStartup").IsVisible);
        Assert.Equal("--quiet", discord.Args);
        Assert.False(discord.ShowActions);
        Assert.Same(Button(window, discord, "more"), window.FocusManager!.GetFocusedElement());
        // The startup list is one setting: moving an app and editing its arguments is one change to it.
        Assert.Equal("1 unsaved change", model.UnsavedChangesText);
    }

    [AvaloniaFact]
    public void RemoveAsksFirstAndBackClosesTheActions()
    {
        using var fixture = Fixture();
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        var tool = model.StartupApps[2];

        UiFixture.Click(window, Button(window, tool, "more"));
        UiFixture.Click(window, Button(window, tool, "remove"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(tool.ConfirmRemove);
        Assert.Same(Button(window, tool, "keep"), window.FocusManager!.GetFocusedElement());
        UiFixture.Click(window, Button(window, tool, "keep"));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(tool, model.StartupApps);

        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.False(tool.ShowActions);
        Assert.Same(Button(window, tool, "more"), window.FocusManager!.GetFocusedElement());

        UiFixture.Click(window, Button(window, tool, "more"));
        UiFixture.Click(window, Button(window, tool, "remove"));
        UiFixture.Click(window, Button(window, tool, "confirm"));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(tool, model.StartupApps);
        Assert.Equal([1, 2], model.StartupApps.Select(static app => app.Number));
    }

    [AvaloniaFact]
    public void AddingAFoundAppPutsItLastAndOpensItsEditor()
    {
        using var fixture = Fixture();
        fixture.StartupSuggestions.Add(("Lossless Scaling", Lossless, true));
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        UiFixture.Click(window, UiFixture.Tab(window, 6));

        UiFixture.Click(window, UiFixture.Named<Button>(UiFixture.Named<Control>(window, "PageStartup"), "AddApp"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, "PageStartupAdd").IsVisible);
        VisualBaseline.Verify(window, "settings-startup-add-1024");
        UiFixture.Click(window, UiFixture.Named<Control>(window, "PageStartupAdd").GetVisualDescendants()
            .OfType<Button>().First(static button => Equals(button.Content, "Add")));
        Dispatcher.UIThread.RunJobs();

        var added = model.StartupApps[^1];
        Assert.Equal(Lossless, added.Path);
        Assert.True(added.Elevated);
        Assert.Same(added, model.EditedStartupApp);
        Assert.True(UiFixture.Named<Control>(window, "PageStartupApp").IsVisible);

        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(window, "PageStartup").IsVisible);
        Assert.Same(Button(window, added, "more"), window.FocusManager!.GetFocusedElement());
    }

    private static UiFixture Fixture()
    {
        UiFixture fixture = new();
        fixture.Saved.StartupApps =
        [
            new StartupAppConfig { Path = Lossless, Enabled = true, Elevated = true },
            new StartupAppConfig { Name = "Discord", Path = Discord, Args = "--processStart Discord.exe", Enabled = true, AutoRelaunch = true },
            new StartupAppConfig { Path = OldTool, Enabled = true }
        ];
        fixture.ProgramExists = static path => path != OldTool;
        return fixture;
    }

    private static Button Button(Window window, StartupAppRow row, string tag)
    {
        return UiFixture.Named<Control>(window, "PageStartup").GetVisualDescendants().OfType<Button>()
            .First(button => ReferenceEquals(button.DataContext, row)
                             && (tag == "more" ? button.Classes.Contains("settings-more") : Equals(button.Tag, tag)));
    }
}
