using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One tool's state for one game.</summary>
/// <param name="Id">The tool id.</param>
/// <param name="Name">The product name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Homepage">The upstream project page.</param>
/// <param name="License">The upstream license identifier.</param>
/// <param name="Notice">The affiliation notice.</param>
/// <param name="IncompatibleReason">Why it can't be installed for the chosen runtime, or null when it can.</param>
/// <param name="Status"><c>notInstalled</c>, <c>installed</c>, <c>updateAvailable</c>, <c>modified</c>, <c>conflict</c> or <c>unavailable</c>.</param>
/// <param name="StatusText">A short user-facing status line.</param>
/// <param name="InstalledVersion">The installed product version, or null.</param>
/// <param name="LatestVersion">The latest stable release tag, or null when unknown.</param>
/// <param name="LatestError">Why the latest release is unknown, or null.</param>
/// <param name="CheckingLatest">Whether a release lookup is running.</param>
/// <param name="UpdateChangesConfig">Whether the available update changes the configuration format.</param>
/// <param name="ProxyName">The installed proxy name, or the suggested one when not installed.</param>
/// <param name="ProxyIntro">The text introducing the file name choice: before installation, or for a rename.</param>
/// <param name="ProxyOptions">Every offered proxy name.</param>
/// <param name="Settings">The tool's settings.</param>
/// <param name="ShowOptions">Whether the file name and settings are shown.</param>
/// <param name="CanEditOptions">Whether the file name and settings can be changed now.</param>
/// <param name="Conflicts">Files that block installation or need review.</param>
/// <param name="Warnings">Things the user should know before acting; the anti-cheat warning is game-level.</param>
/// <param name="Actions">Commands currently valid for this tool.</param>
/// <param name="Files">The ownership summary.</param>
internal sealed record GameToolsTool(
    string Id,
    string Name,
    string Description,
    string Homepage,
    string License,
    string Notice,
    string? IncompatibleReason,
    string Status,
    string StatusText,
    string? InstalledVersion,
    string? LatestVersion,
    string? LatestError,
    bool CheckingLatest,
    bool UpdateChangesConfig,
    string ProxyName,
    string ProxyIntro,
    IReadOnlyList<GameToolsProxyOption> ProxyOptions,
    IReadOnlyList<GameToolsSetting> Settings,
    bool ShowOptions,
    bool CanEditOptions,
    IReadOnlyList<GameToolsConflict> Conflicts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Actions,
    GameToolsFiles Files);
