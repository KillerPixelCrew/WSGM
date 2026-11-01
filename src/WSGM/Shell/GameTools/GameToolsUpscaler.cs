namespace WSGM.Shell;

/// <summary>A standalone upscaler library the game ships.</summary>
/// <param name="Technology">The technology shown to the user.</param>
/// <param name="RelativePath">Forward-slash path relative to the install folder.</param>
/// <param name="Version">The file version, or null.</param>
internal sealed record GameToolsUpscaler(string Technology, string RelativePath, string? Version);
