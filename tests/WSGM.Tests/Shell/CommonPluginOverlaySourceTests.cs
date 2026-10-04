using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class CommonPluginOverlaySourceTests
{
    [Fact]
    public async Task ReadsShareSessionSnapshotUntilConfigReloadAppliesPins()
    {
        PluginWidgetPin first = new("one", "default", "status");
        PluginWidgetPin second = new("two", "default", "status");
        using TemporaryConfigStore config = new();
        CommonPluginOverlaySource source = new(config.Store, null,
            new PluginHost(action => action(), new MemoryPluginConfigurationStore()), [first]);

        var initial = await source.WidgetPreferences.Read();
        Assert.Same(initial, await source.WidgetPreferences.Read());
        Assert.Equal([first], initial);

        source.ApplyPins([second]);

        var reloaded = await source.WidgetPreferences.Read();
        Assert.NotSame(initial, reloaded);
        Assert.Equal([second], reloaded);
    }
}
