using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Metadata-only discovery of independent plugins; hardware libraries have no package slot.</summary>
internal sealed record PluginPackageCatalog
{
    public required IReadOnlyList<CommonInstalledPlugin> Common { get; init; }
    public required IReadOnlyList<PluginPackageNotice> Superseded { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    internal static PluginPackageCatalog Empty { get; } = new()
        { Common = [], Superseded = [], Errors = [] };

    internal static PluginPackageCatalog Discover(string pluginsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        List<string> errors = [];
        string[] files;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot));
            if (!Directory.Exists(root))
            {
                return Empty;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return Empty with { Errors = ["The Plugins folder must not be a reparse point."] };
            }

            files = Directory.GetFiles(root, "*" + PluginPackageFile.Extension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Empty with { Errors = ["The Plugins folder could not be read: " + exception.Message] };
        }

        List<CommonInstalledPlugin> candidates = [];
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var package = PluginPackageFile.Open(file);
                if (BuiltinGpuDrivers.Contains(package.Id))
                {
                    continue;
                }

                if (!PluginPackageFile.IsForThisHost(package.WsgmVersion))
                {
                    errors.Add($"{Path.GetFileName(file)}: {package.Id} {package.Version} was built for WSGM "
                               + $"{package.WsgmVersion ?? "(unstamped)"}, not {PluginPackageFile.HostVersion.ToString(3)}.");
                    continue;
                }

                candidates.Add(new CommonInstalledPlugin(package.Path, package.CommonManifest)
                    { Sha256 = package.HashFile() });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InvalidDataException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }

        List<CommonInstalledPlugin> selected = [];
        List<PluginPackageNotice> superseded = [];
        foreach (var group in candidates.GroupBy(candidate => candidate.Manifest.Id, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(candidate => Version.TryParse(candidate.Manifest.Version,
                    out var version)
                    ? version
                    : new Version(0, 0))
                .ThenBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase).ToArray();
            var winner = ordered[0];
            selected.Add(winner);
            superseded.AddRange(ordered.Skip(1).Select(loser => new PluginPackageNotice(loser.PackagePath,
                loser.Manifest.Id,
                $"Superseded by version {winner.Manifest.Version} in {Path.GetFileName(winner.PackagePath)}.")));
        }

        return new PluginPackageCatalog
        {
            Common = selected.OrderBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal).ToArray(),
            Superseded = superseded.AsReadOnly(), Errors = errors.AsReadOnly()
        };
    }
}
