namespace WSGM.Core;

/// <summary>Outcome of a launch-configuration change.</summary>
/// <param name="Ok">
///     Whether the change reached Steam: applied, or sent and left unanswered. A write that was sent
///     is published as written and never retried; only one that never reached Steam or that Steam
///     refused is a failure.
/// </param>
/// <param name="Detail">A user-facing note (why it failed, or what was done).</param>
/// <param name="Confirmed">Whether Steam answered that the change was applied.</param>
public readonly record struct LaunchConfigResult(bool Ok, string Detail, bool Confirmed = false);
