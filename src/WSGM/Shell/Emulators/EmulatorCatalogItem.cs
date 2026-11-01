namespace WSGM.Shell;

/// <summary>One supported emulator as Get emulators lists it.</summary>
/// <param name="DefinitionId">The reviewed definition the install command takes.</param>
/// <param name="Name">The emulator's name.</param>
/// <param name="Systems">The supported systems' names, joined for display.</param>
/// <param name="Families">The console families it covers, for the system filters.</param>
/// <param name="LatestVersion">The newest release of its first channel, or empty before a check.</param>
/// <param name="Requirement">What it needs after installing, from its prerequisites, or empty.</param>
/// <param name="InstalledVersion">The version of a registered installation, or empty when none is.</param>
internal sealed record EmulatorCatalogItem(
    string DefinitionId,
    string Name,
    string Systems,
    string[] Families,
    string LatestVersion,
    string Requirement,
    string InstalledVersion)
{
    /// <summary>The supported canonical system identities, for a filter on one system.</summary>
    public string[] SystemIds { get; init; } = [];
}
