namespace WSGM.Core;

/// <summary>Where Windows can start Steam from without WSGM.</summary>
public enum SteamAutostartKind
{
    /// <summary>A value under a <c>CurrentVersion\Run</c> key.</summary>
    RunValue,

    /// <summary>A shortcut in a Startup folder.</summary>
    StartupShortcut,

    /// <summary>A scheduled task with a logon trigger.</summary>
    ScheduledTask
}
