using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WSGM.Overlay;

namespace WSGM.UiTests;

public sealed class ManualTdpModeTests
{
    [AvaloniaFact]
    public void InitialUnifiedReadbackDoesNotSaveASyntheticSelection()
    {
        using UiFixture fixture = new();
        int writes = 0;
        ManualTdpModeView view = new(() => (true, true), _ => { writes++; return Task.CompletedTask; });
        Window window = new() { Content = view, Width = 500, Height = 200 };
        try
        {
            window.Show();
            var choice = Assert.IsType<ComboBox>(view.Children[0]);
            Assert.Equal(1, choice.SelectedIndex);
            Assert.True(choice.IsEnabled);
            Assert.Equal(0, writes);
        }
        finally { window.Close(); }
    }
}
