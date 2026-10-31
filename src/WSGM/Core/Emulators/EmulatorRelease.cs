namespace WSGM.Core;

internal sealed record EmulatorRelease(
    string Version,
    string Id,
    string NotesUrl,
    string Provider,
    EmulatorAsset Asset,
    string ExtractDirectory = "",
    string CoreIndexUrl = "",
    string CoreAssetsUrl = "");
