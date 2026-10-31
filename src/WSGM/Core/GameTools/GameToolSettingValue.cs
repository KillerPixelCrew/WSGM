namespace WSGM.Core;

/// <summary>A setting's displayed value for one game.</summary>
/// <param name="Value">The selected option value; empty when the file holds a value no option matches.</param>
/// <param name="IniValue">The raw value in the configuration file, or null when not installed or missing.</param>
/// <param name="Available">False when the installed version's configuration file has no such key.</param>
/// <param name="Note">A vendor-specific or availability note, or null.</param>
internal sealed record GameToolSettingValue(string Value, string? IniValue, bool Available, string? Note);
