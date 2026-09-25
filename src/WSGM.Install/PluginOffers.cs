using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Install;

/// <summary>One bundled plugin as offered for this machine.</summary>
/// <param name="Plugin">The bundled plugin.</param>
/// <param name="Match">The hardware rule that matched, for a device plugin.</param>
/// <param name="Installed">Whether a package with this id is installed now.</param>
/// <param name="Components">The system components its declared roles require.</param>
public sealed record PluginOffer(
    BundledPlugin Plugin,
    HardwareMatch? Match,
    bool Installed,
    IReadOnlyList<SetupComponent> Components);

/// <summary>
///     What the bundle offers this machine: the device plugins whose hardware rules match, the common
///     plugins, and the device plugins that are not for this hardware. Setup and the Plugins page use
///     the same answer. Nothing here loads plugin code.
/// </summary>
public sealed record PluginOffers
{
    /// <summary>Matching device plugins, best first: exact before fallback, tested before blind.</summary>
    public required IReadOnlyList<PluginOffer> DeviceCandidates { get; init; }

    /// <summary>Common plugins, which are optional on every machine.</summary>
    public required IReadOnlyList<PluginOffer> Common { get; init; }

    /// <summary>Device plugins whose hardware rules do not match this machine.</summary>
    public required IReadOnlyList<BundledPlugin> NotForThisHardware { get; init; }

    /// <summary>The one device plugin to recommend, or null when none matched or the best is tied.</summary>
    public PluginOffer? RecommendedDevice =>
        DeviceCandidates.Count switch
        {
            0 => null,
            1 => DeviceCandidates[0],
            _ => Rank(DeviceCandidates[0]) < Rank(DeviceCandidates[1]) ? DeviceCandidates[0] : null
        };

    /// <summary>Whether several device plugins match equally well and the user must pick one.</summary>
    public bool NeedsDeviceChoice => DeviceCandidates.Count > 1 && RecommendedDevice is null;

    /// <summary>Works out the offers for one machine.</summary>
    /// <param name="bundle">What the release bundles.</param>
    /// <param name="identity">The machine's identity.</param>
    /// <param name="installedIds">Ids of the packages installed now.</param>
    /// <returns>The offers.</returns>
    public static PluginOffers Compute(
        BundleManifest bundle,
        DeviceIdentitySnapshot identity,
        IReadOnlyCollection<string> installedIds)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(installedIds);
        List<PluginOffer> candidates = [];
        List<BundledPlugin> notForThisHardware = [];
        foreach (var plugin in bundle.Plugins.Where(plugin => plugin.IsDevice))
        {
            var match = HardwareMatcher.Match(plugin.Hardware, identity);
            if (match is null)
            {
                notForThisHardware.Add(plugin);
                continue;
            }

            candidates.Add(Offer(plugin, match, installedIds));
        }

        return new PluginOffers
        {
            DeviceCandidates =
            [
                .. candidates
                    .OrderBy(Rank)
                    .ThenBy(offer => offer.Plugin.Id, StringComparer.Ordinal)
            ],
            Common =
            [
                .. bundle.Plugins.Where(plugin => !plugin.IsDevice)
                    .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(plugin => Offer(plugin, null, installedIds))
            ],
            NotForThisHardware = [.. notForThisHardware.OrderBy(plugin => plugin.Id, StringComparer.Ordinal)]
        };
    }

    private static PluginOffer Offer(BundledPlugin plugin, HardwareMatch? match, IReadOnlyCollection<string> installed)
    {
        return new PluginOffer(plugin, match, installed.Contains(plugin.Id),
            SetupComponents.Required(plugin.Capabilities));
    }

    // Lower is better: an exact rule outranks a fallback, and a tested plugin outranks a blind one
    // matched by the same kind of rule.
    private static int Rank(PluginOffer offer)
    {
        return (offer.Match?.Fallback == true ? 2 : 0) + (offer.Plugin.HardwareTested ? 0 : 1);
    }
}
