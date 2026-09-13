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
            UiFixture.Click(window, view);
            Assert.True(view.IsPinned);
            UiFixture.Click(window, view);
            Assert.False(view.IsPinned);
            Assert.Equal([true, false], edits);
        }
        finally { window.Close(); }
    }
}
