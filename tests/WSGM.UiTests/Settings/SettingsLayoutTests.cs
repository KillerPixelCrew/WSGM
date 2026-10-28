using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

/// <summary>Settings window layout at handheld sizes.</summary>
public sealed class SettingsLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1024)]
    [InlineData(1280)]
    public void DisplaySettingsShowsReadableModesAndLabeledScaling(int width)
    {
        using UiFixture fixture = new();
        var window = PreviewDisplaySettings.Create(fixture, width);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var text = window.GetVisualDescendants().OfType<TextBlock>().Where(control => control.IsEffectivelyVisible)
            .ToArray();
        Assert.DoesNotContain(text, control => control.Text?.Contains("DisplayMode {") == true);
        Assert.Contains(text, control => control.Text == "Scale (%)");
        Assert.Contains(text, control => control.Text == "Output bit depth");
        Assert.Contains(text, control => control.Text == "Dithering");
        Assert.Contains(text, control => control.Text == "G-SYNC enabled");
        Assert.Equal(3, model.GameLayout.Selected?.GpuControls.Count);
        Assert.Empty(model.GameLayout.GlobalGpuControls);
        // The detail card's rounded edges antialias a level or two apart between runs; anything real in
        // the card moves far more than the noise budget.
        var card = window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "ResolutionChoice")
            .GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("layout-surface"));
        VisualBaseline.Verify(window, "settings-display-custom-" + width,
            new Rect(card.TranslatePoint(default, window)!.Value, card.Bounds.Size));
    }

    [AvaloniaFact]
    public void SettingsTabsScrollToTheirFullLabels()
    {
        using UiFixture fixture = new();
        Window window = fixture.Settings(1024, 700);
        var tabs = UiFixture.Named<TabStrip>(window, "Tabs");
        for (var i = 0; i < tabs.Tabs!.Count; i++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, i));
            Dispatcher.UIThread.RunJobs();
            var button = UiFixture.Tab(window, i);
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.Equal(tabs.Tabs[i].Label, label.Text);
            Assert.True(button.Bounds.Width >= label.Bounds.Width + 12);
            var origin = button.TranslatePoint(default, tabs)!.Value;
            Assert.InRange(origin.X, 0, tabs.Bounds.Width - button.Bounds.Width);
        }
    }
}
