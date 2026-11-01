using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One data-driven setting.</summary>
/// <param name="Id">The setting id.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">A short explanation.</param>
/// <param name="Value">The selected option value; empty when the file holds a value no option matches.</param>
/// <param name="IniValue">The raw value in the configuration file, or null when not installed or missing.</param>
/// <param name="Options">The selectable options.</param>
/// <param name="Available">False when the installed version's configuration file has no such key.</param>
/// <param name="Note">A vendor-specific or availability note, or null.</param>
internal sealed record GameToolsSetting(
    string Id,
    string Label,
    string Description,
    string Value,
    string? IniValue,
    IReadOnlyList<GameToolsSettingOption> Options,
    bool Available,
    string? Note);
