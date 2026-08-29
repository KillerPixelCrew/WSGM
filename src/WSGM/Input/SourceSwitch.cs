using System;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>
/// The state carried across a change of UI input source.
/// </summary>
/// <remarks>
/// Make-before-break: the incoming source must be healthy and delivering before the outgoing one is
/// dropped. The hard part is not the swap but the controls held across it — without explicit
/// handling, a button held during the switch produces a press edge on the new source that the user
/// never made, or a release that never arrives and leaves the control latched.
/// </remarks>
public sealed record SourceSwitch
{
    /// <summary>How long a control held across the switch waits before it is treated as released.</summary>
    /// <remarks>
    /// A bound is required because the incoming source may never report the control at all — a
    /// managed source exposes rear paddles that the SDL fallback cannot see, so their release would
    /// otherwise never be observed and they would stay suppressed forever.
    /// </remarks>
    public static TimeSpan HeldControlTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The source being switched away from.</summary>
    public required UiInputSource From { get; init; }

    /// <summary>The source being switched to.</summary>
    public required UiInputSource To { get; init; }

    /// <summary>Buttons held on the outgoing source at the moment of the switch.</summary>
    public CanonicalButtons HeldAtSwitch { get; init; }

    /// <summary>When the switch began, in UTC.</summary>
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>What the arbiter should do about a source change.</summary>
public enum SourceSwitchDecision
{
    /// <summary>Complete the switch to the incoming source.</summary>
    Switch,

    /// <summary>Keep the outgoing source; the incoming one is not usable.</summary>
    KeepCurrent,

    /// <summary>
    /// Neither source is usable. Publish neutral, keep keyboard and touch working, warn the user.
    /// </summary>
    FallBackToKeyboardAndTouch,
}

/// <summary>
/// Decides source switches and how held controls are carried across them.
/// </summary>
public static class SourceArbitration
{
    /// <summary>
    /// Decides whether to switch sources.
    /// </summary>
    /// <param name="currentHealthy">Whether the current source is still delivering usable input.</param>
    /// <param name="candidateHealthy">Whether the candidate source is healthy and has delivered a sample.</param>
    /// <returns>What to do.</returns>
    /// <remarks>
    /// The candidate must have delivered at least one complete sample before the current source is
    /// dropped. Switching on "it exists" rather than "it works" produces a gap in which no source is
    /// delivering and the UI appears frozen.
    /// </remarks>
    public static SourceSwitchDecision Decide(bool currentHealthy, bool candidateHealthy) =>
        (currentHealthy, candidateHealthy) switch
        {
            (_, true) => SourceSwitchDecision.Switch,
            (true, false) => SourceSwitchDecision.KeepCurrent,
            (false, false) => SourceSwitchDecision.FallBackToKeyboardAndTouch,
        };

    /// <summary>
    /// Returns the buttons that must be suppressed on the incoming source.
    /// </summary>
    /// <param name="heldAtSwitch">Buttons held on the outgoing source when the switch began.</param>
    /// <param name="observedNow">Buttons the incoming source currently reports.</param>
    /// <param name="elapsed">Time since the switch began.</param>
    /// <returns>Buttons still suppressed.</returns>
    /// <remarks>
    /// A control stays suppressed while the incoming source still reports it held, and is released
    /// once observed up — or once the timeout expires, for controls the incoming source cannot see at
    /// all. Neither a press edge nor a release edge is emitted for a suppressed control: the user
    /// never made either.
    /// </remarks>
    public static CanonicalButtons Suppressed(
        CanonicalButtons heldAtSwitch,
        CanonicalButtons observedNow,
        TimeSpan elapsed) =>
        elapsed >= SourceSwitch.HeldControlTimeout
            ? CanonicalButtons.None
            : heldAtSwitch & observedNow;

    /// <summary>
    /// Whether a switch requires zeroing the virtual target first.
    /// </summary>
    /// <param name="from">The outgoing source.</param>
    /// <param name="to">The incoming source.</param>
    /// <returns><see langword="true"/> when a neutral state must be published before the swap.</returns>
    /// <remarks>
    /// Always true when either side involves the managed source, which is the only one that forwards
    /// to a virtual target. Without the neutral state, whatever was held at the moment of the switch
    /// stays latched in the game for as long as the swap takes.
    /// </remarks>
    public static bool RequiresNeutralOutput(UiInputSource from, UiInputSource to) =>
        from is UiInputSource.ManagedCanonical || to is UiInputSource.ManagedCanonical;
}
