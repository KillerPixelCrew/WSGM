namespace WSGM.Core;

/// <summary>Where the controller is in its control loop.</summary>
internal enum AutoTdpPhase
{
    /// <summary>Waiting for a write to take effect. Nothing is judged.</summary>
    Settling,

    /// <summary>The steady state: counting evidence toward a raise or a downward probe.</summary>
    Tracking,

    /// <summary>Stepping up, and judging whether delivery responded to each step.</summary>
    Raising,

    /// <summary>Power went up and delivery did not follow. Holding rather than climbing further.</summary>
    Unresponsive,

    /// <summary>A stall or present hiatus is contaminating the evidence. Nothing is judged or learned.</summary>
    Quarantine,

    /// <summary>A step down is in effect and being judged.</summary>
    Probing
}
