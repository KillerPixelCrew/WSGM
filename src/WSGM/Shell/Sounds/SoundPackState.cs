using WSGM.Core;

namespace WSGM.Shell;

internal sealed record SoundPackState(
    SoundPack[] Packs,
    string Selected,
    bool Busy,
    string? Error,
    string Compatibility,
    ThemeStoreSummary[] Listings,
    int Total,
    int Page)
{
    internal string Integration { get; init; } = "Waiting for Steam sound integration.";

    /// <summary>The search the store was last asked with, which the next page continues.</summary>
    internal string Search { get; init; } = "";
}
