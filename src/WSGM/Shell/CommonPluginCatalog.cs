using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed record CommonInstalledPlugin(string PackageRoot, PluginManifest Manifest);
internal sealed record CommonPluginCatalog(IReadOnlyList<CommonInstalledPlugin> Packages, IReadOnlyList<string> Errors)
{
    internal static string InstalledRoot => Path.Combine(DeviceInstallationPaths.ProtectedRoot, "Plugins");

    /// <summary>Reads protected installed metadata only. Discovery never loads plugin code.</summary>
    internal static CommonPluginCatalog Discover(string installedRoot)
    {
        List<CommonInstalledPlugin> packages = [];
        List<string> errors = [];
        try
        {
            string root = Path.GetFullPath(installedRoot);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(root); }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            { return new(packages.AsReadOnly(), errors.AsReadOnly()); }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidDataException("The installed plugin directory cannot be a reparse point."); }
            var directories = Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(129).ToArray();
            if (directories.Length > 128) { throw new InvalidDataException("The installed plugin count exceeds 128."); }
            foreach (string directory in directories)
            {
                try
                {
                    var manifest = CommonPluginPackage.ReadManifest(directory);
                    if (!string.Equals(Path.GetFileName(directory), manifest.Id, StringComparison.Ordinal))
                    { throw new InvalidDataException("The package directory must match the manifest identity."); }
                    packages.Add(new(directory, manifest));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { errors.Add(Path.GetFileName(directory) + ": " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { errors.Add(ex.Message); }
        return new(packages.AsReadOnly(), errors.AsReadOnly());
    }
}
