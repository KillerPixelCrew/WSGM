namespace WSGM.Core;

/// <summary>What the richer OSD levels show about the sustained power limit and AutoTDP.</summary>
/// <param name="TdpWatts">The current sustained power limit, when the device reports one.</param>
/// <param name="AutoTdpEnabled">Whether AutoTDP is switched on.</param>
/// <param name="AutoTdpRunning">Whether AutoTDP is actively controlling the limit.</param>
/// <param name="AutoTdpWatts">The limit AutoTDP currently holds or is moving toward.</param>
/// <param name="AutoTdpActivity">Short, display-ready text describing what AutoTDP is doing.</param>
/// <param name="SlowWatts">The slow boost limit (Intel PL2), only when the device publishes one.</param>
/// <param name="FastWatts">The fast boost limit, only when the device publishes one.</param>
internal sealed record RtssOsdPowerStatus(
    int? TdpWatts,
    bool AutoTdpEnabled,
    bool AutoTdpRunning,
    int? AutoTdpWatts,
    string AutoTdpActivity,
    int? SlowWatts = null,
    int? FastWatts = null)
{
    /// <summary>No known limit and no active AutoTDP state.</summary>
    internal static readonly RtssOsdPowerStatus Empty = new(null, false, false, null, string.Empty);
}
