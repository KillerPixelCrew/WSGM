using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The last Update all run: its progress while running, then which updates applied and which failed.</summary>
/// <param name="Running">Whether the sequence is still working.</param>
/// <param name="Done">How many updates have finished, applied or failed.</param>
/// <param name="Total">How many updates the run started with.</param>
/// <param name="Current">The emulator updating now, or empty.</param>
/// <param name="Updated">The names of the emulators that updated.</param>
/// <param name="Failed">Each failed emulator's name and the reason it gave.</param>
/// <param name="Outcome">The run's result for both surfaces, empty while running.</param>
internal sealed record EmulatorUpdateAllState(
    bool Running,
    int Done,
    int Total,
    string Current,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Failed,
    string Outcome)
{
    /// <summary>Whether the run was stopped before it reached every update.</summary>
    public bool Cancelled { get; init; }
}
