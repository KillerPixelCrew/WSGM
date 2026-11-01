namespace WSGM.Core;

/// <summary>What both surfaces show for the extended VRR range.</summary>
/// <param name="Phase">Where the range stands.</param>
/// <param name="Detail">The row's description: the state, what to do next, or why nothing can change.</param>
/// <param name="Busy">Whether a change is running.</param>
internal sealed record ExtendedVrrStatus(ExtendedVrrPhase Phase, string Detail, bool Busy = false)
{
    /// <summary>The row's title on both surfaces.</summary>
    internal const string Title = "Extended VRR range (28–120 Hz)";

    /// <summary>Nothing to show.</summary>
    internal static ExtendedVrrStatus Hidden { get; } = new(ExtendedVrrPhase.Hidden, string.Empty);

    /// <summary>Whether the row is drawn.</summary>
    internal bool Visible => Phase is not ExtendedVrrPhase.Hidden;

    /// <summary>The switch position: on while the override is written or the standalone tool owns the range.</summary>
    internal bool Enabled => Phase is ExtendedVrrPhase.RestartToApply or ExtendedVrrPhase.Active
        or ExtendedVrrPhase.Standalone;

    /// <summary>Whether the switch can be used now.</summary>
    internal bool CanChange => !Busy && Phase is not (ExtendedVrrPhase.Hidden or ExtendedVrrPhase.Refused);
}
