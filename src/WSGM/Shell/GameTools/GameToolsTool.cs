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
/// <param name="Status">
///     <c>notInstalled</c>, <c>installed</c>, <c>updateAvailable</c>, <c>modified</c>, <c>conflict</c> or
///     <c>unavailable</c>.
/// </param>
/// <param name="StatusText">A short user-facing status line.</param>
/// <param name="InstalledVersion">The installed release's version, such as <c>0.9.4</c>, or null.</param>
/// <param name="InstalledLabel">
///     The installed version as the user reads it, with the channel and build date for a dated build such as
///     <c>10.0.0-pre1 · nightly 31 Oct 2026</c>; null when not installed.
/// </param>
/// <param name="Channel">The channel the installation follows, or the default channel before installation.</param>
/// <param name="Channels">Every release channel with its latest release, the default first.</param>
/// <param name="LatestVersion">The latest release of <paramref name="Channel" />, or null when unknown.</param>
/// <param name="LatestLabel">That release as the user reads it, or null when unknown.</param>
/// <param name="LatestError">Why the latest release of <paramref name="Channel" /> is unknown, or null.</param>
/// <param name="CheckingLatest">Whether a release lookup of any channel is running.</param>
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
/// <param name="Addons">The add-ons a page shows for this game, already filtered by support.</param>
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
    string? InstalledLabel,
    string Channel,
    IReadOnlyList<GameToolsChannel> Channels,
    string? LatestVersion,
    string? LatestLabel,
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
    GameToolsFiles Files,
    IReadOnlyList<GameToolsAddon> Addons)
{
    /// <summary>Whether files are active in this game's persisted profile.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the proxy chooses a distinct API-specific payload.</summary>
    public bool ModeBased { get; init; }
}
