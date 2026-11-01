namespace WSGM.Core;

/// <summary>Steam-native fields for a custom launch action.</summary>
/// <param name="LaunchOptions">Launch Options for a regular Steam title.</param>
/// <param name="ShortcutTarget">Target for a non-Steam shortcut.</param>
/// <param name="ShortcutArguments">Launch Arguments for a non-Steam shortcut.</param>
internal readonly record struct SteamCustomLaunchFields(
    string LaunchOptions,
    string ShortcutTarget,
    string ShortcutArguments);
