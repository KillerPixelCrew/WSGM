namespace WSGM.Shell;

/// <summary>One option of a setting.</summary>
/// <param name="Value">The value sent back in commands.</param>
/// <param name="Label">The label.</param>
internal sealed record GameToolsSettingOption(string Value, string Label);
