using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Core;

namespace WSGM.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Built-in graphics driver instances, independent of device and Steam integration.</summary>
    public ObservableCollection<CommonPluginInstanceRow> GraphicsDrivers { get; } = [];

    private void LoadGraphicsDrivers()
    {
        foreach (var (driver, identity, enabled) in BuiltinGpuDrivers.Instances(_config.PluginInstances))
        {
            var name = identity.InstanceId != CommonPluginEnablement.DefaultInstanceId
                       || _config.PluginInstances.Count(instance => instance.PluginId == driver.Id) > 1
                ? $"{driver.Name} ({identity.InstanceId})"
                : driver.Name;
            GraphicsDrivers.Add(new CommonPluginInstanceRow(driver.Id, identity.InstanceId, name, enabled, true));
        }
    }
}
