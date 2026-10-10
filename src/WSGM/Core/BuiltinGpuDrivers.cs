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
            Intel = LegacyEnabled(config, "wsgm.gpu.intel"),
            Amd = LegacyEnabled(config, "wsgm.gpu.amd"),
            Nvidia = LegacyEnabled(config, "wsgm.gpu.nvidia")
        };
        config.PluginInstances.RemoveAll(instance => Contains(instance.PluginId));
    }

    internal static bool Enabled(AppConfig config, GpuVendor vendor)
    {
        if (config.GpuDrivers is null)
        {
            var id = All.First(driver => driver.Vendor == vendor).Id;
            return LegacyEnabled(config, id);
        }

        return vendor switch
        {
            GpuVendor.Intel => config.GpuDrivers.Intel,
            GpuVendor.Amd => config.GpuDrivers.Amd,
            GpuVendor.Nvidia => config.GpuDrivers.Nvidia,
            _ => throw new ArgumentOutOfRangeException(nameof(vendor))
        };
    }

    private static bool LegacyEnabled(AppConfig config, string id)
    {
        // Setup left matching GPU packages unconfigured because they ran by default.
        // Preserve that default, and the explicit opt-out of a configured package.
        return config.PluginInstances.All(instance => instance.PluginId != id)
               || config.PluginInstances.Any(instance => instance.PluginId == id && instance.Enabled);
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
