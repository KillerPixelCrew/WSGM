using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;

namespace WSGM.UiTests;

public sealed class CommonPluginEditorTests
{
    [AvaloniaFact]
    public void ActionTextDraftUsesControllerKeyboardAndChangesOnlyOnAcceptance()
    {
        using UiFixture fixture = new();
        var previous = KeyboardService.Handler;
        Action<string>? accept = null;
        KeyboardService.Handler = (prompt, initial, maximum, callback) =>
        {
            Assert.Equal("Command name", prompt);
            Assert.Equal("Power", initial);
            Assert.Equal(4096, maximum);
            accept = callback;
            return true;
        };
        Window window = new() { Width = 500, Height = 200 };
        try
        {
            var (editor, read) = CommonPluginPanel.CreateTextArgumentEditor(
                new("name", "Command name", PluginSettingKind.Text, new(Text: "Power")));
            window.Content = editor;
            window.Show();
            UiFixture.Click(window, editor);
            Assert.NotNull(accept);
            Assert.Equal("Power", read().Text);
            accept("HDMI 1");
            Assert.Equal("HDMI 1", read().Text);
            Assert.Equal("HDMI 1", editor.Content);
        }
        finally { window.Close(); KeyboardService.Handler = previous; }
    }
}
