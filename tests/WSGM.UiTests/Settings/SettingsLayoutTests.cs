using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    public void EverySectionFitsTheRailAndKeepsSaveReachableAtTheFloor()
    {
        using UiFixture fixture = new();
        Window window = fixture.Settings(1024, 640);
        var rail = UiFixture.Named<StackPanel>(window, "SectionRail");
        Assert.Equal(
            ["General", "Steam", "Device", "Quick access", "Performance", "Display", "Startup", "Appearance", "Plugins",
                "Diagnostics", "About"],
            rail.Children.OfType<Button>().Select(button => button.Content as string));
        var save = window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Save changes"));
        for (var i = 0; i < rail.Children.Count; i++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, i));
            Dispatcher.UIThread.RunJobs();
            var button = UiFixture.Tab(window, i);
            Assert.Contains("selected", button.Classes);
            Assert.InRange(button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value.Y,
                0, 640);
            var bottom = save.TranslatePoint(new Point(0, save.Bounds.Height), window)!.Value.Y;
            Assert.InRange(bottom, 0, 640);
        }
    }
}
