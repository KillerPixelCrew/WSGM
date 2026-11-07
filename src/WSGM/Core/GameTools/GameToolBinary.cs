namespace WSGM.Core;

/// <summary>Read-only identification evidence for a game-local DLL; identification never grants ownership.</summary>
internal sealed record GameToolBinary(
    string RelativePath,
    string ToolId,
    string Name,
    string Architecture,
    string[] Pipelines,
    string Version,
    string Evidence);
