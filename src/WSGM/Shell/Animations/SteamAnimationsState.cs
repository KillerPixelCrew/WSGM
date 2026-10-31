using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Everything the Animations page, the Quick Access section and the overlay draw.</summary>
/// <param name="ActiveTab"><c>browse</c>, <c>library</c> or <c>settings</c>.</param>
/// <param name="Selected">The library id Big Picture starts with, empty for Steam's own movie.</param>
/// <param name="StockName">What the empty choice is called.</param>
/// <param name="Library">The library, downloads first.</param>
/// <param name="Browse">The repository's list.</param>
/// <param name="Detail">The movie opened for a closer look, or null.</param>
/// <param name="Settings">What else the pages know.</param>
/// <param name="Busy">Whether a download or a copy is running.</param>
/// <param name="Notice">A line worth reading, or null.</param>
/// <param name="Error">The last refusal, or null.</param>
/// <param name="Revision">Monotonic observation revision.</param>
internal sealed record SteamAnimationsState(
    string ActiveTab,
    string Selected,
    string StockName,
    IReadOnlyList<SteamAnimationsItem> Library,
    SteamAnimationsBrowse Browse,
    SteamAnimationsItem? Detail,
    SteamAnimationsSettings Settings,
    bool Busy,
    string? Notice,
    string? Error,
    long Revision);
