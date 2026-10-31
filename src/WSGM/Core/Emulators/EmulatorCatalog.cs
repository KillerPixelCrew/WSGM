using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WSGM.Core;

internal sealed record EmulatorCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public EmulatorPackageDefinition[] Definitions { get; init; } = [];

    public static EmulatorCatalog LoadBundled()
    {
        using var embedded =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("WSGM.Core.Emulators.emulators.json")
            ?? throw new InvalidDataException("The emulator definition catalogue is missing from this build.");
        return Parse(embedded);
    }

    public static EmulatorCatalog Parse(Stream file)
    {
        var catalog = JsonSerializer.Deserialize<EmulatorCatalog>(file, EmulatorStorage.JsonOptions)
                      ?? throw new InvalidDataException("The emulator definition catalogue is empty.");
        if (catalog.SchemaVersion != 1 || catalog.Definitions is null || catalog.Definitions.Length == 0
            || catalog.Definitions.Select(definition => definition.Id).Distinct(StringComparer.Ordinal).Count()
            != catalog.Definitions.Length)
        {
            throw new InvalidDataException(
                "The emulator definition catalogue is unsupported or contains duplicate identities.");
        }

        foreach (var definition in catalog.Definitions)
        {
            if (!Regex.IsMatch(definition.Id, "^[a-z0-9][a-z0-9-]*$")
                || definition.Sources.Count == 0 || definition.ExecutableNames.Length == 0
                || definition.ExecutableNames.Any(name => Path.GetFileName(name) != name)
                || definition.PackageType is not ("zip" or "7z" or "exe") || definition.ValidationArguments.Length == 0
                || definition.SteamGridDbId is <= 0)
            {
                throw new InvalidDataException("Invalid emulator definition: " + definition.Id);
            }

            foreach (var source in definition.Sources.Values)
            {
                DownloadClient.Https(source.BaseUrl);
                if (!HostedReleases.Providers.Contains(source.Provider)
                    && source.Provider is not ("Scoop" or "Buildbot" or "Dolphin" or "WebPage"))
                {
                    throw new InvalidDataException("Unsupported emulator release provider: " + source.Provider);
                }
            }
        }

        return catalog;
    }
}
