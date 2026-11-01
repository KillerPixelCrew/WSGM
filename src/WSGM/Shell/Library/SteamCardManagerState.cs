using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Everything the SD Card Manager draws.</summary>
/// <param name="Cards">Every tracked card, inserted or remembered.</param>
/// <param name="Loaded">Whether a drive scan has completed since the service started.</param>
/// <param name="Busy">Whether a scan or a change is in flight.</param>
/// <param name="Error">Why the last operation failed, or null.</param>
/// <param name="Notice">Something the user should read, such as a partial rename, or null.</param>
/// <param name="Games">The games of the card last inspected, or null.</param>
/// <param name="Revision">Monotonic observation revision, shared with the tab manager.</param>
internal sealed record SteamCardManagerState(
    IReadOnlyList<LibraryCardView> Cards,
    bool Loaded,
    bool Busy,
    string? Error,
    string? Notice,
    LibraryCardGames? Games,
    long Revision);
