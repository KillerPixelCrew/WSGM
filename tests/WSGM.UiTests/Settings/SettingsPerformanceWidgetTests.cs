using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

public sealed class SettingsPerformanceWidgetTests
{
    [AvaloniaFact]
    public void AGripPicksUpItsWidgetArrowsMoveItAndBackPutsItDown()
    {
        using UiFixture fixture = new();
        fixture.Saved.Performance.Enabled = true;
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        UiFixture.Click(window, UiFixture.Tab(window, 4));
        var grip = Grip(window, "Clock");
        grip.Focus(NavigationMethod.Directional);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Move", UiFixture.Named<TextBlock>(window, "TertiaryHint").Text);

        UiFixture.Key(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.OsdWidgets[0].IsMoving);
        UiFixture.Key(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        UiFixture.Key(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, "settings-performance-moving-1024");
        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.Equal(["GPU", "CPU", "Time"], model.OsdWidgets.Take(3).Select(static row => row.Key));
        Assert.DoesNotContain(model.OsdWidgets, static row => row.IsMoving);
        Assert.Same(Grip(window, "Clock"), window.FocusManager!.GetFocusedElement());
        Assert.Equal("1 unsaved change", model.UnsavedChangesText);
    }

    [AvaloniaFact]
    public void DraggingAGripMovesItsWidgetWhereThePointerIs()
    {
        using UiFixture fixture = new();
        fixture.Saved.Performance.Enabled = true;
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        UiFixture.Click(window, UiFixture.Tab(window, 4));
        var grip = Grip(window, "Battery");
        grip.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var from = Centre(window, grip);
        var to = Centre(window, Grip(window, "Clock"));

        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("BATT", model.OsdWidgets[0].Key);
        Assert.DoesNotContain(model.OsdWidgets, static row => row.IsMoving);
    }

    private static Button Grip(Window window, string name)
    {
        return UiFixture.Named<Control>(window, "PagePerformance").GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("settings-grip")
                              && button.DataContext is OsdWidgetRow row && row.Name == name);
    }

    private static Point Centre(Window window, Control control)
    {
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    }
}
