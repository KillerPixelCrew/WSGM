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
