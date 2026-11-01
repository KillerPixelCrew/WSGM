namespace WSGM.Shell;

/// <summary>The Settings tab.</summary>
/// <param name="Enabled">Whether enabled themes are installed into Steam.</param>
/// <param name="TranslationsBranch">Which translation table is fetched.</param>
/// <param name="SteamBeta">Whether Steam is on a beta branch.</param>
/// <param name="Translations">How many class names the table maps.</param>
/// <param name="TranslationsFetched">When the table was last fetched, or null.</param>
/// <param name="ThemesPath">The themes folder.</param>
/// <param name="SteamLink">What became of Steam's <c>themes_custom</c> link.</param>
internal sealed record SteamThemesSettings(
    bool Enabled,
    string TranslationsBranch,
    bool SteamBeta,
    int Translations,
    string? TranslationsFetched,
    string ThemesPath,
    string SteamLink);
