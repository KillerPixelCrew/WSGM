using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class CommonPluginOverlaySourceTests
{
    [Fact]
    public async Task ReadsShareSessionSnapshotUntilConfigReloadAppliesPins()
    {
        PluginWidgetPin first = new("one", "default", "status");
        PluginWidgetPin second = new("two", "default", "status");
        CommonPluginOverlaySource source = new(null, new PluginHost(action => action()), [first]);

        var initial = await source.WidgetPreferences.Read();
        Assert.Same(initial, await source.WidgetPreferences.Read());
        Assert.Equal([first], initial);

        source.ApplyPins([second]);

        var reloaded = await source.WidgetPreferences.Read();
        Assert.NotSame(initial, reloaded);
        Assert.Equal([second], reloaded);
    }
}
