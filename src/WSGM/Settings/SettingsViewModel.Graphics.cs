using System.Collections.ObjectModel;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Built-in graphics driver instances, independent of device and Steam integration.</summary>
    public ObservableCollection<CommonPluginInstanceRow> GraphicsDrivers { get; } = [];

    private void LoadGraphicsDrivers()
    {
        foreach (var driver in BuiltinGpuDrivers.Detect())
        {
            GraphicsDrivers.Add(new CommonPluginInstanceRow(driver.Id, CommonPluginEnablement.DefaultInstanceId,
                driver.Name, BuiltinGpuDrivers.Enabled(_config, driver.Vendor), true));
        }
    }
}
