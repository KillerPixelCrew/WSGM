namespace WSGM.Core;

/// <summary>A standalone upscaler or frame-generation library found in a game folder.</summary>
/// <param name="Technology">The technology shown to the user, such as <c>DLSS</c>.</param>
/// <param name="Family">The input family a tool may require: <c>dlss</c>, <c>fsr</c>, <c>xess</c> or <c>other</c>.</param>
/// <param name="Kind"><c>upscaler</c>, <c>frameGeneration</c> or <c>other</c>.</param>
/// <param name="RelativePath">The forward-slash path relative to the install root.</param>
/// <param name="Version">The file's product or file version, or null.</param>
internal sealed record GameUpscalerFile(string Technology, string Family, string Kind, string RelativePath,
    string? Version);
