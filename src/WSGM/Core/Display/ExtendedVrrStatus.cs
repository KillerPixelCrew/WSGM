namespace WSGM.Core;

/// <summary>What both surfaces show for the extended VRR range.</summary>
/// <param name="Phase">Where the range stands.</param>
/// <param name="Detail">The row's description: the state, what to do next, or why nothing can change.</param>
/// <param name="Busy">Whether a change is running.</param>
/// <param name="LowFrameRateCompensation">Whether Intel's low frame rate compensation is kept on while the range is active.</param>
/// <param name="LowFrameRateVisible">
///     Whether the low frame rate compensation switch is drawn: while the range is loaded, WSGM's own or the standalone
///     tool's, which using the switch hands to WSGM.
/// </param>
internal sealed record ExtendedVrrStatus(
    ExtendedVrrPhase Phase,
    string Detail,
    bool Busy = false,
    bool LowFrameRateCompensation = false,
    bool LowFrameRateVisible = false)
{
    /// <summary>The row's title on both surfaces.</summary>
    internal const string Title = "Extended VRR range (28–120 Hz)";

    /// <summary>The low frame rate compensation switch's title on both surfaces.</summary>
    internal const string LowFrameRateTitle = "Low framerate compensation";

    /// <summary>The low frame rate compensation switch's description on both surfaces.</summary>
    internal const string LowFrameRateDetail =
        "Below 28 FPS Intel repeats frames to stay in the range. Smoother at very low frame rates, but it can judder.";

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
