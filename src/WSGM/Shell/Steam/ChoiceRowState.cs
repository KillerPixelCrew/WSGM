using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One feature's choice as the Steam row producer reads it.</summary>
/// <param name="Available">Whether the choice can be changed now.</param>
/// <param name="Options">The options, in order; none hides the row.</param>
/// <param name="Current">The selected option's id, or empty when none of them is in force.</param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="Accent">Whether the running game's own profile supplies the value.</param>
internal sealed record ChoiceRowState(
    bool Available,
    IReadOnlyList<ChoiceRowOption> Options,
    string Current,
    string StatusText,
    bool Accent = false);
