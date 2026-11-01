namespace WSGM.Core;

/// <summary>A version family recognised from the first numeric token of a version string.</summary>
internal sealed record GameToolFamily
{
    /// <summary>The family identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The pattern matched against the numeric version token.</summary>
    public string Pattern { get; init; } = "";
}
