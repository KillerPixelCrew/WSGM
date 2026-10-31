namespace WSGM.Core;

/// <summary>An upscaler family a tool needs the game to offer.</summary>
internal sealed record GameToolUpscalerRequirement
{
    /// <summary>The family id the upscaler scan reports: <c>dlss</c>, <c>fsr</c> or <c>xess</c>.</summary>
    public string Family { get; init; } = "";

    /// <summary>The family shown to the user.</summary>
    public string Label { get; init; } = "";
}
