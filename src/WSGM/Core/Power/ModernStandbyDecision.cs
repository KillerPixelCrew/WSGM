namespace WSGM.Core;

/// <summary>The decision for one look at an unattended wake.</summary>
/// <param name="Outcome">What was decided, and why.</param>
internal readonly record struct ModernStandbyDecision(ModernStandbyOutcome Outcome)
{
    /// <summary>Whether this look should suspend the machine.</summary>
    internal bool ShouldResuspend => Outcome == ModernStandbyOutcome.Resuspend;

    /// <summary>Whether a later look at the same wake could still decide to suspend.</summary>
    /// <remarks>
    ///     Only the grace period is worth waiting through. Every other refusal is a fact about this
    ///     wake that will not change while it lasts, so polling past one is a timer that can never
    ///     reach a decision.
    /// </remarks>
    internal bool ShouldKeepWatching => Outcome == ModernStandbyOutcome.TooSoon;
}
