using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WSGM.Core;

internal sealed record EmulatorSource
{
    public string Provider { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Manifest { get; init; } = "";
    public Dictionary<string, string> Repositories { get; init; } = new(StringComparer.Ordinal);
    public bool BodyAssets { get; init; }
    public string ChecksumFile { get; init; } = "";
    public string AssetPattern { get; init; } = "";
    public Dictionary<string, string> AssetArchitectureNames { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AssetVariants { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PlatformNames { get; init; } = new(StringComparer.Ordinal);
    public string PackageName { get; init; } = "";
    public string PackagePattern { get; init; } = "";
    public string CorePath { get; init; } = "";
    public string AssetPath { get; init; } = "";
}

internal sealed record EmulatorPackageDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] Systems { get; init; } = [];
    public string[] Architectures { get; init; } = [];
    public string[] ExecutableNames { get; init; } = [];
    public string[] LaunchArguments { get; init; } = [];
    public EmulatorDataPolicy DataPolicy { get; init; } = new();
    public string PackageType { get; init; } = "";
    public string[] ValidationArguments { get; init; } = [];
    public int[] ValidationExitCodes { get; init; } = [0];
    public string ValidationOutput { get; init; } = "";
    public Dictionary<string, EmulatorSource> Sources { get; init; } = new(StringComparer.Ordinal);

    public EmulatorDefinition View()
    {
        return new EmulatorDefinition
        {
            Id = Id,
            Name = Name,
            Systems = [.. Systems],
            Channels = [.. Sources.Keys],
            DataPolicy = DataPolicy,
            Source = string.Join(" / ", Sources.Values.Select(source => source.BaseUrl).Distinct()),
            Prerequisites = DataPolicy.Prerequisites
        };
    }
}

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
                || definition.PackageType is not ("zip" or "7z") || definition.ValidationArguments.Length == 0)
            {
                throw new InvalidDataException("Invalid emulator definition: " + definition.Id);
            }

            foreach (var source in definition.Sources.Values)
            {
                EmulatorNetwork.Https(source.BaseUrl);
                if (source.Provider is not ("GitHub" or "Forgejo" or "Scoop" or "Buildbot" or "Dolphin"))
                {
                    throw new InvalidDataException("Unsupported emulator release provider: " + source.Provider);
                }
            }
        }

        return catalog;
    }
}
