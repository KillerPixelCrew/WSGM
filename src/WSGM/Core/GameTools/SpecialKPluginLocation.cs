using System;
using System.IO;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Matches local-injection SK_GetInstallPath: environment, HKCU Path, Documents/My Mods/SpecialK.</summary>
/// <remarks>https://github.com/SpecialKO/SpecialK/blob/main/src/core.cpp#L1764-L1825</remarks>
internal static class SpecialKPluginLocation
{
    internal static string Resolve(bool create)
    {
        var environment = Environment.GetEnvironmentVariable("SPECIALK_PATH");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Kaldaien\Special K");
        var registry = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Resolve(environment, registry, documents, create);
    }

    internal static string Resolve(string? environment, string? registry, string documents, bool create)
    {
        foreach (var candidate in new[] { environment, registry, Path.Combine(documents, "My Mods", "SpecialK") })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!Path.IsPathFullyQualified(candidate))
            {
                throw new GameToolException("Special K's plugin location is relative or unresolved. Use an absolute SPECIALK_PATH or Special K installation path.");
            }

            try
            {
                var full = Path.GetFullPath(candidate);
                if (create)
                {
                    Directory.CreateDirectory(full);
                }

                return full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Upstream falls through when it cannot create the selected installation directory.
            }
        }

        throw new GameToolException("Special K's shared plugin location could not be resolved.");
    }
}
