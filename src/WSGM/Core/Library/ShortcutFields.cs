namespace WSGM.Core;

/// <summary>What a generated non-Steam shortcut points at.</summary>
/// <param name="Target">The Target Steam stores, quoted when it needs to be.</param>
/// <param name="StartDirectory">The working directory Steam stores.</param>
/// <param name="LaunchOptions">The Launch Arguments Steam stores.</param>
public sealed record ShortcutFields(
    string Target,
    string StartDirectory,
    string LaunchOptions);
