using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceWidgetSourceTests
{
    [Fact]
    public void WidgetIdentityDoesNotDependOnLabelsOrRuntimeGeneration()
    {
        Assert.Equal(DeviceWidgetSource.KeyFor("power", "left"), DeviceWidgetSource.KeyFor("power", "left"));
        Assert.NotEqual(DeviceWidgetSource.KeyFor("power", "left"), DeviceWidgetSource.KeyFor("power", "right"));
        Assert.NotEqual(DeviceWidgetSource.KeyFor("ab", "c"), DeviceWidgetSource.KeyFor("a", "bc"));
    }

    [Fact]
    public void ChoiceReadbackIsPreservedAndMissingStateDoesNotBecomeZero()
    {
        Assert.Equal("quiet", DeviceWidgetSource.Value(new() { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" }).Text);
        Assert.Null(DeviceWidgetSource.Value(null).Number);
        Assert.Equal("Unavailable", DeviceWidgetSource.Value(null).Text);
    }
}
