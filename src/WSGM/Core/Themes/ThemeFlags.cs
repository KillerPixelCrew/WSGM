namespace WSGM.Core;

/// <summary>The flags a <c>theme.json</c> may carry, upper-cased as CSS Loader reads them.</summary>
public static class ThemeFlags
{
    /// <summary>The theme is a profile: a generated set of dependencies and their patch values.</summary>
    public const string Preset = "PRESET";

    /// <summary>Disabling the theme leaves its dependencies enabled.</summary>
    public const string KeepDependencies = "KEEP_DEPENDENCIES";

    /// <summary>Enabling the theme asks whether to enable its dependencies.</summary>
    public const string OptionalDependencies = "OPTIONAL_DEPENDENCIES";

    /// <summary>The theme wants CSS Loader's navigation patch, which WSGM does not have.</summary>
    public const string RequireNavPatch = "REQUIRE_NAV_PATCH";
}
