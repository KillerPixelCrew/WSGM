using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WSGM.Install;

namespace WSGM.Core;

/// <summary>
///     The Plugins page's view of the installed packages and of what the installed release bundles, and
///     the two changes it can make: copy a bundled package in, or remove one. Both apply at the next
///     start, because a loaded package file is held open.
/// </summary>
internal static class PluginPackageManager
{
    /// <summary>Reads the rows for this machine.</summary>
    /// <param name="catalog">The installed packages.</param>
    /// <param name="bundle">The bundle the install came from, or null when setup recorded none.</param>
    /// <param name="bundledPackages">Where setup keeps the bundled files.</param>
    /// <param name="offers">Offers for this machine, or null without a bundle.</param>
    /// <param name="removals">The files removed while loaded; an unreadable list shows none.</param>
    /// <returns>Installed rows first, then available ones.</returns>
    internal static IReadOnlyList<PluginPackageRowState> Rows(
        PluginPackageCatalog catalog,
        BundleManifest? bundle,
        string bundledPackages,
        PluginOffers? offers,
        PendingPluginRemovalStore removals)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(removals);
        List<PluginPackageRowState> rows = [];
        var pending = removals.TryRead(out var entries) ? entries : [];

        foreach (var common in catalog.Common)
        {
            var path = common.PackagePath;
            var id = common.Manifest.Id;
            var isGpu = common.Manifest.Category == BundledPlugin.GpuCategory;
            var bundled = common.Sha256.Length == 0 ? null : bundle?.ByHash(common.Sha256);
            var removal = pending.Contains(path, StringComparer.OrdinalIgnoreCase);
            var status = removal
                ? new PluginBadge("Removing", PluginBadgeTone.Warn)
                : new PluginBadge("Installed", PluginBadgeTone.Good);
            var idle = isGpu && offers?.GpuNotForThisHardware.Any(plugin => plugin.Id == id) == true;
            var notice = removal
                ? "Removed at the next start."
                : idle
                    ? "This PC has none of the graphics it is for, so it does not run."
                    : "";
            IReadOnlyList<PluginBadge> origin = bundled is null
                ? [new PluginBadge("Local build", PluginBadgeTone.Neutral)]
                : Provenance(bundled);
            rows.Add(new PluginPackageRowState(id, common.Manifest.Name, PluginPackageSection.Installed,
                [status, .. Facts(common.Manifest.Version, isGpu), .. origin], notice,
                removal ? PluginPackageAction.None : PluginPackageAction.Remove, path));
        }

        rows.AddRange(catalog.Superseded.Select(superseded => new PluginPackageRowState(superseded.Id,
            Path.GetFileName(superseded.PackagePath), PluginPackageSection.Installed,
            [new PluginBadge("Superseded", PluginBadgeTone.Neutral)], superseded.Reason, PluginPackageAction.Remove,
            superseded.PackagePath)));
        rows.AddRange(catalog.Errors.Select(error => new PluginPackageRowState("", error.Split(':')[0],
            PluginPackageSection.Installed, [new PluginBadge("Refused", PluginBadgeTone.Bad)], error,
            PluginPackageAction.None, "")));

        if (bundle is null || offers is null)
        {
            return rows;
        }

        rows.AddRange(offers.Gpu.Where(offer => !offer.Installed).Select(offer => new PluginPackageRowState(
            offer.Plugin.Id, offer.Plugin.Name, PluginPackageSection.Available,
            [
                new PluginBadge("For this PC's graphics", PluginBadgeTone.Info),
                .. Facts(offer.Plugin.Version, true), .. Provenance(offer.Plugin)
            ],
            Contact(offer.Plugin), PluginPackageAction.Install, Path.Combine(bundledPackages, offer.Plugin.File))));
        rows.AddRange(offers.Common.Where(offer => !offer.Installed).Select(offer => new PluginPackageRowState(
            offer.Plugin.Id, offer.Plugin.Name, PluginPackageSection.Available,
            [
                new PluginBadge("Available", PluginBadgeTone.Info), .. Facts(offer.Plugin.Version, false),
                .. Provenance(offer.Plugin)
            ],
            Contact(offer.Plugin), PluginPackageAction.Install, Path.Combine(bundledPackages, offer.Plugin.File))));
        rows.AddRange(offers.GpuNotForThisHardware
            .Where(plugin => catalog.Common.All(common => common.Manifest.Id != plugin.Id))
            .Select(plugin => new PluginPackageRowState(plugin.Id, plugin.Name, PluginPackageSection.Unavailable,
            [
                new PluginBadge("Not for this PC's graphics", PluginBadgeTone.Neutral),
                .. Facts(plugin.Version, true), .. Provenance(plugin)
            ], "For " + AdapterVendors(plugin) + " graphics.", PluginPackageAction.None, "")));
        rows.AddRange(bundle.Outdated.Select(outdated => new PluginPackageRowState(outdated.Id, outdated.Id,
            PluginPackageSection.Unavailable,
            [new PluginBadge("Outdated", PluginBadgeTone.Bad), new PluginBadge("Community", PluginBadgeTone.Community)],
            $"No build for WSGM {bundle.WsgmVersion}."
            + (outdated.Contact is null ? "" : $" Developer: {outdated.Contact}"),
            PluginPackageAction.None, "")));
        return rows;
    }

    /// <summary>Copies a bundled package into the Plugins folder after checking it is the bundled file.</summary>
    /// <param name="bundled">The bundled file.</param>
    /// <param name="bundle">The bundle that lists it.</param>
    /// <param name="pluginsRoot">The Plugins folder.</param>
    /// <param name="removals">The files removed while loaded; an installed file is no longer one of them.</param>
    /// <returns>What happened, for the page.</returns>
    internal static string Install(string bundled, BundleManifest bundle, string pluginsRoot,
        PendingPluginRemovalStore removals)
    {
        ArgumentNullException.ThrowIfNull(removals);
        ArgumentNullException.ThrowIfNull(bundle);
        var entry = bundle.ByHash(Hash(bundled));
        if (entry is null)
        {
            return "The bundled file does not match this release's bundle; run Repair.";
        }

        Directory.CreateDirectory(pluginsRoot);
        var target = Path.Combine(pluginsRoot, Path.GetFileName(bundled));
        var incoming = target + ".incoming";
        File.Copy(bundled, incoming, true);
        File.Move(incoming, target, true);
        removals.Forget(target);
        return "Installed. Restart WSGM to apply.";
    }

    /// <summary>Removes a package file now, or at the next start when it is loaded.</summary>
    /// <param name="path">The installed file.</param>
    /// <param name="pluginsRoot">The Plugins folder; nothing outside it is touched.</param>
    /// <param name="removals">Where a loaded file is recorded for deletion at the next start.</param>
    /// <returns>What happened, for the page.</returns>
    internal static string Remove(string path, string pluginsRoot, PendingPluginRemovalStore removals)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (!IsInside(path, pluginsRoot))
        {
            return "That file is not in the Plugins folder.";
        }

        try
        {
            File.Delete(path);
            return "Removed.";
        }
        catch (IOException)
        {
            // A loaded package is held open until WSGM exits.
            removals.Add(path);
            return "Removed at the next start. Restart WSGM to apply.";
        }
    }

    /// <summary>Checks lexical full-path containment as a direct child of the package root.</summary>
    /// <param name="path">Candidate package path.</param>
    /// <param name="root">Expected package directory.</param>
    /// <returns>True for one direct child, case-insensitively; does not check existence or resolve reparse points.</returns>
    internal static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && full.IndexOf(Path.DirectorySeparatorChar, prefix.Length) < 0;
    }

    // Version and kind: facts every row with a manifest carries.
    private static IEnumerable<PluginBadge> Facts(string version, bool isGpu)
    {
        if (version.Length > 0)
        {
            yield return new PluginBadge("v" + version, PluginBadgeTone.Neutral);
        }

        yield return new PluginBadge(isGpu ? "Graphics" : "Integration",
            PluginBadgeTone.Neutral);
    }

    // The graphics vendors a package serves, by name where WSGM knows the PCI vendor id.
    private static string AdapterVendors(BundledPlugin plugin)
    {
        return string.Join(" or ", plugin.DisplayAdapters.Select(adapter =>
            adapter.PciVendorId.ToUpperInvariant() switch
            {
                "8086" => "Intel",
                "10DE" => "NVIDIA",
                "1002" => "AMD",
                var other => "PCI vendor " + other
            }));
    }

    // Origin and validation, which only the maintainer's curation sets. Validation is answered for
    // this machine when it is known: a package can be tested on one of the models it covers.
    private static PluginBadge[] Provenance(BundledPlugin plugin)
    {
        return
        [
            plugin.Community
                ? new PluginBadge("Community", PluginBadgeTone.Community)
                : new PluginBadge("First-party", PluginBadgeTone.Accent),
            plugin.HardwareTested
                ? new PluginBadge("Hardware-tested", PluginBadgeTone.Good)
                : new PluginBadge("Blind", PluginBadgeTone.Warn)
        ];
    }

    private static string Contact(BundledPlugin plugin)
    {
        return plugin.Community && plugin.Contact is { } contact ? "Developer: " + contact : "";
    }

    private static string Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
