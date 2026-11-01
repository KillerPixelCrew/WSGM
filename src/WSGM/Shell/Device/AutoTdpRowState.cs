namespace WSGM.Shell;

/// <summary>AutoTDP's switch as Steam's rows show it.</summary>
/// <param name="Available">Whether the switch is offered: false only without a primary power limit or device.</param>
/// <param name="Enabled">The stored setting.</param>
/// <param name="Controlling">Whether AutoTDP is moving the limit now.</param>
/// <param name="Watts">The limit it settled on while controlling, or null.</param>
/// <param name="Progress">
///     <c>applying</c> while starting, <c>completed</c> while controlling, <c>failed</c> when it cannot
///     run, otherwise empty.
/// </param>
/// <param name="StatusText">One line of detail, or empty.</param>
/// <param name="Target">The frame-rate target slider, or null to offer none.</param>
internal sealed record AutoTdpRowState(
    bool Available,
    bool Enabled,
    bool Controlling,
    int? Watts,
    string Progress,
    string StatusText,
    AutoTdpRowTarget? Target = null);
