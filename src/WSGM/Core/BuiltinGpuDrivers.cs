using System;
using System.Collections.Generic;
using System.Linq;
using LibGPUDriverInteract;

namespace WSGM.Core;

/// <summary>A linked graphics backend and its stable profile identity.</summary>
internal sealed record BuiltinGpuDriver(GpuVendor Vendor, string Id, string Name);

/// <summary>The three process-wide driver owners and their explicit configuration.</summary>
internal static class BuiltinGpuDrivers
{
    internal static IReadOnlyList<BuiltinGpuDriver> All { get; } =
    [
        new(GpuVendor.Intel, "wsgm.gpu.intel", "Intel graphics"),
        new(GpuVendor.Amd, "wsgm.gpu.amd", "AMD graphics"),
        new(GpuVendor.Nvidia, "wsgm.gpu.nvidia", "NVIDIA graphics")
    ];

    internal static bool Contains(string id)
    {
        return All.Any(driver => driver.Id == id);
    }

    internal static IReadOnlyList<BuiltinGpuDriver> Detect()
    {
        var vendors = GpuDriver.DetectVendors();
        return All.Where(driver => vendors.Contains(driver.Vendor)).ToArray();
    }

    internal static void Normalize(AppConfig config)
    {
        config.GpuDrivers ??= new GpuDriverConfig
        {
            Intel = config.PluginInstances.Any(instance => instance.PluginId == "wsgm.gpu.intel" && instance.Enabled),
            Amd = config.PluginInstances.Any(instance => instance.PluginId == "wsgm.gpu.amd" && instance.Enabled),
            Nvidia = config.PluginInstances.Any(instance => instance.PluginId == "wsgm.gpu.nvidia" && instance.Enabled)
        };
        config.PluginInstances.RemoveAll(instance => Contains(instance.PluginId));
    }

    internal static bool Enabled(AppConfig config, GpuVendor vendor)
    {
        if (config.GpuDrivers is null)
        {
            var id = All.First(driver => driver.Vendor == vendor).Id;
            return config.PluginInstances.Any(instance => instance.PluginId == id && instance.Enabled);
        }

        return vendor switch
        {
            GpuVendor.Intel => config.GpuDrivers.Intel,
            GpuVendor.Amd => config.GpuDrivers.Amd,
            GpuVendor.Nvidia => config.GpuDrivers.Nvidia,
            _ => throw new ArgumentOutOfRangeException(nameof(vendor))
        };
    }

    internal static void SetEnabled(AppConfig config, string id, bool enabled)
    {
        Normalize(config);
        switch (All.First(driver => driver.Id == id).Vendor)
        {
            case GpuVendor.Intel: config.GpuDrivers!.Intel = enabled; break;
            case GpuVendor.Amd: config.GpuDrivers!.Amd = enabled; break;
            case GpuVendor.Nvidia: config.GpuDrivers!.Nvidia = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(id));
        }
    }
}
