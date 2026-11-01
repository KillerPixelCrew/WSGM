using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The shared BIOS folder's checklist, counted over the systems the installed emulators use.</summary>
/// <param name="Found">Needed checklist lines that are present.</param>
/// <param name="Needed">Checklist lines the installed emulators need, optional ones excluded.</param>
/// <param name="Label">"4 of 8 files found", or why nothing is counted.</param>
/// <param name="Systems">Systems an installed emulator uses, first, then the others.</param>
internal sealed record EmulatorBiosSummary(
    int Found,
    int Needed,
    string Label,
    IReadOnlyList<EmulatorBiosChecklist> Systems);
