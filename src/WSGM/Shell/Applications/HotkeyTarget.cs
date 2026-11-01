namespace WSGM.Shell;

/// <summary>A window a hotkey may be sent to: an application's window, never WSGM's, Steam's or the shell's.</summary>
/// <param name="Window">The window.</param>
/// <param name="ProcessId">Its process.</param>
/// <param name="Executable">Its executable's file name, for the log and the notice.</param>
internal sealed record HotkeyTarget(nint Window, uint ProcessId, string Executable);
