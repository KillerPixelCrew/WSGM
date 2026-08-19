namespace WSGM.Interop;

/// <summary>Which registered power setting reported a display on/off transition.
///
/// <para>Windows offers no way to <i>query</i> the current display power state from
/// user mode, so a notification is the only mechanism there is. WSGM therefore listens on
/// all three that exist and records which one spoke — a wake that one setting misses can
/// still arrive on another, and the source name in the log is what makes a missing
/// notification diagnosable from a pasted device log instead of guesswork.</para></summary>
public enum DisplayStateSource
{
    /// <summary>GUID_SESSION_DISPLAY_STATUS — the display of this session. The primary
    /// source, the one Microsoft documents for interactive applications, and the only one
    /// that may be trusted to say the screen went dark.</summary>
    Session,

    /// <summary>GUID_CONSOLE_DISPLAY_STATE — the console session's display. Redundant
    /// wake source only: it describes whichever session owns the console, so acting on
    /// its "off" would mute the wrong session after a fast user switch.</summary>
    Console,

    /// <summary>GUID_MONITOR_POWER_ON — the superseded pre-Windows-8 setting. Modern
    /// Windows may never send it; treated as a best-effort wake source only.</summary>
    LegacyMonitor,
}
