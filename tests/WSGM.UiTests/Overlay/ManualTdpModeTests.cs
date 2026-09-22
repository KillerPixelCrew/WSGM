using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class ManualTdpModeTests
{
    [AvaloniaFact]
    public void InitialUnifiedReadbackDoesNotSaveASyntheticSelection()
    {
        using UiFixture fixture = new();
        var writes = 0;
        ManualTdpModeView view = new(() => (true, true), _ =>
        {
            writes++;
            return Task.CompletedTask;
        });
        Window window = new() { Content = view, Width = 500, Height = 200 };
        try
        {
            window.Show();
            // The mode dropdown sits inside its game-override marker.
            var marker = Assert.IsType<ProfileOverrideMarker>(view.Children[0]);
            var choice = Assert.IsType<ComboBox>(marker.Row);
            Assert.Equal(1, choice.SelectedIndex);
            Assert.True(choice.IsEnabled);
            Assert.Equal(0, writes);
        }
        finally
        {
            window.Close();
        }
    }
}
