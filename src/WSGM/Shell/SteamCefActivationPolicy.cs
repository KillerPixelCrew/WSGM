namespace WSGM.Shell;

/// <summary>What the gate worker does about Steam's debugging port after one observation.</summary>
internal enum SteamCefActivation
{
    /// <summary>Nothing: the port is coming, Steam is still starting, or recovery is not WSGM's to start.</summary>
    None,

    /// <summary>Restart Steam once: its browser process runs without remote debugging.</summary>
    Restart,

    /// <summary>The restart did not help: the replacement also runs without remote debugging. Reported once.</summary>
    GaveUp
}

/// <summary>Decides from steamwebhelper's command line whether Steam must be restarted for its debugging port.</summary>
/// <remarks>
///     The gate worker is the sole owner. No timer is involved: a Steam that is still updating, syncing
///     or waiting for its login has no browser process yet, and a browser process started with the
///     flag opens the port in its own time, so neither ever restarts Steam. Only a browser process
///     started without the flag does, once; an unsuccessful restart cannot start a loop, and the claim
///     clears when a browser process with the flag is seen or the integration is turned off.
/// </remarks>
internal sealed class SteamCefActivationPolicy
{
    private bool _attempted;
    private uint _reportedBrowser;

    /// <summary>Decides what one observation asks for, claiming the restart before it is returned.</summary>
    /// <param name="enabled">Whether the integration is on and Steam was not closed by the user.</param>
    /// <param name="transitionPending">Whether a session transition currently owns Steam.</param>
    /// <param name="steam">The observation.</param>
    /// <returns>The action; <see cref="SteamCefActivation.Restart" /> at most once per claim.</returns>
    internal SteamCefActivation Decide(bool enabled, bool transitionPending, SteamProcessSnapshot steam)
    {
        if (!enabled || steam.BrowserDebugging == true)
        {
            _attempted = false;
            _reportedBrowser = 0;
            return SteamCefActivation.None;
        }

        if (steam.BrowserDebugging is null || transitionPending)
        {
            return SteamCefActivation.None;
        }

        if (!_attempted)
        {
            // The browser that asked for the restart is never also the one reported as unhelped.
            _attempted = true;
            _reportedBrowser = steam.BrowserProcessId;
            return SteamCefActivation.Restart;
        }

        if (_reportedBrowser == steam.BrowserProcessId)
        {
            return SteamCefActivation.None;
        }

        _reportedBrowser = steam.BrowserProcessId;
        return SteamCefActivation.GaveUp;
    }
}
