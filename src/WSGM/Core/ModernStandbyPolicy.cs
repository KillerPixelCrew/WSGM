using System;

namespace WSGM.Core;

/// <summary>Why a resume did or did not send the machine back to sleep.</summary>
/// <remarks>
/// Every refusal is named rather than collapsed into a bool. A machine that quietly stays awake and
/// a machine that quietly suspends are both surprising, and the difference between "you woke it"
/// and "something else did, but you are using it" is the whole of this feature's diagnosis.
/// </remarks>
internal enum ModernStandbyOutcome
{
    /// <summary>The mode is switched off. WSGM does nothing on resume.</summary>
    Disabled,

    /// <summary>Windows attributes the wake to a person. Their wake is never undone.</summary>
    UserWoke,

    /// <summary>The display came on. Something is being shown to somebody.</summary>
    DisplayOn,

    /// <summary>Input was observed since the wake.</summary>
    UserActive,

    /// <summary>This wake has already been slept through as often as WSGM will try.</summary>
    AttemptsExhausted,

    /// <summary>Still inside the grace period; ask again later.</summary>
    TooSoon,

    /// <summary>Nothing accounts for the wake. Suspend again.</summary>
    Resuspend,
}

/// <summary>The decision for one look at an unattended wake.</summary>
/// <param name="Outcome">What was decided, and why.</param>
internal readonly record struct ModernStandbyDecision(ModernStandbyOutcome Outcome)
{
    /// <summary>Whether this look should suspend the machine.</summary>
    internal bool ShouldResuspend => Outcome == ModernStandbyOutcome.Resuspend;

    /// <summary>Whether a later look at the same wake could still decide to suspend.</summary>
    /// <remarks>
    /// Only the grace period is worth waiting through. Every other refusal is a fact about this
    /// wake that will not change while it lasts, so polling past one is a timer that can never
    /// reach a decision.
    /// </remarks>
    internal bool ShouldKeepWatching => Outcome == ModernStandbyOutcome.TooSoon;
}

/// <summary>
/// Decides whether an unattended wake should be slept through again. Pure: it reads no clock, no
/// Windows state and no configuration, so every rule below is testable on its own.
/// </summary>
/// <remarks>
/// The shape follows the Winhanced investigation in #27 rather than Handheld Companion's Enhanced
/// Sleep: nothing global is changed, so there is nothing to restore if WSGM dies mid-session. The
/// machine wakes normally, and WSGM only decides whether to put it back.
/// </remarks>
internal static class ModernStandbyPolicy
{
    /// <summary>How many times one wake may be slept through before WSGM leaves it awake.</summary>
    /// <remarks>
    /// A machine that wakes for a reason WSGM cannot see would otherwise be suspended in a loop it
    /// never escapes, which is worse than the drain this feature exists to stop: the user reaches
    /// for it and it will not stay on.
    /// </remarks>
    internal const int MaximumAttemptsPerWake = 3;

    /// <summary>The default settle time before an unexplained wake is acted on.</summary>
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(20);

    /// <summary>Decides one look at the current wake.</summary>
    /// <param name="enabled">Whether the user switched the mode on.</param>
    /// <param name="unattendedResume">
    ///     Whether Windows attributes the resume to something other than a person, from
    ///     <c>IsSystemResumeAutomatic</c>.
    /// </param>
    /// <param name="displayOn">Whether the console display is currently on.</param>
    /// <param name="sinceWake">How long ago the machine woke.</param>
    /// <param name="sinceUserInput">
    ///     How long ago Windows last recorded user input. Larger than <paramref name="sinceWake"/>
    ///     means nothing has been touched since the wake.
    /// </param>
    /// <param name="grace">How long to let an unexplained wake settle before acting.</param>
    /// <param name="attempts">How many times this wake has already been slept through.</param>
    /// <returns>The decision and the reason behind it.</returns>
    internal static ModernStandbyDecision Decide(
        bool enabled,
        bool unattendedResume,
        bool displayOn,
        TimeSpan sinceWake,
        TimeSpan sinceUserInput,
        TimeSpan grace,
        int attempts)
    {
        if (!enabled)
        {
            return new(ModernStandbyOutcome.Disabled);
        }
        if (!unattendedResume)
        {
            return new(ModernStandbyOutcome.UserWoke);
        }

        // The display is the gate that does not depend on Windows counting a device as input.
        // A handheld's gamepad does not advance the last-input time (see #69), so a player holding
        // a controller looks idle by that measure alone — and suspending the machine under their
        // hands is the one failure this feature must never produce.
        if (displayOn)
        {
            return new(ModernStandbyOutcome.DisplayOn);
        }
        if (sinceUserInput < sinceWake || sinceUserInput < grace)
        {
            return new(ModernStandbyOutcome.UserActive);
        }
        if (attempts >= MaximumAttemptsPerWake)
        {
            return new(ModernStandbyOutcome.AttemptsExhausted);
        }
        return sinceWake < grace
            ? new(ModernStandbyOutcome.TooSoon)
            : new(ModernStandbyOutcome.Resuspend);
    }
}
