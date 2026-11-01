using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The installed choices needed by ROM editors, without downloader progress or release offers.</summary>
/// <param name="Choices">Compatible installations and cores per system.</param>
/// <param name="SystemPreferences">The persisted default choices.</param>
public sealed record RomEmulatorState(
    IReadOnlyList<RomSystemEmulatorChoices> Choices,
    IReadOnlyList<EmulatorSystemPreference> SystemPreferences)
{
    /// <summary>No installed emulator or system preference.</summary>
    public static RomEmulatorState Empty { get; } = new([], []);
}
