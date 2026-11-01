namespace WSGM.Shell;

/// <summary>How much of the library one system holds, counted by the Game Library from its own state.</summary>
/// <param name="SystemId">The canonical system.</param>
/// <param name="Sources">ROM sources configured for the system or with imported titles of it.</param>
/// <param name="Titles">Imported titles of the system, or null when the import records could not be read.</param>
public sealed record EmulatorSystemUsage(string SystemId, int Sources, int? Titles);
