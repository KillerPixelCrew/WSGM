namespace WSGM.Core;

/// <summary>One resolved power decision for an application transition.</summary>
/// <param name="Action">What the caller should do.</param>
/// <param name="Watts">
///     The value for <see cref="PerAppPowerAction.Apply" /> and
///     <see cref="PerAppPowerAction.ReleaseToCeiling" />; ignored otherwise.
/// </param>
internal readonly record struct PerAppPowerDecision(PerAppPowerAction Action, int Watts);
