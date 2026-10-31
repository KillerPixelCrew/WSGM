using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>The validated, bundled Game Tools definitions.</summary>
internal sealed record GameToolCatalog
{
    private static readonly Lazy<GameToolCatalog> Bundled = new(LoadBundled);

    /// <summary>The catalog format version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The definitions.</summary>
    public GameToolDefinition[] Tools { get; init; } = [];

    /// <summary>The definitions shipped in this build, parsed once.</summary>
    internal static GameToolCatalog Current => Bundled.Value;

    /// <summary>Finds a definition by id.</summary>
    /// <param name="id">The tool id.</param>
    /// <returns>The definition, or null.</returns>
    internal GameToolDefinition? Find(string id)
    {
        return Tools.FirstOrDefault(tool => tool.Id == id);
    }

    private static GameToolCatalog LoadBundled()
    {
        using var embedded =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("WSGM.Core.GameTools.game-tools.json")
            ?? throw new InvalidDataException("The Game Tools catalog is missing from this build.");
        return Parse(embedded);
    }

    /// <summary>Parses and validates a catalog.</summary>
    /// <param name="file">The JSON document.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">The catalog is unsupported or a definition is invalid.</exception>
    internal static GameToolCatalog Parse(Stream file)
    {
        var catalog = JsonSerializer.Deserialize<GameToolCatalog>(file, GameToolStorage.JsonOptions)
                      ?? throw new InvalidDataException("The Game Tools catalog is empty.");
        if (catalog.SchemaVersion != 1 || catalog.Tools is null || catalog.Tools.Length == 0
            || catalog.Tools.Select(tool => tool.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Tools.Length)
        {
            throw new InvalidDataException("The Game Tools catalog is unsupported or contains duplicate identities.");
        }

        foreach (var tool in catalog.Tools)
        {
            Validate(tool);
        }

        return catalog;
    }

    private static void Validate(GameToolDefinition tool)
    {
        var invalid = !Regex.IsMatch(tool.Id, "^[a-z0-9][a-z0-9-]*$") || tool.Name.Length == 0
                      || tool.PrimaryFile.Length == 0 || Path.GetFileName(tool.PrimaryFile) != tool.PrimaryFile
                      || tool.Architectures.Length == 0 || tool.Proxy.Options.Length == 0
                      || tool.Proxy.Options.All(option => option.Name != tool.Proxy.Default)
                      || tool.Proxy.Options.Any(option => !DownloadClient.IsSafeAssetName(option.Name))
                      || tool.Identity.OriginalFilename.Length == 0
                      || !HostedReleases.Providers.Contains(tool.Source.Provider)
                      || tool.RequiresUpscaler.Any(requirement => requirement.Family.Length == 0
                                                                  || requirement.Label.Length == 0)
                      || tool.Source.PackageType is not ("7z" or "zip")
                      || tool.Source.AssetPattern.Length == 0
                      || tool.Payload.Required.Length == 0
                      || tool.ConfigFiles.Any(name => !SafeRelative(name))
                      || tool.RuntimeArtifacts.Any(name => !SafeRelative(name))
                      || tool.Homepage.Length > 0 && JsonRead.HttpsUrl(tool.Homepage).Length == 0;
        foreach (var setting in tool.Settings)
        {
            invalid |= !Regex.IsMatch(setting.Id, "^[a-zA-Z][a-zA-Z0-9]*$") || setting.Section.Length == 0
                       || setting.Key.Length == 0 || setting.Options.Length == 0
                       || setting.Options.All(option => option.Value != setting.Default)
                       || setting.Options.Any(option => option.Ini.IndexOfAny(['\r', '\n']) >= 0);
        }

        foreach (var family in tool.Families)
        {
            _ = new Regex(family.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        if (invalid)
        {
            throw new InvalidDataException("Invalid Game Tools definition: " + tool.Id);
        }

        DownloadClient.Https(tool.Source.BaseUrl);
    }

    private static bool SafeRelative(string path)
    {
        return path.Length > 0 && !Path.IsPathRooted(path)
                               && path.Split('/').All(segment => segment.Length > 0 && segment != ".." && segment != ".");
    }
}
