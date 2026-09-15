using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class PluginWidgetPinTests
{
    [Fact]
    public void IdentityAndOrderSurviveSerializationWithoutAnInstalledProvider()
    {
        PluginWidgetPin first = new("ir", "living-room", "power");
        PluginWidgetPin second = new("ir", "bedroom", "power");
        List<PluginWidgetPin> pins = [];
        PluginWidgetPins.Set(pins, first, true);
        PluginWidgetPins.Set(pins, second, true);
        PluginWidgetPins.Set(pins, first, true);
        PluginWidgetPins.Move(pins, second, -1);
        var restored = PluginWidgetPins.Normalize(JsonSerializer.Deserialize<List<PluginWidgetPin>>(JsonSerializer.Serialize(pins)));
        Assert.Equal([second, first], restored);
        PluginWidgetPins.Set(restored, second, false);
        Assert.Equal(first, Assert.Single(restored));
    }

    [Fact]
    public void ResetOrderRetainsPinsAndInvalidIdentitiesAreDiscarded()
    {
        PluginWidgetPin first = new("b", "default", "status");
        PluginWidgetPin second = new("a", "default", "status");
        var pins = PluginWidgetPins.Normalize([first, second, first, new("", "default", "status")]);
        PluginWidgetPins.ResetOrder(pins);
        Assert.Equal([second, first], pins);
        PluginWidgetPins.Move(pins, second, -1);
        Assert.Equal([second, first], pins);
    }
}
