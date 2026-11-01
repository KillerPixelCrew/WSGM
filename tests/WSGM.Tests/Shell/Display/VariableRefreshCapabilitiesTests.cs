using WSGM.Shell;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

public sealed class VariableRefreshCapabilitiesTests
{
    [Fact]
    public void TheOnlyVariableRefreshCapabilityIsChosenWhereverItIs()
    {
        PublishedCapability only = new(View(Vrr("display-2"), null, null), GpuInstance.PluginId);

        Assert.Same(only, VariableRefreshCapabilities.Select([only]));
        Assert.Null(VariableRefreshCapabilities.Select([]));
    }

    [Fact]
    public void TheBuiltInPanelWinsThenTheDevicePackage()
    {
        PublishedCapability external = new(View(Vrr("display-2"), null, null), GpuInstance.PluginId);
        PublishedCapability panel = new(View(Vrr("internal-edp"), null, null), GpuInstance.PluginId);
        PublishedCapability device = new(View(Vrr(null), null, null), null);

        Assert.Same(panel, VariableRefreshCapabilities.Select([external, device, panel]));
        Assert.Same(device, VariableRefreshCapabilities.Select([external, device]));
    }

    [Fact]
    public void WithoutAPanelOrDeviceTheFirstPublisherAndInstanceWin()
    {
        PublishedCapability second = new(View(Vrr("display-2"), null, null), "wsgm.b-gpu");
        PublishedCapability first = new(View(Vrr("display-1"), null, null), "wsgm.a-gpu");

        Assert.Same(first, VariableRefreshCapabilities.Select([second, first]));
    }
}
