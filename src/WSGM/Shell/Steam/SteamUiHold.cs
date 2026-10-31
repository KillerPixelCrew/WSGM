using System;

namespace WSGM.Shell;

/// <summary>Why the transport is held closed while the master switch is on.</summary>
[Flags]
internal enum SteamUiHold
{
    /// <summary>No hold.</summary>
    None = 0,

    /// <summary>
    ///     A transition is about to ask, or has asked, Steam for Big Picture and has not settled. The
    ///     request rebuilds Steam's whole front-end, so the hold begins before it fires; it ends early
    ///     once the Big Picture window exists.
    /// </summary>
    BigPictureRequest = 1,

    /// <summary>
    ///     A transition is about to ask, or has asked, Steam to leave Big Picture and has not settled.
    ///     Leaving rebuilds the front-end exactly as entering does.
    /// </summary>
    BigPictureClose = 2,

    /// <summary>A bounded restart to activate Steam's debugging port owns Steam.</summary>
    Activation = 4,

    /// <summary>The machine accepted a suspend; nothing may be in flight across the sleep.</summary>
    Suspended = 8,

    /// <summary>The machine resumed moments ago; the transport reconnects once Steam has settled.</summary>
    Resuming = 16
}
