using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal static class ThemeRules
{
    /// <summary>Repairs the themes section: a known translation branch and a clean hidden list.</summary>
    /// <param name="themes">The section.</param>
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
