namespace WSGM.Shell;

/// <summary>Shared admission state for AutoTDP commands and every UI projection.</summary>
/// <param name="Available">Whether writable primary and required paired power limits are present.</param>
/// <param name="Detail">Reason admission is unavailable; empty when available.</param>
/// <param name="TargetFrametimeMs">
///     The deadline control judges against, from AutoTDP's own target and any lower active frame limit,
///     even if power is unavailable; null without a usable target.
/// </param>
internal sealed record AutoTdpAvailability(bool Available, string Detail, double? TargetFrametimeMs);
