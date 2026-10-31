namespace WSGM.Core;

/// <summary>What one fresh observation window was.</summary>
internal enum AutoTdpWindowClass
{
    /// <summary>No fresh window: the same window read again, or no renderer at all.</summary>
    None,

    /// <summary>A stall, present hiatus or loading interval. Contaminated, never evidence.</summary>
    Severe,

    /// <summary>Delivery below target. Evidence once it is sustained.</summary>
    Missed,

    /// <summary>Meeting the deadline with no headroom to give away.</summary>
    OnTarget,

    /// <summary>Headroom, or the limiter holding the game at its target.</summary>
    Comfortable
}
