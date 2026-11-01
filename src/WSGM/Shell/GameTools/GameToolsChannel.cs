namespace WSGM.Shell;

/// <summary>One release channel of a tool, as a game's page offers it.</summary>
/// <param name="Id">The channel id sent back in <c>install</c> and <c>switchChannel</c>: <c>stable</c> or <c>nightly</c>.</param>
/// <param name="Label">The name shown to the user.</param>
/// <param name="Description">What the channel offers, empty for none.</param>
/// <param name="Current">Whether the installed tool follows this channel; before installation, the default channel.</param>
/// <param name="LatestVersion">The channel's latest version, or null when unknown.</param>
/// <param name="LatestLabel">
///     The latest version as the user reads it, such as <c>10.0.0-pre1 · nightly 31 Oct 2026</c>, or null when unknown.
/// </param>
/// <param name="BuildDate">The latest build's date as <c>yyyy-MM-dd</c> when the channel dates its builds, else null.</param>
/// <param name="LatestError">Why the latest release is unknown, or null.</param>
/// <param name="CheckingLatest">Whether this channel's release lookup is running.</param>
/// <param name="ChangesConfig">
///     Whether installing this channel's latest release changes the installed configuration format, so the user
///     must confirm <c>replaceConfig</c>.
/// </param>
internal sealed record GameToolsChannel(
    string Id,
    string Label,
    string Description,
    bool Current,
    string? LatestVersion,
    string? LatestLabel,
    string? BuildDate,
    string? LatestError,
    bool CheckingLatest,
    bool ChangesConfig);
