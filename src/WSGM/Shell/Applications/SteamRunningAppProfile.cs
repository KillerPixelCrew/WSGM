namespace WSGM.Shell;

/// <summary>Optional executable/profile resolution for one known Steam AppID.</summary>
/// <param name="ExecutablePath">The shortcut target, when Steam exposes one.</param>
/// <param name="RtssProfileName">The executable file name RTSS keys its profile on.</param>
/// <param name="Diagnostic">Why the resolution is partial, for the log.</param>
/// <param name="InstallFolder">
///     A store title's install folder. Steam never exposes a store title's executable, so the folder is
///     what the foreground pairing is validated against: only a process running from inside it may
///     become the game's RTSS profile.
/// </param>
internal sealed record SteamRunningAppProfile(
    string? ExecutablePath,
    string? RtssProfileName,
    string? Diagnostic,
    string? InstallFolder = null);
