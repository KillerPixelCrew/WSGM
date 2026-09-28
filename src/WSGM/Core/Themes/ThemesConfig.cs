using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Which class-translation table the themes use.</summary>
public static class ThemeTranslationBranch
{
    /// <summary>The beta table when Steam is on a beta branch, the stable one otherwise.</summary>
    public const string Auto = "auto";

    /// <summary>Always the stable table.</summary>
    public const string Stable = "stable";

    /// <summary>Always the beta table.</summary>
    public const string Beta = "beta";
}

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
}
