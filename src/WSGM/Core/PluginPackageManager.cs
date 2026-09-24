using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>One line of the Plugins page.</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">Version, or empty for an unreadable file.</param>
/// <param name="Status">Installed, available, not for this hardware, superseded or refused.</param>
/// <param name="Badges">Origin and validation, or "Local build" when the file is not from the bundle.</param>
/// <param name="Notice">What needs attention, or empty.</param>
/// <param name="Action">The one action the row offers.</param>
/// <param name="PackagePath">The installed file, or the bundled file to install.</param>
public sealed record PluginPackageRowState(
    string Id,
    string Name,
    string Version,
    string Status,
    string Badges,
    string Notice,
    PluginPackageAction Action,
    string PackagePath);

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
    /// <returns>Installed rows first, then available ones.</returns>
    internal static IReadOnlyList<PluginPackageRowState> Rows(
        PluginPackageCatalog catalog,
        BundleManifest? bundle,
        string bundledPackages,
        PluginOffers? offers)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        List<PluginPackageRowState> rows = [];
        var pending = PendingPluginRemovals.Read();

        void AddInstalled(string path, string id, string name, string version, IReadOnlyList<SetupComponent> needs)
        {
            var bundled = bundle?.ByHash(Hash(path));
            var removal = pending.Contains(path, StringComparer.OrdinalIgnoreCase);
            var notice = removal
                ? "Removed at the next start."
                : needs.Count == 0
                    ? ""
                    : "Needs " + string.Join(", ", needs.Select(SetupComponents.DisplayName))
                               + ". Run Repair if it is missing.";
            rows.Add(new PluginPackageRowState(id, name, version, removal ? "Removing" : "Installed",
                bundled is null ? "Local build" : Badges(bundled), notice,
                removal ? PluginPackageAction.None : PluginPackageAction.Remove, path));
        }

        if (catalog.Device.InstalledPackage is { Manifest: { } device } installed)
        {
            AddInstalled(installed.PackagePath, device.Id, device.Name, device.Version,
                SetupComponents.Required(device.Capabilities));
            if (!installed.Valid)
            {
                rows[^1] = rows[^1] with
                {
                    Status = "Refused",
                    Notice = installed.Detail ?? installed.RejectionCode ?? "The package did not pass validation."
                };
            }
        }

        foreach (var file in catalog.Device.Inventory.PackageFiles.Where(_ => catalog.Device.ErrorCode is not null))
        {
            rows.Add(new PluginPackageRowState(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file), "",
                "Refused", "", "More than one device plugin is installed. Remove all but one.",
                PluginPackageAction.Remove, file));
        }

        foreach (var common in catalog.Common)
        {
            AddInstalled(common.PackagePath, common.Manifest.Id, common.Manifest.Name, common.Manifest.Version, []);
        }

        rows.AddRange(catalog.Superseded.Select(superseded => new PluginPackageRowState(superseded.Id,
            Path.GetFileName(superseded.PackagePath), "", "Superseded", "", superseded.Reason,
            PluginPackageAction.Remove, superseded.PackagePath)));
        rows.AddRange(catalog.Errors.Select(error => new PluginPackageRowState("", error.Split(':')[0], "", "Refused",
            "", error, PluginPackageAction.None, "")));

        if (bundle is null || offers is null)
        {
            return rows;
        }

        var installedDevice = catalog.Device.InstalledPackage?.Manifest?.Id;
        foreach (var offer in offers.DeviceCandidates.Where(offer => !offer.Installed))
        {
            var blocked = installedDevice is not null;
            rows.Add(new PluginPackageRowState(offer.Plugin.Id, offer.Plugin.Name, offer.Plugin.Version,
                "Available for this device", Badges(offer.Plugin),
                blocked ? $"Remove {installedDevice} first: only one device plugin runs." : Contact(offer.Plugin),
                blocked ? PluginPackageAction.None : PluginPackageAction.Install,
                Path.Combine(bundledPackages, offer.Plugin.File)));
        }

        rows.AddRange(offers.Common.Where(offer => !offer.Installed).Select(offer => new PluginPackageRowState(
            offer.Plugin.Id, offer.Plugin.Name, offer.Plugin.Version, "Available", Badges(offer.Plugin),
            Contact(offer.Plugin), PluginPackageAction.Install, Path.Combine(bundledPackages, offer.Plugin.File))));
        rows.AddRange(offers.NotForThisHardware.Select(plugin => new PluginPackageRowState(plugin.Id, plugin.Name,
            plugin.Version, "Not for this hardware", Badges(plugin), "", PluginPackageAction.None, "")));
        rows.AddRange(bundle.Outdated.Select(outdated => new PluginPackageRowState(outdated.Id, outdated.Id, "",
            "Outdated", "Community",
            $"No build for WSGM {bundle.WsgmVersion}." +
            (outdated.Contact is null ? "" : $" Developer: {outdated.Contact}"),
            PluginPackageAction.None, "")));
        return rows;
    }

    /// <summary>Copies a bundled package into the Plugins folder after checking it is the bundled file.</summary>
    /// <param name="bundled">The bundled file.</param>
    /// <param name="bundle">The bundle that lists it.</param>
    /// <param name="pluginsRoot">The Plugins folder.</param>
    /// <returns>What happened, for the page.</returns>
    internal static string Install(string bundled, BundleManifest bundle, string pluginsRoot)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.ByHash(Hash(bundled)) is null)
        {
            return "The bundled file does not match this release's bundle; run Repair.";
        }

        Directory.CreateDirectory(pluginsRoot);
        var target = Path.Combine(pluginsRoot, Path.GetFileName(bundled));
        var incoming = target + ".incoming";
        File.Copy(bundled, incoming, true);
        File.Move(incoming, target, true);
        PendingPluginRemovals.Forget(target);
        return "Installed. Restart WSGM to apply.";
    }

    /// <summary>Removes a package file now, or at the next start when it is loaded.</summary>
    /// <param name="path">The installed file.</param>
    /// <param name="pluginsRoot">The Plugins folder; nothing outside it is touched.</param>
    /// <returns>What happened, for the page.</returns>
    internal static string Remove(string path, string pluginsRoot)
    {
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
            PendingPluginRemovals.Add(path);
            return "Removed at the next start. Restart WSGM to apply.";
        }
    }

    internal static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && full.IndexOf(Path.DirectorySeparatorChar, prefix.Length) < 0;
    }

    private static string Badges(BundledPlugin plugin)
    {
        return (plugin.Community ? "Community" : "First-party") + " · "
                                                                + (plugin.HardwareTested ? "Hardware-tested" : "Blind");
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
///     Package files the Plugins page removed while they were loaded. Deleted before the next
///     discovery, when nothing holds them. Only files directly in the Plugins folder are ever deleted.
/// </summary>
internal static class PendingPluginRemovals
{
    internal static IReadOnlyList<string> Read(string? path = null)
    {
        try
        {
            var file = path ?? InstallLayout.PendingPluginRemovals;
            return File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), PendingRemovalsJsonContext.Default.StringArray) ??
                  []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    internal static void Add(string packagePath)
    {
        Write([.. Read().Append(packagePath).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    internal static void Forget(string packagePath)
    {
        var remaining = Read().Where(entry => !string.Equals(entry, packagePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length != Read().Count)
        {
            Write(remaining);
        }
    }

    /// <summary>Deletes what can be deleted now and keeps the rest.</summary>
    /// <param name="pluginsRoot">The Plugins folder.</param>
    internal static void Apply(string pluginsRoot)
    {
        var pending = Read();
        if (pending.Count == 0)
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
            catch (IOException)
            {
                kept.Add(file);
            }
            catch (UnauthorizedAccessException)
            {
                kept.Add(file);
            }
        }

        Write(kept);
    }

    private static void Write(IReadOnlyList<string> entries)
    {
        try
        {
            var file = InstallLayout.PendingPluginRemovals;
            if (entries.Count == 0)
            {
                File.Delete(file);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file,
                JsonSerializer.Serialize(entries.ToArray(), PendingRemovalsJsonContext.Default.StringArray));
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
