namespace WSGM.Core;

/// <summary>Decides what a running-application change does to the processor boost mode.</summary>
/// <remarks>
///     Like the display twin, with one difference in the restore baseline: Windows has no default the
///     way a fixed-refresh desktop does, so when no layer prefers a mode and WSGM had imposed one, the
///     mode WSGM found before its first write comes back. An unknown baseline is left alone rather
///     than replaced with a guess.
/// </remarks>
internal static class PerApplicationCpuBoostPolicy
{
    /// <summary>Decides the action for a transition to the resolved mode.</summary>
    /// <param name="effectiveMode">The mode resolved for the new application, or null for none.</param>
    /// <param name="modeCurrentlyImposed">Whether WSGM's per-application feature is the reason the current mode holds.</param>
    /// <param name="baseline">The mode WSGM observed before it first wrote one, or null when unknown.</param>
    /// <returns>The action and, where relevant, the mode it carries.</returns>
    internal static PerAppCpuBoostDecision DecideOnTargetChange(
        CpuBoostMode? effectiveMode,
        bool modeCurrentlyImposed,
        CpuBoostMode? baseline)
    {
        return effectiveMode is { } mode
            ? new PerAppCpuBoostDecision(PerAppCpuBoostAction.Apply, mode)
            : modeCurrentlyImposed && baseline is { } original
                ? new PerAppCpuBoostDecision(PerAppCpuBoostAction.Apply, original)
                : new PerAppCpuBoostDecision(PerAppCpuBoostAction.Leave, default);
    }
}
