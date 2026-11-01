namespace WSGM.Shell;

/// <summary>Who asked for a capability command.</summary>
/// <remarks>
///     The origin decides whether a command may persist the user's preference. A limit the user moved
///     is an instruction; the one
///     AutoTDP wrote itself is the controller's own output, and treating it as a manual override would
///     pause the feature on its first tick.
/// </remarks>
internal enum CapabilityCommandOrigin
{
    /// <summary>A person moved this control on a WSGM surface.</summary>
    User,

    /// <summary>An automatic controller inside WSGM wrote it.</summary>
    AutomaticControl,

    /// <summary>AutoTDP's power-lane writes, including its final original-limit restore.</summary>
    AutoTdp,

    /// <summary>
    ///     WSGM is re-applying a stored per-application or global preference on an application change.
    /// </summary>
    /// <remarks>
    ///     Not <see cref="User" />: the value is already the user's saved preference, so persisting it
    ///     again is redundant and, on a release-to-ceiling or a fall back to the global layer, would
    ///     write the wrong value into the layer the funnel resolves. The transition path pauses or
    ///     resumes AutoTDP itself, so this origin deliberately skips the funnel.
    /// </remarks>
    ProfileRestore,

    /// <summary>Replays device desired state without saving it; restored power still pauses AutoTDP.</summary>
    DesiredStateRestore
}
