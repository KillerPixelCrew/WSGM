using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>An install request.</summary>
/// <param name="AppId">The game.</param>
/// <param name="ToolId">The tool.</param>
/// <param name="ProxyName">The proxy name, or null for the suggested one.</param>
/// <param name="Settings">Setting values by id; omitted ids use their defaults.</param>
/// <param name="AcknowledgeRisk">Whether the user accepted the anti-cheat warning.</param>
/// <param name="ReplaceExisting">Back up and replace an installation made outside WSGM.</param>
internal sealed record GameToolsInstallRequest(
    uint AppId,
    string ToolId,
    string? ProxyName,
    IReadOnlyDictionary<string, string> Settings,
    bool AcknowledgeRisk,
    bool ReplaceExisting);
