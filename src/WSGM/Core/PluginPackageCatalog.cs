using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSGM.Device.Sdk;
using WSGM.Install;
using WSGM.Plugin.Sdk;
using DeviceManifest = WSGM.Device.Sdk.Packaging.PluginManifest;
using CommonManifest = WSGM.Plugin.Sdk.PluginManifest;

namespace WSGM.Core;

/// <summary>How many different device packages the Plugins folder offers.</summary>
internal enum DevicePackageCardinality
{
    /// <summary>No device package is installed; core WSGM remains available without Device Integration.</summary>
    Empty,

    /// <summary>Exactly one device package id is present and may be validated.</summary>
    Single,

    /// <summary>More than one device package id is present; device integration refuses all of them.</summary>
    Multiple
}

/// <summary>The device package files found in the Plugins folder.</summary>
internal sealed record DevicePackageInventory
{
    /// <summary>The selected file for every distinct device package id, sorted by path.</summary>
    public required IReadOnlyList<string> PackageFiles { get; init; }

    /// <summary>The cardinality derived solely from <see cref="PackageFiles" />.</summary>
    public DevicePackageCardinality Cardinality => PackageFiles.Count switch
    {
        0 => DevicePackageCardinality.Empty,
        1 => DevicePackageCardinality.Single,
        _ => DevicePackageCardinality.Multiple
    };
}

/// <summary>The sole installed device package after structural, API and architecture validation.</summary>
internal sealed record InstalledDevicePackage
{
    /// <summary>Canonical path of the package file.</summary>
    public required string PackagePath { get; init; }

    /// <summary>Parsed manifest when structural validation succeeded.</summary>
    public DeviceManifest? Manifest { get; init; }

    /// <summary>Whether this sole package may be activated.</summary>
    public required bool Valid { get; init; }

    /// <summary>Stable rejection code, or null when eligible.</summary>
    public string? RejectionCode { get; init; }

    /// <summary>Sanitized diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>The device side of one Plugins folder read.</summary>
internal sealed record DevicePackageDiscovery
{
    /// <summary>The device package files considered.</summary>
    public required DevicePackageInventory Inventory { get; init; }

    /// <summary>The sole installed package, including its validation failure when invalid.</summary>
    public InstalledDevicePackage? InstalledPackage { get; init; }

    /// <summary>The folder-level failure code used when several device packages were found.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Sanitized folder-level failure detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>One installed non-device package, admitted by metadata only.</summary>
internal sealed record CommonInstalledPlugin(
    string PackagePath,
    CommonManifest Manifest,
    Func<IPlugin>? Factory = null);

/// <summary>One package file that was not selected, and why.</summary>
internal sealed record PluginPackageNotice(string PackagePath, string Id, string Reason);

/// <summary>Everything one read of the Plugins folder found. Discovery never loads plugin code.</summary>
internal sealed record PluginPackageCatalog
{
    private const int MaxPackages = 128;

    /// <summary>The device package, if any, and why it may or may not run.</summary>
    public required DevicePackageDiscovery Device { get; init; }

    /// <summary>The selected non-device packages.</summary>
    public required IReadOnlyList<CommonInstalledPlugin> Common { get; init; }

    /// <summary>Files that lost to a newer version of the same id. They are listed, never deleted.</summary>
    public required IReadOnlyList<PluginPackageNotice> Superseded { get; init; }

    /// <summary>Files that could not be read or failed validation, with the reason.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    internal static PluginPackageCatalog Empty { get; } = new()
    {
        Device = new DevicePackageDiscovery { Inventory = new DevicePackageInventory { PackageFiles = [] } },
        Common = [],
        Superseded = [],
        Errors = []
    };

    /// <summary>Reads the production Plugins folder.</summary>
    /// <remarks>Package files the Plugins page removed while they were loaded are deleted first.</remarks>
    internal static PluginPackageCatalog DiscoverInstalled()
    {
        PendingPluginRemovals.Apply(InstallLayout.Plugins);
        return Discover(InstallLayout.Plugins);
    }

    /// <summary>The id of the installed, valid device plugin, or null when there is none.</summary>
    /// <remarks>
    ///     A settings declaration is cached in configuration and outlives its package, so every surface
    ///     that draws device plugin settings keeps to the scope of the plugin actually installed.
    /// </remarks>
    internal static string? InstalledDevicePluginId()
    {
        try
        {
            var package = DiscoverInstalled().Device.InstalledPackage;
            return package is { Valid: true, Manifest: { } manifest } ? manifest.Id : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Plugin settings unavailable: installed packages could not be inspected ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Reads every <c>.wsgmpkg</c> directly in <paramref name="pluginsRoot" />.</summary>
    /// <param name="pluginsRoot">The folder holding package files.</param>
    /// <returns>The selected device and common packages, superseded files and errors.</returns>
    /// <remarks>
    ///     For each id the highest version wins; older files stay on disk and are reported, because WSGM
    ///     never deletes a file it did not place. Two different device package ids refuse device
    ///     integration entirely rather than choose between them.
    /// </remarks>
    internal static PluginPackageCatalog Discover(string pluginsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        List<string> errors = [];
        string[] files;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot));
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(root);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            {
                return Empty;
            }

            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The Plugins folder must be a plain directory.");
            }

            files =
            [
                .. Directory.EnumerateFiles(root, "*" + PluginPackageFile.Extension, SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), PluginPackageFile.Extension,
                        StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxPackages + 1)
            ];
            if (files.Length > MaxPackages)
            {
                throw new InvalidDataException($"The Plugins folder holds more than {MaxPackages} packages.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException)
        {
            return Empty with { Errors = [ex.Message] };
        }

        List<Candidate> candidates = [];
        foreach (var file in files)
        {
            try
            {
                using var package = PluginPackageFile.Open(file);
                if (!PluginPackageFile.IsForThisHost(package.WsgmVersion))
                {
                    errors.Add($"{Path.GetFileName(file)}: {package.Id} {package.Version} was built for WSGM "
                               + $"{package.WsgmVersion ?? "(unstamped)"}, not {PluginPackageFile.HostVersion.ToString(3)}.");
                    continue;
                }

                candidates.Add(new Candidate(package.Path, package.Id, ParseVersion(package.Version),
                    package.DeviceManifest, package.CommonManifest, package.EntryIsX64Assembly));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        List<PluginPackageNotice> superseded = [];
        List<Candidate> selected = [];
        foreach (var group in candidates.GroupBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderByDescending(candidate => candidate.Version)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            selected.Add(ordered[0]);
            superseded.AddRange(ordered.Skip(1).Select(loser => new PluginPackageNotice(loser.Path, loser.Id,
                $"Superseded by version {ordered[0].Version} in {Path.GetFileName(ordered[0].Path)}.")));
        }

        var devices = selected.Where(candidate => candidate.Device is not null)
            .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PluginPackageCatalog
        {
            Device = DeviceDiscovery(devices),
            Common =
            [
                .. selected.Where(candidate => candidate.Common is not null)
                    .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .Select(candidate => new CommonInstalledPlugin(candidate.Path, candidate.Common!))
            ],
            Superseded = superseded.AsReadOnly(),
            Errors = errors.AsReadOnly()
        };
    }

    private static DevicePackageDiscovery DeviceDiscovery(Candidate[] devices)
    {
        DevicePackageInventory inventory = new() { PackageFiles = [.. devices.Select(device => device.Path)] };
        return inventory.Cardinality switch
        {
            DevicePackageCardinality.Empty => new DevicePackageDiscovery { Inventory = inventory },
            DevicePackageCardinality.Multiple => new DevicePackageDiscovery
            {
                Inventory = inventory,
                ErrorCode = "multiple-device-packages",
                Detail = "Device integration refuses every device package while more than one is installed. "
                         + "Remove all but one from the Plugins folder."
            },
            _ => new DevicePackageDiscovery { Inventory = inventory, InstalledPackage = Validate(devices[0]) }
        };
    }

    private static InstalledDevicePackage Validate(Candidate device)
    {
        var (code, detail) = device.Device!.ApiVersion != DeviceApi.Version
            ? ("api-incompatible", "Package API version does not equal this runtime.")
            : !device.EntryIsX64Assembly
                ? ("architecture-unsupported", "Plugin entry point is not an x64 managed assembly.")
                : ((string?)null, (string?)null);
        return new InstalledDevicePackage
        {
            PackagePath = device.Path,
            Manifest = device.Device,
            Valid = code is null,
            RejectionCode = code,
            Detail = detail
        };
    }

    private static Version ParseVersion(string version)
    {
        return System.Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0);
    }

    private sealed record Candidate(
        string Path,
        string Id,
        Version Version,
        DeviceManifest? Device,
        CommonManifest? Common,
        bool EntryIsX64Assembly);
}
