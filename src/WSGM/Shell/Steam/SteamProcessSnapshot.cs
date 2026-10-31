namespace WSGM.Shell;

/// <summary>One look at Steam's processes and its Big Picture window.</summary>
/// <param name="Running">Whether steam.exe or a steamwebhelper runs in this session.</param>
/// <param name="BrowserProcessId">
///     steamwebhelper's browser process, the one that opens the debugging port; an unreadable helper
///     when no browser process could be told from its children; zero while there is no helper.
/// </param>
/// <param name="BrowserDebugging">
///     Whether that browser process was started with <c>--remote-debugging-port</c>; null while there is
///     no browser process or its command line could not be read.
/// </param>
/// <param name="BigPictureWindow">Whether Steam's Big Picture window exists.</param>
internal readonly record struct SteamProcessSnapshot(
    bool Running,
    uint BrowserProcessId,
    bool? BrowserDebugging,
    bool BigPictureWindow);
