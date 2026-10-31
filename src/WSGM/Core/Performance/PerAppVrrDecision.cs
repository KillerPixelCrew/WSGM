namespace WSGM.Core;

/// <summary>One resolved variable-refresh decision for an application transition.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Enabled">The state to write for <see cref="PerAppVrrAction.Apply" />.</param>
internal readonly record struct PerAppVrrDecision(PerAppVrrAction Action, bool Enabled);
