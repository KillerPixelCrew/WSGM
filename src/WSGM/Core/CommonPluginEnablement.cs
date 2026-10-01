using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Install;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Which installed common plugin instances WSGM runs.</summary>
/// <remarks>
///     An instance runs when the configuration enables it. A <c>wsgm.gpu</c> package is the one exception
///     in both directions: it runs by default on a machine with a display adapter its manifest serves, as
///     its <c>default</c> instance, until the configuration names an instance of it; and it never runs on a
///     machine without such an adapter, whatever the configuration says. Setup offers these packages by the
///     same match, so it writes no enable entry for them.
/// </remarks>
internal static class CommonPluginEnablement
{
    /// <summary>The instance id a package runs as when nothing configures another.</summary>
    internal const string DefaultInstanceId = "default";

    private static readonly Lazy<IReadOnlyList<DisplayAdapterIdentity>> Adapters =
        new(ReadAdapters);

    /// <summary>The display adapters present, read once per process.</summary>
    /// <remarks>
    ///     A read-only device enumeration, cached so a Settings page and every reconcile agree and none of
    ///     them enumerates again.
    /// </remarks>
    internal static IReadOnlyList<DisplayAdapterIdentity> PresentAdapters => Adapters.Value;

    /// <summary>Whether a package serves one of the present adapters.</summary>
    /// <param name="manifest">The package manifest.</param>
    /// <param name="adapters">The present adapters.</param>
    /// <returns>True when a declared vendor id matches a present adapter.</returns>
    internal static bool ServesMachine(PluginManifest manifest, IReadOnlyList<DisplayAdapterIdentity> adapters)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.DisplayAdapters.Count != 0
               && DisplayAdapterInventory.AnyVendor(adapters,
                   manifest.DisplayAdapters.Select(adapter => adapter.PciVendorId));
    }

    /// <summary>Whether a package's default instance runs when the configuration does not name it.</summary>
    /// <param name="manifest">The package manifest.</param>
    /// <param name="adapters">The present adapters.</param>
    /// <returns>True for a graphics package that serves this machine.</returns>
    internal static bool EnabledByDefault(PluginManifest manifest, IReadOnlyList<DisplayAdapterIdentity> adapters)
    {
        return manifest.Category == PluginCategories.Gpu && ServesMachine(manifest, adapters);
    }

    /// <summary>Whether a package's default instance runs on this machine when nothing names it.</summary>
    /// <param name="manifest">The package manifest.</param>
    /// <returns>True for a graphics package that serves a present adapter.</returns>
    /// <remarks>Reads the adapters only for a graphics package.</remarks>
    internal static bool EnabledByDefault(PluginManifest manifest)
    {
        return manifest.Category == PluginCategories.Gpu && ServesMachine(manifest, PresentAdapters);
    }

    /// <summary>The instances that should run.</summary>
    /// <param name="configured">The configured instances, enabled or not.</param>
    /// <param name="installed">The installed common packages.</param>
    /// <param name="adapters">The present adapters.</param>
    /// <returns>Every instance to run, each once.</returns>
    internal static PluginInstanceIdentity[] Desired(
        IReadOnlyList<CommonPluginInstanceConfig> configured,
        IReadOnlyList<CommonInstalledPlugin> installed,
        IReadOnlyList<DisplayAdapterIdentity> adapters)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(installed);
        List<PluginInstanceIdentity> desired = [];
        foreach (var instance in configured.Where(instance => instance.Enabled))
        {
            var package = installed.FirstOrDefault(candidate => candidate.Manifest.Id == instance.PluginId);
            if (package is { Manifest.Category: PluginCategories.Gpu } && !ServesMachine(package.Manifest, adapters))
            {
                continue;
            }

            desired.Add(new PluginInstanceIdentity(instance.PluginId, instance.InstanceId));
        }

        foreach (var package in installed)
        {
            if (EnabledByDefault(package.Manifest, adapters)
                && configured.All(instance => instance.PluginId != package.Manifest.Id))
            {
                desired.Add(new PluginInstanceIdentity(package.Manifest.Id, DefaultInstanceId));
            }
        }

        return [.. desired.Distinct()];
    }

    private static IReadOnlyList<DisplayAdapterIdentity> ReadAdapters()
    {
        try
        {
            var adapters = DisplayAdapterInventory.Collect();
            Log.Info("Display adapters: "
                     + (adapters.Count == 0
                         ? "none reported."
                         : string.Join(", ",
                             adapters.Select(adapter => $"{adapter.PciVendorId}:{adapter.PciDeviceId}")) + "."));
            return adapters;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Display adapters could not be listed: {ex.Message}");
            return [];
        }
    }
}
