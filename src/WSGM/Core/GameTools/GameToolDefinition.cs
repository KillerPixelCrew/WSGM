using System;
using System.Linq;

namespace WSGM.Core;

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

    /// <summary>Upscaler families the game must already support, any one of which is enough.</summary>
    public GameToolUpscalerRequirement[] RequiresUpscaler { get; init; } = [];

    /// <summary>Version families.</summary>
    public GameToolFamily[] Families { get; init; } = [];

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
