using System;
using System.Collections.Generic;
using System.Linq;
using LibHandheld.Contracts;

namespace WSGM.Install;

/// <summary>One bundled common or graphics plugin as offered for this machine.</summary>
/// <param name="Plugin">The bundled plugin.</param>
/// <param name="Installed">Whether a package with this id is installed now.</param>
/// <param name="Components">System components required by the offer; common plugins require none.</param>
/// <param name="HardwareTested">Whether the maintainer recorded hardware validation for the plugin.</param>
public sealed record PluginOffer(
    BundledPlugin Plugin,
    bool Installed,
    IReadOnlyList<SetupComponent> Components,
    bool HardwareTested);

/// <summary>Pure built-in handheld detection and common/graphics bundle offers shared by setup and WSGM.</summary>
public sealed record PluginOffers
{
    /// <summary>Exact built-in handheld support; requires no installed device package.</summary>
    public HandheldOffer? Handheld { get; init; }

    /// <summary>Graphics packages for the machine's present display adapters.</summary>
    public required IReadOnlyList<PluginOffer> Gpu { get; init; }

    /// <summary>Common plugins optional on every machine.</summary>
    public required IReadOnlyList<PluginOffer> Common { get; init; }

    /// <summary>Graphics packages for display adapters this machine does not have.</summary>
    public IReadOnlyList<BundledPlugin> GpuNotForThisHardware { get; init; } = [];

    /// <summary>The identity snapshot used to compute these offers.</summary>
    public DeviceIdentitySnapshot? Identity { get; init; }

    /// <summary>Works out the offers without loading plugin code or opening hardware.</summary>
    /// <param name="bundle">What the release bundles.</param>
    /// <param name="identity">The machine's identity.</param>
    /// <param name="adapters">The machine's present display adapters.</param>
    /// <param name="installedIds">Ids of packages installed now.</param>
    /// <returns>The offers.</returns>
    public static PluginOffers Compute(BundleManifest bundle, DeviceIdentitySnapshot identity,
        IReadOnlyList<DisplayAdapterIdentity> adapters, IReadOnlyCollection<string> installedIds)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(installedIds);
        var gpu = bundle.Plugins.Where(plugin => plugin.IsGpu).ToArray();
        return new PluginOffers
        {
            Handheld = HandheldSupport.Detect(identity),
            Gpu =
            [
                .. gpu.Where(plugin => plugin.MatchesAdapters(adapters))
                    .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(plugin => Offer(plugin, installedIds))
            ],
            Common =
            [
                .. bundle.Plugins.Where(plugin => !plugin.IsGpu && plugin.Category != "wsgm.device")
                    .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(plugin => Offer(plugin, installedIds))
            ],
            GpuNotForThisHardware =
            [
                .. gpu.Where(plugin => !plugin.MatchesAdapters(adapters))
                    .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
            ],
            Identity = identity
        };
    }

    private static PluginOffer Offer(BundledPlugin plugin, IReadOnlyCollection<string> installed)
    {
        return new PluginOffer(plugin, installed.Contains(plugin.Id), [], plugin.HardwareTested);
    }
}
