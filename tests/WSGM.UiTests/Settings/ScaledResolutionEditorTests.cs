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
    public void DisplayEditorCanRemoveDriverEntriesWithoutRememberedOwnership(int width, bool intel)
    {
        using var fixture = new UiFixture();
        var window = PreviewScaledResolutions.Create(fixture, width, intel);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var editor = model.ScaledResolutions;
        Assert.DoesNotContain("PCI\\FIXTURE", editor.AdapterText);
        Assert.Contains(editor.Resolutions, mode => mode.Width == 3648 && mode.Height == 2052);
        Assert.Contains(editor.Resolutions, mode => mode.Width == 1920 && mode.Height == 1080);
        Assert.DoesNotContain(editor.Resolutions, mode => mode.Width == 800 && mode.Height == 600);
        Assert.False(editor.CanRemove);
        Assert.False(editor.CanRestore);
        editor.Resolution = editor.Resolutions.Single(mode => mode.Width == 1760 && mode.Height == 1100);
        Assert.True(editor.CanRemove);
        Assert.True(window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == "Remove").IsEffectivelyEnabled);
        Assert.True(editor.CanAddSteps);
        Assert.False(editor.CanAdd);
        editor.Size = "1760 x 1100";
        Assert.True(editor.CanAdd);
        editor.Size = "";
        Assert.Equal(!intel, editor.NeedsRestart);
        if (intel)
        {
            Assert.Empty(editor.ActivationNotice);
        }
        else
        {
            Assert.Contains("Restart Windows", editor.ActivationNotice);
            Assert.True(Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == editor.ActivationNotice).IsEffectivelyVisible);
        }
        Assert.True(window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == "Edit").IsEffectivelyEnabled);
        editor.Resolution = editor.Resolutions.First();
        VisualBaseline.Verify(window, "settings-scaled-resolutions-" + width);
        window.Close();
    }
}
