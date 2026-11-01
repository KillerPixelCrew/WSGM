using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The Steam themes: CSSLoader-compatible themes WSGM installs into Big Picture.</summary>
/// <remarks>
///     Persisted as <see cref="AppConfig.Themes" />. Which themes are on and what their patches are
///     set to live beside each theme, as CSS Loader keeps them; this holds only what is WSGM's own.
/// </remarks>
public sealed class ThemesConfig
{
    /// <summary>Whether enabled themes are installed into Steam at all. Off leaves Steam's own styling.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which class-translation table is fetched: one of <see cref="ThemeTranslationBranch" />.</summary>
    public string TranslationsBranch { get; set; } = ThemeTranslationBranch.Auto;

    /// <summary>Themes hidden from the Quick Access section, by name. They stay on the page and in the overlay.</summary>
    public List<string> HiddenThemes { get; set; } = [];

    /// <summary>A copy, for a service that shows a change before the saved configuration reaches it again.</summary>
    /// <returns>The copy.</returns>
    public ThemesConfig Clone()
    {
        return new ThemesConfig
        {
            Enabled = Enabled,
            TranslationsBranch = TranslationsBranch,
            HiddenThemes = [.. HiddenThemes]
        };
    }
}
