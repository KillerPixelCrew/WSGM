namespace WSGM.Shell;

/// <summary>One host-authorized game override returned by an artwork provider.</summary>
/// <param name="Id">Opaque identity for a currently authorized provider match.</param>
/// <param name="Name">Displayed game name.</param>
/// <param name="Provider">Provider display name, falling back to its configured id.</param>
internal sealed record SteamArtworkBrowserGame(string Id, string Name, string Provider);
