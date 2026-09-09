using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class PinnedPluginWidgetTests
{
    [AvaloniaFact]
    public void MissingPinnedProviderRemainsVisibleWithoutDispatching()
    {
        using UiFixture fixture = new();
        CommonPluginPanel panel = new(new MissingProvider(), new PluginWidgetPin("missing", "default", "power"));
        Window window = new() { Content = panel, Width = 500, Height = 200 };
        try
        {
            window.Show();
            Assert.True(panel.IsVisible);
            Assert.Equal("Plugin unavailable", Assert.IsType<TextBlock>(Assert.Single(panel.Children)).Text);
        }
        finally { window.Close(); }
    }

    private sealed class MissingProvider : ICommonPluginOverlaySource
    {
        public CommonPluginInstanceView[] Snapshot() => [];
        public PluginStatePublication[] State(PluginInstanceIdentity identity) => [];
        public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
            string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A missing widget cannot dispatch.");
    }
}
