namespace WSGM.Core;

/// <summary>What a short press of the handheld's power button does while WSGM handles the button.</summary>
/// <remarks>A long press always opens a power menu: Steam's in Game Mode, the overlay's on the desktop.</remarks>
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
