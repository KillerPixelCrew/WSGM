using WSGM.Core;
using WSGM.Settings;

namespace WSGM.Tests.Settings;

public sealed class RetiredPluginPackagesTests
{
    [Fact]
    public void PackagesRetiredIntoTheLibrariesAreNeverListedEvenFromAnOlderBundleRecord()
    {
        PluginPackageRowState[] rows =
        [
            Row("wsgm.gpu.intel"), Row("wsgm.gpu.nvidia"), Row("WSGM.GPU.AMD"), Row("wsgm.device.msi.claw"),
            Row("wsgm.ir")
        ];

        var shown = SettingsViewModel.WithoutRetired(rows);

        Assert.Equal(["wsgm.ir"], shown.Select(static row => row.Id));
    }

    private static PluginPackageRowState Row(string id)
    {
        return new PluginPackageRowState(id, id, PluginPackageSection.Available, [], "", PluginPackageAction.Install, "");
    }
}
