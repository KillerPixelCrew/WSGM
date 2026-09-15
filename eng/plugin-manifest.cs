// SPDX-License-Identifier: MIT
#:project ../src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj
#:property TargetFramework=net10.0-windows

// Answers the common plugin scripts from this checkout's Plugin SDK, so they never restate its API
// version or manifest rules:
//   dotnet run --file eng/plugin-manifest.cs -- api-version
//   dotnet run --file eng/plugin-manifest.cs -- validate <plugin.wsgm.json>
// Validation is PluginManifestReader.TryRead, the reader the host uses at discovery. It loads no
// plugin code.
using WSGM.Plugin.Sdk;

switch (args)
{
    case ["api-version"]:
        Console.WriteLine(PluginApi.Version);
        return 0;
    case ["validate", string path]:
        FileInfo manifest = new(path);
        if (!manifest.Exists || manifest.Length > PluginManifestReader.MaximumBytes)
        {
            Console.Error.WriteLine("Manifest is missing or outside the supported size bounds.");
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
    default:
        Console.Error.WriteLine("usage: dotnet run --file eng/plugin-manifest.cs -- api-version | validate <plugin.wsgm.json>");
        return 64;
}
