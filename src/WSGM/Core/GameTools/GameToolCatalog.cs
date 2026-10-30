using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WSGM.Core.GameTools;

/// <summary>Where a tool's packages are published.</summary>
internal sealed record GameToolSource
{
    /// <summary>The reviewed hosted provider: <c>GitHub</c> or <c>Forgejo</c>.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The HTTPS API root.</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The <c>owner/name</c> repository.</summary>
    public string Repository { get; init; } = "";

    /// <summary>A pattern that selects exactly one asset per release.</summary>
    public string AssetPattern { get; init; } = "";

    /// <summary>The release channel; <c>stable</c> skips prereleases.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>The archive type the selected asset must have.</summary>
    public string PackageType { get; init; } = "";

    /// <summary>Builds the hosted source shape the shared release lookup consumes.</summary>
    /// <returns>An emulator-agnostic hosted source.</returns>
    internal EmulatorSource Hosted()
    {
        return new EmulatorSource
        {
            Provider = Provider, BaseUrl = BaseUrl, Repository = Repository, AssetPattern = AssetPattern
        };
    }
}

/// <summary>How a tool's own binaries identify themselves regardless of their file name.</summary>
internal sealed record GameToolIdentity
{
    /// <summary>The version resource OriginalFilename every copy of the tool carries.</summary>
    public string OriginalFilename { get; init; } = "";

    /// <summary>Tokens that make a file another product even if its OriginalFilename matches.</summary>
    public string[] VetoTokens { get; init; } = [];

    /// <summary>Other mods recognised by a token in their version resource, for conflict messages.</summary>
    public GameToolOtherTool[] OtherTools { get; init; } = [];
}

/// <summary>A known other mod recognised from its version resource.</summary>
internal sealed record GameToolOtherTool
{
    /// <summary>The lowercase token searched for in the seven identity fields.</summary>
    public string Token { get; init; } = "";

    /// <summary>The product name shown to the user.</summary>
    public string Name { get; init; } = "";
}

/// <summary>A name the primary file may be installed as.</summary>
internal sealed record GameToolProxyOption
{
    /// <summary>The file name in the target directory.</summary>
    public string Name { get; init; } = "";

    /// <summary>A short explanation of when to choose it.</summary>
    public string Hint { get; init; } = "";
}

/// <summary>Proxy names the tool installs under and recognises.</summary>
internal sealed record GameToolProxy
{
    /// <summary>The preferred install name.</summary>
    public string Default { get; init; } = "";

    /// <summary>Names offered for installation, in order.</summary>
    public GameToolProxyOption[] Options { get; init; } = [];

    /// <summary>Exact names probed for an existing copy, including names never offered.</summary>
    public string[] DetectionNames { get; init; } = [];

    /// <summary>Relative patterns whose identity-matched files also count as existing copies.</summary>
    public string[] DetectionPatterns { get; init; } = [];
}

/// <summary>Required payload contents and copy exclusions.</summary>
internal sealed record GameToolPayload
{
    /// <summary>Files required at the payload root.</summary>
    public string[] Required { get; init; } = [];

    /// <summary>Directories additionally required by a version family.</summary>
    public Dictionary<string, string[]> FamilyDirectories { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Upstream files never copied into a game, matched with DOS wildcards.</summary>
    public string[] Exclude { get; init; } = [];

    /// <summary>Files an exclusion can never remove.</summary>
    public string[] NeverExclude { get; init; } = [];
}

/// <summary>A version family recognised from the first numeric token of a version string.</summary>
internal sealed record GameToolFamily
{
    /// <summary>The family identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The pattern matched against the numeric version token.</summary>
    public string Pattern { get; init; } = "";
}

/// <summary>Channel tokens recognised in a product version.</summary>
internal sealed record GameToolChannels
{
    /// <summary>Tokens meaning preview builds.</summary>
    public string[] Preview { get; init; } = [];

    /// <summary>Tokens meaning stable builds.</summary>
    public string[] Stable { get; init; } = [];
}

/// <summary>One option of a data-driven setting.</summary>
internal sealed record GameToolSettingOption
{
    /// <summary>The value WSGM's surfaces send.</summary>
    public string Value { get; init; } = "";

    /// <summary>The label shown to the user.</summary>
    public string Label { get; init; } = "";

    /// <summary>The raw INI value written for this option.</summary>
    public string Ini { get; init; } = "";
}

/// <summary>A setting mapped onto an existing key of the tool's configuration file.</summary>
internal sealed record GameToolSetting
{
    /// <summary>The setting identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The label shown to the user.</summary>
    public string Label { get; init; } = "";

    /// <summary>A short explanation.</summary>
    public string Description { get; init; } = "";

    /// <summary>The INI section, matched case-insensitively.</summary>
    public string Section { get; init; } = "";

    /// <summary>The INI key inside that section.</summary>
    public string Key { get; init; } = "";

    /// <summary>The default option value.</summary>
    public string Default { get; init; } = "";

    /// <summary>The selectable options.</summary>
    public GameToolSettingOption[] Options { get; init; } = [];

    /// <summary>Notes keyed by lowercase GPU vendor (<c>nvidia</c>, <c>amd</c>, <c>intel</c>).</summary>
    public Dictionary<string, string> VendorNotes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One data-driven tool that can be installed into a game folder.</summary>
internal sealed record GameToolDefinition
{
    /// <summary>The stable identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The product name.</summary>
    public string Name { get; init; } = "";

    /// <summary>What the tool does.</summary>
    public string Description { get; init; } = "";

    /// <summary>The upstream project page.</summary>
    public string Homepage { get; init; } = "";

    /// <summary>The upstream license identifier.</summary>
    public string License { get; init; } = "";

    /// <summary>The affiliation notice.</summary>
    public string Notice { get; init; } = "";

    /// <summary>The warning shown for games with anti-cheat.</summary>
    public string AntiCheatWarning { get; init; } = "";

    /// <summary>Where packages come from.</summary>
    public GameToolSource Source { get; init; } = new();

    /// <summary>Game architectures the tool supports.</summary>
    public string[] Architectures { get; init; } = [];

    /// <summary>The payload file installed under the proxy name.</summary>
    public string PrimaryFile { get; init; } = "";

    /// <summary>How the tool's binaries identify themselves.</summary>
    public GameToolIdentity Identity { get; init; } = new();

    /// <summary>Install and detection names.</summary>
    public GameToolProxy Proxy { get; init; } = new();

    /// <summary>Required contents and exclusions.</summary>
    public GameToolPayload Payload { get; init; } = new();

    /// <summary>Configuration files: preserved on update, edited by the tool itself, removed on uninstall.</summary>
    public string[] ConfigFiles { get; init; } = [];

    /// <summary>Game-shipped files that may be backed up and replaced.</summary>
    public string[] Replaceable { get; init; } = [];

    /// <summary>Files a manual install leaves behind, removed by "Replace existing installation".</summary>
    public string[] LegacyFiles { get; init; } = [];

    /// <summary>Files the tool writes while running, removed on uninstall when present.</summary>
    public string[] RuntimeArtifacts { get; init; } = [];

    /// <summary>Upscaler families the game must already support.</summary>
    public string[] RequiresUpscaler { get; init; } = [];

    /// <summary>Version families.</summary>
    public GameToolFamily[] Families { get; init; } = [];

    /// <summary>Channel tokens.</summary>
    public GameToolChannels Channels { get; init; } = new();

    /// <summary>Settings mapped onto configuration keys.</summary>
    public GameToolSetting[] Settings { get; init; } = [];

    /// <summary>The primary configuration file, or an empty string when the tool has none.</summary>
    internal string PrimaryConfig => ConfigFiles.FirstOrDefault() ?? "";

    /// <summary>Whether a relative path is a configuration file.</summary>
    /// <param name="relativePath">A path relative to the target directory with forward slashes.</param>
    /// <returns>True for a configured configuration file.</returns>
    internal bool IsConfig(string relativePath)
    {
        return ConfigFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a payload-relative path is excluded from copying.</summary>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>True when an exclusion matches and the file is not required.</returns>
    internal bool IsExcluded(string relativePath)
    {
        if (Payload.NeverExclude.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
            || relativePath.Equals(PrimaryFile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return GameToolPattern.MatchesAny(Payload.Exclude, relativePath);
    }

    /// <summary>Whether a game-shipped file may be backed up and replaced.</summary>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>True when a replaceable pattern matches.</returns>
    internal bool IsReplaceable(string relativePath)
    {
        return GameToolPattern.MatchesAny(Replaceable, relativePath);
    }
}

/// <summary>DOS wildcard matching over forward-slash relative paths.</summary>
internal static class GameToolPattern
{
    /// <summary>Matches one pattern.</summary>
    /// <param name="pattern">A pattern containing <c>/</c> matches the relative path only; others also match the name.</param>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>Whether it matches, ignoring case.</returns>
    internal static bool Matches(string pattern, string relativePath)
    {
        if (pattern.Contains('/'))
        {
            return FileSystemName.MatchesSimpleExpression(pattern, relativePath);
        }

        return FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(relativePath))
               || FileSystemName.MatchesSimpleExpression(pattern, relativePath);
    }

    /// <summary>Matches any of several patterns.</summary>
    /// <param name="patterns">The patterns.</param>
    /// <param name="relativePath">The forward-slash relative path.</param>
    /// <returns>Whether one matches.</returns>
    internal static bool MatchesAny(IEnumerable<string> patterns, string relativePath)
    {
        return patterns.Any(pattern => Matches(pattern, relativePath));
    }

    /// <summary>Converts an OS relative path to the catalog's forward-slash form.</summary>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The normalized path.</returns>
    internal static string Normalize(string relativePath)
    {
        return relativePath.Replace('\\', '/').TrimStart('/');
    }
}

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
                      || tool.Proxy.Options.Any(option => !SafeName(option.Name))
                      || tool.Identity.OriginalFilename.Length == 0
                      || tool.Source.Provider is not ("GitHub" or "Forgejo")
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

        EmulatorNetwork.Https(tool.Source.BaseUrl);
    }

    private static bool SafeName(string name)
    {
        return name.Length > 0 && Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool SafeRelative(string path)
    {
        return path.Length > 0 && !Path.IsPathRooted(path)
                               && path.Split('/').All(segment => segment.Length > 0 && segment != ".." && segment != ".");
    }
}
