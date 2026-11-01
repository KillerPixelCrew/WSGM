using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One installed theme as the Themes page and the overlay show it.</summary>
/// <param name="Id">The store id, or the name.</param>
/// <param name="Name">The identity among installed themes, sent back with every command.</param>
/// <param name="DisplayName">The name the user sees.</param>
/// <param name="Version">The installed version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="Hidden">Whether it is kept off the Quick Access section.</param>
/// <param name="Status">
///     <c>installed</c>, <c>outdated</c>, <c>local</c> (the store does not list it) or <c>unknown</c>
///     (not checked yet).
/// </param>
/// <param name="LatestVersion">The store's version when it differs, or null.</param>
/// <param name="Patches">Its patches and their values.</param>
/// <param name="Dependencies">The names of the themes it needs.</param>
internal sealed record SteamThemesInstalled(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Author,
    bool Enabled,
    bool Hidden,
    string Status,
    string? LatestVersion,
    IReadOnlyList<ThemePatchSnapshot> Patches,
    IReadOnlyList<string> Dependencies);
