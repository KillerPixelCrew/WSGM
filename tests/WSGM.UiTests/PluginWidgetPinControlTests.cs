using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WSGM.Overlay;

namespace WSGM.UiTests;

public sealed class PluginWidgetPinControlTests
{
    [AvaloniaFact]
    public void OnlyExplicitPinAndUnpinClicksChangePreferences()
    {
        using UiFixture fixture = new();
        List<bool> edits = [];
        PluginWidgetPinControls view = new("Remote", pinned => { edits.Add(pinned); return Task.CompletedTask; });
        Window window = new() { Content = view, Width = 500, Height = 200 };
        try
        {
            window.Show();
            Assert.Empty(edits);
            var buttons = Assert.IsType<StackPanel>(view.Children[1]);
            UiFixture.Click(window, Assert.IsType<Button>(buttons.Children[0]));
            UiFixture.Click(window, Assert.IsType<Button>(buttons.Children[1]));
            Assert.Equal([true, false], edits);
        }
        finally { window.Close(); }
    }
}
