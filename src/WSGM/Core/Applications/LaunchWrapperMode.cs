using System;

namespace WSGM.Core;

/// <summary>Which behaviours a game's launch wrapper should apply.</summary>
[Flags]
public enum LaunchWrapperMode
{
    /// <summary>No wrapper; the game launches the way Steam normally would.</summary>
    None = 0,

    /// <summary>Run the game at medium integrity under elevated Steam.</summary>
    Deelevate = 1,

    /// <summary>
    ///     Block Steam Input for the game's lifetime through the resident
    ///     shim Steam loaded itself. Fails open when no shim is loaded - the wrapper
    ///     never injects on this route.
    /// </summary>
    InputLease = 2,

    /// <summary>Both behaviours in one wrapper process.</summary>
    Both = Deelevate | InputLease,

    /// <summary>
    ///     Block Steam Input by injecting the gate into Steam for the game's
    ///     lifetime. Written only when Steam Input Management is off, because then no
    ///     resident shim exists to connect to.
    /// </summary>
    InputLeaseInject = 4,

    /// <summary>De-elevation plus the injecting lease.</summary>
    BothInject = Deelevate | InputLeaseInject
}
