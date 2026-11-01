namespace WSGM.Core;

/// <summary>A known other mod recognised from its version resource.</summary>
internal sealed record GameToolOtherTool
{
    /// <summary>The lowercase token searched for in the seven identity fields.</summary>
    public string Token { get; init; } = "";

    /// <summary>The product name shown to the user.</summary>
    public string Name { get; init; } = "";
}
