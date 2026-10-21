using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

public sealed class ScaledResolutionEditorTests
{
    [AvaloniaTheory]
    [InlineData(1024, true)]
    [InlineData(1280, false)]
    public void DisplayEditorShowsDrivingAdapterPreviewAndExplicitActivation(int width, bool intel)
    {
        using var fixture = new UiFixture();
        var window = PreviewScaledResolutions.Create(fixture, width, intel);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var editor = model.ScaledResolutions;
        Assert.Contains("PCI\\FIXTURE", editor.AdapterText);
        Assert.Contains("95%: 3648 × 2052", editor.PreviewText);
        Assert.Contains("50%: 1920 × 1080", editor.PreviewText);
        Assert.Equal(!intel, editor.NeedsRestart);
        Assert.True(window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == "Edit width").IsEffectivelyEnabled);
        VisualBaseline.Verify(window, "settings-scaled-resolutions-" + width);
        window.Close();
    }
}
