using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Identity;
using WSGM.Install;

namespace WSGM.Core;

/// <summary>What the Plugins page can do with one row.</summary>
public enum PluginPackageAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Copy a bundled package into the Plugins folder.</summary>
    Install,

    /// <summary>Remove an installed package file.</summary>
    Remove
}

/// <summary>The colour a badge takes on the Plugins page.</summary>
public enum PluginBadgeTone
{
    /// <summary>Facts with no judgement: version, kind, a local build.</summary>
    Neutral,

    /// <summary>First-party.</summary>
    Accent,

    /// <summary>Community.</summary>
    Community,

    /// <summary>Available to install.</summary>
    Info,

    /// <summary>Installed, hardware-tested.</summary>
    Good,

    /// <summary>Blind, or being removed.</summary>
    Warn,

    /// <summary>Refused or outdated.</summary>
    Bad
}

/// <summary>One badge on a Plugins page row.</summary>
/// <param name="Text">What it says.</param>
/// <param name="Tone">Its colour.</param>
public sealed record PluginBadge(string Text, PluginBadgeTone Tone);

/// <summary>Which part of the Plugins page a row belongs to.</summary>
public enum PluginPackageSection
{
    /// <summary>A file in the Plugins folder, loaded or not.</summary>
    Installed,

    /// <summary>Bundled with this release and installable here.</summary>
    Available,

    /// <summary>Bundled but not for this hardware, or outdated.</summary>
    Unavailable
}

/// <summary>One plugin on the Plugins page.</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Section">Installed, available or unavailable.</param>
/// <param name="IsDevice">Whether it is a device plugin rather than an integration.</param>
/// <param name="Badges">Status first, then version, kind, origin and validation.</param>
/// <param name="Notice">What needs attention, or empty.</param>
/// <param name="Action">The one action the row offers.</param>
/// <param name="PackagePath">The installed file, or the bundled file to install.</param>
public sealed record PluginPackageRowState(
    string Id,
    string Name,
    PluginPackageSection Section,
    bool IsDevice,
    IReadOnlyList<PluginBadge> Badges,
    string Notice,
    PluginPackageAction Action,
    string PackagePath)
{
    /// <summary>Whether the package requests unrestricted Steam frontend access.</summary>
    public bool SteamCef { get; init; }
}

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

        void AddInstalled(string path, string sha256, string id, string name, string version, bool isDevice,
            bool isGpu, IReadOnlyList<SetupComponent> needs, string? refusal)
        {
            var bundled = sha256.Length == 0 ? null : bundle?.ByHash(sha256);
            var removal = pending.Contains(path, StringComparer.OrdinalIgnoreCase);
            var status = refusal is not null
                ? new PluginBadge("Refused", PluginBadgeTone.Bad)
                : removal
                    ? new PluginBadge("Removing", PluginBadgeTone.Warn)
                    : new PluginBadge("Installed", PluginBadgeTone.Good);
            var idle = isGpu && offers?.GpuNotForThisHardware.Any(plugin => plugin.Id == id) == true;
            var notice = refusal
                         ?? (removal
                             ? "Removed at the next start."
                             : idle
                                 ? "This PC has none of the graphics it is for, so it does not run."
                                 : needs.Count == 0
                                     ? ""
                                     : "Needs " + string.Join(", ", needs.Select(SetupComponents.DisplayName))
                                                + ". Run Repair if it is missing.");
            IReadOnlyList<PluginBadge> origin = bundled is null
                ? [new PluginBadge("Local build", PluginBadgeTone.Neutral)]
                : Provenance(bundled, offers?.Identity);
            rows.Add(new PluginPackageRowState(id, name, PluginPackageSection.Installed, isDevice,
                [status, .. Facts(version, isDevice, isGpu), .. origin], notice,
                removal ? PluginPackageAction.None : PluginPackageAction.Remove, path));
        }

        if (catalog.Device.InstalledPackage is { Manifest: { } device } installed)
        {
            AddInstalled(installed.PackagePath, installed.Sha256, device.Id, device.Name, device.Version, true, false,
                SetupComponents.Required(device.Capabilities),
                installed.Valid
                    ? null
                    : installed.Detail ?? installed.RejectionCode ?? "The package did not pass validation.");
        }

        foreach (var file in catalog.Device.Inventory.PackageFiles.Where(_ => catalog.Device.ErrorCode is not null))
        {
            rows.Add(new PluginPackageRowState(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file),
                PluginPackageSection.Installed, true, [new PluginBadge("Refused", PluginBadgeTone.Bad)],
                "More than one device plugin is installed. Remove all but one.", PluginPackageAction.Remove, file));
        }

        foreach (var common in catalog.Common)
        {
            AddInstalled(common.PackagePath, common.Sha256, common.Manifest.Id, common.Manifest.Name,
                common.Manifest.Version, false, common.Manifest.Category == BundledPlugin.GpuCategory, [], null);
        }

        rows.AddRange(catalog.Superseded.Select(superseded => new PluginPackageRowState(superseded.Id,
            Path.GetFileName(superseded.PackagePath), PluginPackageSection.Installed, false,
            [new PluginBadge("Superseded", PluginBadgeTone.Neutral)], superseded.Reason, PluginPackageAction.Remove,
            superseded.PackagePath)));
        rows.AddRange(catalog.Errors.Select(error => new PluginPackageRowState("", error.Split(':')[0],
            PluginPackageSection.Installed, false, [new PluginBadge("Refused", PluginBadgeTone.Bad)], error,
            PluginPackageAction.None, "")));

        if (bundle is null || offers is null)
        {
            return rows;
        }

        rows.AddRange(offers.Gpu.Where(offer => !offer.Installed).Select(offer => new PluginPackageRowState(
            offer.Plugin.Id, offer.Plugin.Name, PluginPackageSection.Available, false,
            [
                new PluginBadge("For this PC's graphics", PluginBadgeTone.Info),
                .. Facts(offer.Plugin.Version, false, true), .. Provenance(offer.Plugin, offers.Identity)
            ],
            Contact(offer.Plugin), PluginPackageAction.Install, Path.Combine(bundledPackages, offer.Plugin.File))));
        rows.AddRange(offers.Common.Where(offer => !offer.Installed).Select(offer => new PluginPackageRowState(
            offer.Plugin.Id, offer.Plugin.Name, PluginPackageSection.Available, false,
            [
                new PluginBadge("Available", PluginBadgeTone.Info), .. Facts(offer.Plugin.Version, false, false),
                .. Provenance(offer.Plugin, offers.Identity)
            ],
            Contact(offer.Plugin), PluginPackageAction.Install, Path.Combine(bundledPackages, offer.Plugin.File))));
        rows.AddRange(offers.GpuNotForThisHardware
            .Where(plugin => catalog.Common.All(common => common.Manifest.Id != plugin.Id))
            .Select(plugin => new PluginPackageRowState(plugin.Id, plugin.Name, PluginPackageSection.Unavailable,
                false,
                [
                    new PluginBadge("Not for this PC's graphics", PluginBadgeTone.Neutral),
                    .. Facts(plugin.Version, false, true), .. Provenance(plugin, null)
                ], "For " + AdapterVendors(plugin) + " graphics.", PluginPackageAction.None, "")));
        rows.AddRange(bundle.Outdated.Select(outdated => new PluginPackageRowState(outdated.Id, outdated.Id,
            PluginPackageSection.Unavailable, false,
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

        if (entry.IsDevice)
        {
            return "Handheld support is built into WSGM through LibHandheld; device packages are retired.";
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
    private static IEnumerable<PluginBadge> Facts(string version, bool isDevice, bool isGpu)
    {
        if (version.Length > 0)
        {
            yield return new PluginBadge("v" + version, PluginBadgeTone.Neutral);
        }

        yield return new PluginBadge(isDevice ? "Device" : isGpu ? "Graphics" : "Integration",
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
    private static PluginBadge[] Provenance(BundledPlugin plugin, DeviceIdentitySnapshot? identity)
    {
        return
        [
            plugin.Community
                ? new PluginBadge("Community", PluginBadgeTone.Community)
                : new PluginBadge("First-party", PluginBadgeTone.Accent),
            plugin.HardwareTestedOn(identity)
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

/// <summary>
///     Package files the Plugins page removed while they were loaded. WSGM deletes them once at startup,
///     before it opens any package. Only files directly in the Plugins folder are ever deleted.
/// </summary>
/// <param name="path">The file the list is kept in.</param>
internal sealed class PendingPluginRemovalStore(string path)
{
    /// <summary>Reads the list.</summary>
    /// <param name="entries">The pending files; empty when there is no list.</param>
    /// <returns>False when a list exists but cannot be read, so nothing may write over it.</returns>
    internal bool TryRead(out string[] entries)
    {
        entries = [];
        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllText(path),
                PendingRemovalsJsonContext.Default.StringArray);
            if (read is null || read.Any(entry => entry is null))
            {
                Log.Warn("Plugins: the pending removal list is malformed and is left as it is.");
                return false;
            }

            entries = read;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Plugins: the pending removal list could not be read and is left as it is: " + ex.Message);
            return false;
        }
    }

    /// <summary>Records a file to delete at the next start.</summary>
    /// <param name="packagePath">The installed file.</param>
    internal void Add(string packagePath)
    {
        if (TryRead(out var entries))
        {
            Write([.. entries.Append(packagePath).Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
    }

    /// <summary>Drops a file from the list, because it was installed again.</summary>
    /// <param name="packagePath">The installed file.</param>
    internal void Forget(string packagePath)
    {
        if (!TryRead(out var entries))
        {
            return;
        }

        var remaining = entries
            .Where(entry => !string.Equals(entry, packagePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length != entries.Length)
        {
            Write(remaining);
        }
    }

    /// <summary>Deletes what can be deleted now and keeps the rest.</summary>
    /// <param name="pluginsRoot">The Plugins folder.</param>
    internal void Apply(string pluginsRoot)
    {
        if (!TryRead(out var pending) || pending.Length == 0)
        {
            return;
        }

        List<string> kept = [];
        foreach (var file in pending)
        {
            if (!PluginPackageManager.IsInside(file, pluginsRoot))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                Log.Info($"Plugins: removed {Path.GetFileName(file)} as asked on the Plugins page.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Plugins: {Path.GetFileName(file)} could not be removed yet and stays pending: {ex.Message}");
                kept.Add(file);
            }
        }

        Write(kept);
    }

    private void Write(IReadOnlyList<string> entries)
    {
        try
        {
            if (entries.Count == 0)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteText(path,
                JsonSerializer.Serialize(entries.ToArray(), PendingRemovalsJsonContext.Default.StringArray), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Plugins: the pending removal list could not be written: " + ex.Message);
        }
    }
}

/// <summary>Source-generated JSON metadata for the pending removal list.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class PendingRemovalsJsonContext : JsonSerializerContext;
