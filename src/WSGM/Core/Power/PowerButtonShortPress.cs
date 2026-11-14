namespace WSGM.Core;

/// <summary>What a short press of a supported handheld's power button does on the desktop.</summary>
/// <remarks>Game Mode always runs Steam suspend. A long press opens the mode's power menu.</remarks>
public enum PowerButtonShortPress
{
    /// <summary>Modern Standby, entered the way the display timeout does it.</summary>
    Sleep,

    /// <summary>In Game Mode, Steam's own suspend screen first, then Modern Standby; elsewhere plain sleep.</summary>
    SteamSuspend,

    /// <summary>Hibernation.</summary>
    Hibernate,

    /// <summary>Shut down.</summary>
    Shutdown,

    /// <summary>Restart.</summary>
    Restart,

    /// <summary>Nothing; only the long press does something.</summary>
    Nothing
}
