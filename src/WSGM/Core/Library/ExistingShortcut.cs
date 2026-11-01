namespace WSGM.Core;

/// <summary>An existing non-Steam shortcut, as Steam reports it.</summary>
/// <param name="AppId">Its generated id.</param>
/// <param name="Target">What it runs.</param>
/// <param name="LaunchOptions">Its arguments.</param>
/// <param name="StartDirectory">Its authored working directory.</param>
/// <param name="Name">Its current Steam display name.</param>
public sealed record ExistingShortcut(
    uint AppId,
    string Target,
    string LaunchOptions,
    string StartDirectory = "",
    string Name = "");
