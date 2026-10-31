namespace WSGM.Shell;

/// <summary>Concrete library dependencies to show before forgetting or removing an installation.</summary>
/// <param name="InstallationId">The installation those sources and imported titles use.</param>
/// <param name="Sources">Configured ROM sources using the installation.</param>
/// <param name="Titles">Imported titles using the installation, or null when their store could not be read.</param>
public sealed record EmulatorDependencyCount(string InstallationId, int Sources, int? Titles)
{
    /// <summary>A truthful dependency description shared by both user interfaces.</summary>
    public string Summary => $"Used by {Sources} configured ROM {(Sources == 1 ? "source" : "sources")}. "
                             + (Titles is { } titles
                                 ? $"{titles} imported {(titles == 1 ? "title uses" : "titles use")} this installation."
                                 : "Imported title dependencies could not be read.");
}
