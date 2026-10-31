using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>Ready-to-render emulator choices for one canonical system.</summary>
/// <param name="SystemId">The system identity.</param>
/// <param name="Installations">Only installations accepted by the shared launch policy.</param>
public sealed record RomSystemEmulatorChoices(string SystemId, IReadOnlyList<RomEmulatorChoice> Installations);
