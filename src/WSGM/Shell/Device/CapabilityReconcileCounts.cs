namespace WSGM.Shell;

/// <summary>What one reconciliation pass did.</summary>
/// <param name="Applied">Commands reporting application, including accepted writes without independent readback.</param>
/// <param name="Unchanged">Candidates already at their desired value.</param>
/// <param name="Refused">Dispatched commands returning an outcome other than application.</param>
/// <param name="Skipped">
///     Candidates deferred by missing state, availability, value bounds, prior uncertainty, or owner
///     policy.
/// </param>
internal readonly record struct CapabilityReconcileCounts(int Applied, int Unchanged, int Refused, int Skipped);
