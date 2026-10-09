using System.Collections.Generic;
using System.Linq;
using LibGPUDriverInteract;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>A built-in driver backend and its retained configuration/profile identity.</summary>
internal sealed record BuiltinGpuDriver(GpuVendor Vendor, string Id, string Name, string PciVendorId);

/// <summary>The directly linked graphics drivers; their old identifiers preserve saved user data.</summary>
internal static class BuiltinGpuDrivers
{
    internal static IReadOnlyList<BuiltinGpuDriver> All { get; } =
    [
        new(GpuVendor.Intel, "wsgm.gpu.intel", "Intel graphics", "8086"),
        new(GpuVendor.Amd, "wsgm.gpu.amd", "AMD graphics", "1002"),
        new(GpuVendor.Nvidia, "wsgm.gpu.nvidia", "NVIDIA graphics", "10de")
    ];

    internal static bool Contains(string id)
    {
        return All.Any(driver => driver.Id == id);
    }

    internal static IEnumerable<(BuiltinGpuDriver Driver, PluginInstanceIdentity Identity, bool Enabled)> Instances(
        IReadOnlyList<CommonPluginInstanceConfig> configured)
    {
        foreach (var driver in All)
        {
            var instances = configured.Where(instance => instance.PluginId == driver.Id).ToArray();
            if (instances.Length == 0)
            {
                yield return (driver, new PluginInstanceIdentity(driver.Id, CommonPluginEnablement.DefaultInstanceId),
                    true);
                continue;
            }

            foreach (var instance in instances)
            {
                yield return (driver, new PluginInstanceIdentity(driver.Id, instance.InstanceId), instance.Enabled);
            }
        }
    }
}
