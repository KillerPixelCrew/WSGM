using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One system's BIOS checklist.</summary>
/// <param name="SystemId">The system the add-files and relink commands take.</param>
/// <param name="Name">The system's name.</param>
/// <param name="UsedBy">The installed emulators that read the files, joined, or empty.</param>
/// <param name="Optional">Whether every file of the system is optional.</param>
/// <param name="Items">The checklist lines.</param>
internal sealed record EmulatorBiosChecklist(
    string SystemId,
    string Name,
    string UsedBy,
    bool Optional,
    IReadOnlyList<EmulatorBiosItem> Items);
