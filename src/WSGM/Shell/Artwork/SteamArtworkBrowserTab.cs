namespace WSGM.Shell;

/// <summary>One artwork slot shown in the page's Decky-compatible tab strip.</summary>
/// <param name="Id">Stable artwork-slot id, or the management-tab id.</param>
/// <param name="Label">Displayed tab label.</param>
/// <param name="Manage">True for the management tab, which does not fetch provider results.</param>
internal sealed record SteamArtworkBrowserTab(string Id, string Label, bool Manage = false);
