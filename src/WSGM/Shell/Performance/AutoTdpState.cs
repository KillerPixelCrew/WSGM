namespace WSGM.Shell;

/// <summary>Truthful state of AutoTDP for the overlay, native QAM, and diagnostics.</summary>
internal enum AutoTdpState
{
    /// <summary>The user has not enabled AutoTDP.</summary>
    Off,

    /// <summary>Enabled, but a prerequisite is missing.</summary>
    Unavailable,

    /// <summary>Enabled and waiting for a foreground application to render.</summary>
    Idle,

    /// <summary>Actively controlling the power limit.</summary>
    Controlling,

    /// <summary>Suspended because the power limit was changed by hand.</summary>
    Paused
}
