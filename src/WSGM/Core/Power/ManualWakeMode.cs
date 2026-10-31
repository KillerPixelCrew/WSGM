namespace WSGM.Core;

/// <summary>
///     The quick-access Keep Awake cycle: off → block standby → block standby
///     and keep the display on → off.
/// </summary>
public enum ManualWakeMode
{
    /// <summary>No manual hold.</summary>
    Off,

    /// <summary>Standby blocked; the display still times out.</summary>
    Standby,

    /// <summary>Standby blocked and the display pinned on.</summary>
    StandbyAndDisplay
}
