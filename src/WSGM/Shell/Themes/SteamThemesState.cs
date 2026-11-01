using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The whole model behind the Themes page, the Quick Access section and the overlay.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>installed</c>, <c>profiles</c> or <c>settings</c>.</param>
/// <param name="Themes">The installed themes that are not profiles, by display name.</param>
/// <param name="Presets">The profiles.</param>
/// <param name="SelectedPreset">The enabled profile's name, or empty.</param>
/// <param name="Browse">The Browse tab.</param>
/// <param name="Detail">The theme opened from the store, or null.</param>
/// <param name="Settings">The Settings tab.</param>
/// <param name="Errors">The folders the loader refused.</param>
/// <param name="Busy">Whether an install, update or reload is running.</param>
/// <param name="Notice">Something the user should read, or null.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Updates">How many installed themes the store has a newer version of.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamThemesState(
    string ActiveTab,
    IReadOnlyList<SteamThemesInstalled> Themes,
    IReadOnlyList<SteamThemesInstalled> Presets,
    string SelectedPreset,
    SteamThemesBrowse Browse,
    SteamThemesDetail? Detail,
    SteamThemesSettings Settings,
    IReadOnlyList<ThemeLoadError> Errors,
    bool Busy,
    string? Notice,
    string? Error,
    int Updates,
    long Revision);
