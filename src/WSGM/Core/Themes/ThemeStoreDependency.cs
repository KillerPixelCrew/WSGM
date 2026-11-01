namespace WSGM.Core;

/// <summary>A theme the store lists as another's dependency.</summary>
/// <param name="Id">The store id.</param>
/// <param name="Name">The theme's name.</param>
/// <param name="DisplayName">What the store shows.</param>
/// <param name="Version">The version text.</param>
public sealed record ThemeStoreDependency(string Id, string Name, string DisplayName, string Version);
