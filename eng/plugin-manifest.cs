// SPDX-License-Identifier: MIT
#:project ../src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj
#:property TargetFramework=net10.0-windows

// Answers the common plugin scripts from this checkout's SDKs, so they never restate the API
// version, the manifest rules or the package layout:
//   dotnet run --file eng/plugin-manifest.cs -- api-version
//   dotnet run --file eng/plugin-manifest.cs -- validate <plugin.wsgm.json>
//   dotnet run --file eng/plugin-manifest.cs -- validate-package <file.wsgmpkg>
//   dotnet run --file eng/plugin-manifest.cs -- host-provided
// Validation is PluginManifestReader.TryRead, the reader the host uses at discovery, including the
// graphics category's display adapters and capabilities. validate-package also applies the Device
// SDK's PluginPackageLayout (byte bounds, entry names, duplicates, no native images) and checks the
// entry assembly at the package root, as WSGM does when it opens a common package. It loads no plugin
// code. host-provided prints the assemblies WSGM supplies itself, one per line.
using WSGM.Plugin.Sdk;
using PluginPackageLayout = WSGM.Device.Sdk.Packaging.PluginPackageLayout;

switch (args)
{
    case ["api-version"]:
        Console.WriteLine(PluginApi.Version);
        return 0;
    case ["host-provided"]:
        foreach (string name in PluginPackageLayout.HostProvidedAssemblies)
        {
            Console.WriteLine(name);
        }

        return 0;
    case ["validate", string path]:
        FileInfo manifest = new(path);
        if (!manifest.Exists)
        {
            Console.Error.WriteLine("Manifest is missing.");
            return 1;
        }

        if (PluginManifestReader.TryRead(File.ReadAllBytes(path), out _, out IReadOnlyList<string> errors))
        {
            return 0;
        }

        foreach (string error in errors)
        {
            Console.Error.WriteLine(error);
        }

        return 1;
    case ["validate-package", string packagePath]:
        try
        {
            Dictionary<string, byte[]> entries;
            using (FileStream archive = File.OpenRead(packagePath))
            {
                entries = PluginPackageLayout.ReadEntries(archive);
            }

            if (!entries.TryGetValue("plugin.wsgm.json", out byte[]? manifestBytes))
            {
                Console.Error.WriteLine("The package has no plugin.wsgm.json at its root.");
                return 1;
            }

            if (!PluginManifestReader.TryRead(manifestBytes, out PluginManifest? common,
                    out IReadOnlyList<string> packageErrors))
            {
                foreach (string error in packageErrors)
                {
                    Console.Error.WriteLine(error);
                }

                return 1;
            }

            if (common!.Category == PluginCategories.Device)
            {
                Console.Error.WriteLine("Device packages use the device manifest, not a common category.");
                return 1;
            }

            if (!PluginPackageLayout.TryNormalizeEntryName(common.EntryAssembly, out string entryName)
                || entryName.Contains('/')
                || !entries.ContainsKey(entryName))
            {
                Console.Error.WriteLine("The plugin entry assembly is missing from the package root.");
                return 1;
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    default:
        Console.Error.WriteLine(
            "usage: dotnet run --file eng/plugin-manifest.cs -- api-version | host-provided | "
            + "validate <plugin.wsgm.json> | validate-package <file.wsgmpkg>");
        return 64;
}
