using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>WSGM's managed-controller target as Steam's rows show it.</summary>
/// <param name="Available">Whether a target can be chosen now.</param>
/// <param name="Targets">The targets the backend can build, by enum name.</param>
/// <param name="SelectedTarget">The stored or live target's name, or empty.</param>
/// <param name="ObservedTarget">The target a device actually exists for now, or empty.</param>
/// <param name="Progress"><c>completed</c> while a target is up, <c>failed</c> when faulted, otherwise empty.</param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="Accent">Whether the running game's own profile supplies the target.</param>
internal sealed record ControllerTargetRowState(
    bool Available,
    IReadOnlyList<ChoiceRowOption> Targets,
    string SelectedTarget,
    string ObservedTarget,
    string Progress,
    string StatusText,
    bool Accent = false);
