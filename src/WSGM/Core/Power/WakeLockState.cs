namespace WSGM.Core;

/// <summary>
///     The WakeWatch color vocabulary for the system-wide wake-lock state
///     (maintainer's WakeWatch project, reused deliberately so both tools read the
///     same): grey = unknown, green = free, yellow = standby blocked, red = display
///     pinned on. Only DISPLAY drives red and SYSTEM/AWAYMODE drive yellow; EXECUTION
///     and friends deliberately do not affect the state.
/// </summary>
public enum WakeLockState
{
    /// <summary>No trustworthy answer (unelevated, or an unrecognized layout).</summary>
    Unknown,

    /// <summary>No locks — display and sleep are free.</summary>
    Free,

    /// <summary>At least one standby lock: the system cannot sleep.</summary>
    SystemHeld,

    /// <summary>At least one display lock: the screen cannot turn off.</summary>
    DisplayHeld
}
