using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Every candidate for one title's artwork type.</summary>
/// <param name="Asset">The artwork type.</param>
/// <param name="Status">Whether gathering is still going on, and how it ended.</param>
/// <param name="Detail">Why it failed or is unavailable, or empty.</param>
/// <param name="Selected">The position of the image the slot shows, from one, or zero.</param>
/// <param name="Options">The candidates.</param>
internal sealed record GameLibraryOptionsAnswer(
    string Asset,
    string Status,
    string Detail,
    int Selected,
    IReadOnlyList<GameLibraryArtworkOption> Options);
