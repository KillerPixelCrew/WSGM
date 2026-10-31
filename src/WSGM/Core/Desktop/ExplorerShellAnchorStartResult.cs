namespace WSGM.Core;

/// <summary>Result of starting and authenticating a shell anchor.</summary>
/// <param name="Anchor">New caller-owned authenticated anchor, or null when startup failed.</param>
/// <param name="Error">Failure detail; empty on successful startup.</param>
internal readonly record struct ExplorerShellAnchorStartResult(
    ExplorerShellAnchor? Anchor,
    string Error);
