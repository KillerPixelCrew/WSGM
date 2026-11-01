using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>An install request.</summary>
/// <param name="AppId">The game.</param>
/// <param name="ToolId">The tool.</param>
/// <param name="ProxyName">The proxy name, or null for the suggested one.</param>
/// <param name="Settings">Setting values by id; omitted ids use their defaults.</param>
/// <param name="AcknowledgeRisk">Whether the user accepted the anti-cheat warning.</param>
/// <param name="ReplaceExisting">Back up and replace an installation made outside WSGM.</param>
/// <param name="Channel">The release channel to install from, or null for the tool's default channel.</param>
/// <param name="Addons">Add-ons the user turned on or off by id; an omitted add-on follows its recommendation.</param>
internal sealed record GameToolsInstallRequest(
    uint AppId,
    string ToolId,
    string? ProxyName,
    IReadOnlyDictionary<string, string> Settings,
    bool AcknowledgeRisk,
    bool ReplaceExisting,
    string? Channel = null,
    IReadOnlyDictionary<string, bool>? Addons = null);
