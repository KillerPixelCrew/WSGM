namespace WSGM.Shell;

/// <summary>One system on System defaults: its library usage and whether any installed emulator runs it.</summary>
/// <param name="SystemId">The canonical system the preference command takes.</param>
/// <param name="Name">The system's name.</param>
/// <param name="Family">Its console family.</param>
/// <param name="Sources">ROM sources with the system.</param>
/// <param name="Titles">Imported titles of the system, or null when they could not be counted.</param>
/// <param name="Summary">"212 games from 1 source", or "9 games have no emulator yet".</param>
/// <param name="InLibrary">Whether a source or an imported title uses the system.</param>
/// <param name="NeedsEmulator">Whether the library uses the system and no installed emulator runs it.</param>
internal sealed record EmulatorSystemRow(
    string SystemId,
    string Name,
    string Family,
    int Sources,
    int? Titles,
    string Summary,
    bool InLibrary,
    bool NeedsEmulator);
