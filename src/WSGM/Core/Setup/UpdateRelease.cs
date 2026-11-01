namespace WSGM.Core;

/// <summary>A published WSGM release and the files the updater needs from it.</summary>
/// <param name="Version">The release version, without the tag's <c>v</c>.</param>
/// <param name="PageUrl">The release page, for the notes.</param>
/// <param name="SetupName">The setup's file name.</param>
/// <param name="SetupUrl">Where to download the setup.</param>
/// <param name="HashUrl">Where to download the setup's SHA-256 file.</param>
/// <param name="BundleUrl">Where to download the release's bundle manifest, when it has one.</param>
public sealed record UpdateRelease(
    string Version,
    string PageUrl,
    string SetupName,
    string SetupUrl,
    string HashUrl,
    string? BundleUrl);
