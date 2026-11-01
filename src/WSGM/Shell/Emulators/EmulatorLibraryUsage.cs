using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>What the Game Library tells the emulator manager about its own state, counted from records it holds.</summary>
/// <param name="Dependencies">The sources and titles using each installation.</param>
/// <param name="Systems">The sources and titles of each system.</param>
/// <param name="ShortcutAppIds">The Steam shortcut of each installation's interface, by installation.</param>
public sealed record EmulatorLibraryUsage(
    IReadOnlyList<EmulatorDependencyCount> Dependencies,
    IReadOnlyList<EmulatorSystemUsage> Systems,
    IReadOnlyDictionary<string, uint> ShortcutAppIds)
{
    /// <summary>No library state yet.</summary>
    public static EmulatorLibraryUsage Empty { get; } = new([], [], new Dictionary<string, uint>());
}
