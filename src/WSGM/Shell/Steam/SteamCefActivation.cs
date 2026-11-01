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
