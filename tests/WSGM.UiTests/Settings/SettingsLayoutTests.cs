using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Settings;
using WindowsDeviceControl;

namespace WSGM.UiTests;

/// <summary>Settings window layout at handheld sizes.</summary>
public sealed class SettingsLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1024)]
    [InlineData(1280)]
    public void DisplaySettingsShowsReadableModesAndLabeledScaling(int width)
    {
        DisplayTargetIdentity target = new("fixture", null, null, "Internal display", 0, 0, 1);
        using UiFixture fixture = new()
        {
            Displays = new([new(target, true, true,
                new(target, 0, 0, 1920, 1200, DisplayRefresh.FromHertz(120), DpiPercent: 150, Hdr: true))],
                "fixture", DateTimeOffset.UnixEpoch),
        };
        fixture.DisplayFacts[target.DevicePath] = new([new(1920, 1200, 120), new(1280, 800, 60)], true, 225);
        var window = fixture.Settings(width, 800);
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.CopyCurrentLayoutCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Where(control => control.IsEffectivelyVisible).ToArray();
        Assert.DoesNotContain(text, control => control.Text?.Contains("DisplayMode {") == true);
        Assert.Contains(text, control => control.Text == "Scale (%)");
        VisualBaseline.Verify(window, "settings-display-custom-" + width);
    }

    [AvaloniaFact]
    public void SettingsTabsScrollToTheirFullLabels()
    {
        using UiFixture fixture = new();
        Window window = fixture.Settings(1024, 700);
        var tabs = UiFixture.Named<TabStrip>(window, "Tabs");
        for (int i = 0; i < tabs.Tabs!.Count; i++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, i));
            Dispatcher.UIThread.RunJobs();
            Button button = UiFixture.Tab(window, i);
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.Equal(tabs.Tabs[i].Label, label.Text);
            Assert.True(button.Bounds.Width >= label.Bounds.Width + 12);
            var origin = button.TranslatePoint(default, tabs)!.Value;
            Assert.InRange(origin.X, 0, tabs.Bounds.Width - button.Bounds.Width);
        }
    }
}
