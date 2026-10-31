using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Everything the UI sounds page and the Customization hub draw.</summary>
/// <param name="Selected">The selected pack's id, empty for Steam's own sounds.</param>
/// <param name="Packs">The installed packs.</param>
/// <param name="Browse">The store page last asked for.</param>
/// <param name="Busy">Whether an operation is running.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Compatibility">What the selection does to Steam's sounds.</param>
/// <param name="Integration">What Steam's sound integration is doing.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamSoundsState(
    string Selected,
    IReadOnlyList<SteamSoundsPack> Packs,
    SteamSoundsBrowse Browse,
    bool Busy,
    string? Error,
    string Compatibility,
    string Integration,
    long Revision);
