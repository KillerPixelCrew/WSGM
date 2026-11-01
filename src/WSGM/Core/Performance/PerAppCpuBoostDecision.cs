namespace WSGM.Core;

/// <summary>What the transition does to the processor boost mode.</summary>
/// <param name="Action">The action.</param>
/// <param name="Mode">The mode to write, for <see cref="PerAppCpuBoostAction.Apply" />.</param>
internal readonly record struct PerAppCpuBoostDecision(PerAppCpuBoostAction Action, CpuBoostMode Mode);
