namespace WSGM.Core;

internal sealed record EmulatorRelease(
    string Version,
    string Id,
    string NotesUrl,
    string Provider,
    ReleaseAsset Asset,
    string ExtractDirectory = "",
    string CoreIndexUrl = "",
    string CoreAssetsUrl = "");
