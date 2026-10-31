namespace WSGM.Core;

/// <summary>What to do with the device power limit when the running application changes.</summary>
internal enum PerAppPowerAction
{
    /// <summary>Write a concrete watt value the resolved layer specifies.</summary>
    Apply,

    /// <summary>Hand control back to AutoTDP, which the outgoing application's own limit had paused.</summary>
    ResumeAutomatic,

    /// <summary>Release the limit to the device ceiling: nothing is preferred and AutoTDP is off.</summary>
    ReleaseToCeiling,

    /// <summary>Change nothing. WSGM never imposed a limit, so it has none to take back.</summary>
    Leave
}
