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

    /// <summary>The release channels, the default first.</summary>
    public GameToolChannel[] Channels { get; init; } = [];

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

    /// <summary>Optional plugins the tool loads, offered for the games their publishers support.</summary>
    public GameToolAddon[] Addons { get; init; } = [];

    /// <summary>Architecture-specific local injection modes, keyed by proxy name.</summary>
    public GameToolMode[] Modes { get; init; } = [];

    /// <summary>The channel a game uses until the user chooses another.</summary>
    internal GameToolChannel DefaultChannel => Channels[0];

    /// <summary>The primary configuration file, or an empty string when the tool has none.</summary>
    internal string PrimaryConfig => ConfigFiles.FirstOrDefault() ?? "";

    /// <summary>Finds a channel by id.</summary>
    /// <param name="id">The channel id.</param>
    /// <returns>The channel, or null when the tool has no such channel.</returns>
    internal GameToolChannel? Channel(string? id)
    {
        return Channels.FirstOrDefault(channel => channel.Id == id);
    }

    /// <summary>Finds an add-on by id.</summary>
    /// <param name="id">The add-on id.</param>
    /// <returns>The add-on, or null when the tool has no such add-on.</returns>
    internal GameToolAddon? Addon(string? id)
    {
        return Addons.FirstOrDefault(addon => addon.Id == id);
    }

    /// <summary>The channel a manifest was installed from; an unknown or empty id is the default channel.</summary>
    /// <param name="id">The recorded channel id.</param>
    /// <returns>The channel.</returns>
    internal GameToolChannel ChannelOrDefault(string? id)
    {
        return Channel(id) ?? DefaultChannel;
    }

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
