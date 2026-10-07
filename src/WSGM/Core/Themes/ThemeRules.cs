using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Normalizes the stored themes configuration in place before consumers use it.</summary>
internal static class ThemeRules
{
    /// <summary>Repairs the themes section: a known translation branch and a clean hidden list.</summary>
    /// <param name="themes">The section.</param>
    /// <returns>An empty diagnostic list; these repairs do not produce warning entries.</returns>
    internal static IReadOnlyList<string> Normalize(ThemesConfig themes)
    {
        themes.TranslationsBranch = themes.TranslationsBranch?.Trim().ToLowerInvariant() switch
        {
            ThemeTranslationBranch.Stable => ThemeTranslationBranch.Stable,
            ThemeTranslationBranch.Beta => ThemeTranslationBranch.Beta,
            _ => ThemeTranslationBranch.Auto
        };
        themes.HiddenThemes =
        [
            .. (themes.HiddenThemes ?? [])
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
        ];
        return [];
    }
}
