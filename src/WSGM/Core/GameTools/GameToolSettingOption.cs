namespace WSGM.Core;

/// <summary>One option of a data-driven setting.</summary>
internal sealed record GameToolSettingOption
{
    public System.Collections.Generic.Dictionary<string, string> AdditionalKeys { get; init; } = [];
    /// <summary>The value WSGM's surfaces send.</summary>
    public string Value { get; init; } = "";

    /// <summary>The label shown to the user.</summary>
    public string Label { get; init; } = "";

    /// <summary>The raw INI value written for this option.</summary>
    public string Ini { get; init; } = "";
}
