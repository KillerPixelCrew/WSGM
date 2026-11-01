using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>What the UI shows of an installed theme.</summary>
/// <param name="Id">The store id, or the name.</param>
/// <param name="Name">The identity among installed themes.</param>
/// <param name="DisplayName">The name the user sees.</param>
/// <param name="Version">The version text.</param>
/// <param name="Author">The author text.</param>
/// <param name="Enabled">Whether it is on.</param>
/// <param name="Patches">Its patches.</param>
/// <param name="Require">The manifest version it was written for.</param>
/// <param name="Dependencies">The names of the themes it needs.</param>
/// <param name="Flags">Its flags.</param>
/// <param name="Created">When its manifest was last written.</param>
/// <param name="Modified">When its saved state was last written.</param>
public sealed record ThemeSnapshot(
    string Id,
    string Name,
    string DisplayName,
    string Version,
    string Author,
    bool Enabled,
    IReadOnlyList<ThemePatchSnapshot> Patches,
    int Require,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Flags,
    DateTime? Created,
    DateTime? Modified)
{
    /// <summary>Whether the theme is a profile.</summary>
    public bool IsPreset => Flags.Contains(ThemeFlags.Preset);
}
