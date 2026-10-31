namespace WSGM.Shell;

/// <summary>A theme another needs, as the detail view lists it.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Installed">Whether it is already installed.</param>
internal sealed record SteamThemesDependency(string Id, string Name, string DisplayName, bool Installed);
