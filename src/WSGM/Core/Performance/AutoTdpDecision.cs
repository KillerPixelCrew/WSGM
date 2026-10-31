namespace WSGM.Core;

/// <summary>What the controller decided for one observation window.</summary>
/// <param name="Action">The kind of change.</param>
/// <param name="Watts">The limit that should now be in effect.</param>
/// <param name="Reason">Stable diagnostic token, for logs and replay assertions.</param>
internal sealed record AutoTdpDecision(AutoTdpAction Action, int Watts, string Reason)
{
    /// <summary>Whether this decision requires a hardware write.</summary>
    internal bool RequiresWrite => Action is not AutoTdpAction.Hold;
}
