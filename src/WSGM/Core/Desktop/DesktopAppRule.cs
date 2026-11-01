namespace WSGM.Core;

/// <summary>A known desktop integration and its application-specific exit protocol.</summary>
/// <param name="Name">Diagnostic product label.</param>
/// <param name="ProcessNames">Primary process names without extensions; never service/helper sweep patterns.</param>
/// <param name="ExitCommand">Sibling executable implementing orderly exit, or null when no such command exists.</param>
/// <param name="ExitArguments">Arguments passed to the exit command.</param>
/// <param name="RestartArguments">Arguments for restoring the captured executable.</param>
/// <param name="CloseMainWindowFirst">Whether the editor window must close before the remaining exit protocol.</param>
/// <param name="CreateNoWindow">Whether restoration should create a hidden process without shell activation.</param>
/// <param name="ExitWindowClass">Optional owned window class receiving WM_CLOSE instead of process termination.</param>
internal sealed record DesktopAppRule(
    string Name,
    string[] ProcessNames,
    string? ExitCommand,
    string ExitArguments,
    string RestartArguments,
    bool CloseMainWindowFirst = false,
    bool CreateNoWindow = false,
    string? ExitWindowClass = null);
