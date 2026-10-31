using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace WSGM.Core;

/// <summary>Everything a theme's manifest declares.</summary>
/// <param name="Name">The theme's identity among installed themes.</param>
/// <param name="DisplayName">What the user sees, or null to show the name.</param>
/// <param name="Id">The store id, or the name when the manifest has none.</param>
/// <param name="Version">The version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Require">The manifest version the theme was written for.</param>
/// <param name="Flags">The theme's flags, upper-cased.</param>
/// <param name="TabMappings">The theme's own tab aliases.</param>
/// <param name="Dependencies">
///     Themes this one needs, each with the patch values it sets on them: an option, or an option
///     with its components' values, as the manifest wrote it.
/// </param>
/// <param name="Injects">What the theme always injects while enabled.</param>
/// <param name="Patches">The theme's patches.</param>
public sealed record ThemeManifestData(
    string Name,
    string? DisplayName,
    string Id,
    string Version,
    string Author,
    int Require,
    IReadOnlyList<string> Flags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TabMappings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode>> Dependencies,
    IReadOnlyList<ThemeInjectManifest> Injects,
    IReadOnlyList<ThemePatchManifest> Patches);
