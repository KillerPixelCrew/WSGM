namespace WSGM.Core;

/// <summary>What the AutoTDP controller decided to do with the primary power limit.</summary>
internal enum AutoTdpAction
{
    /// <summary>Leave the current limit alone.</summary>
    Hold,

    /// <summary>Raise the limit one step because frames are missing their deadline.</summary>
    Raise,

    /// <summary>Try one step lower because delivery has been comfortable.</summary>
    Probe,

    /// <summary>Go back to the last limit that delivered, because the probe hurt.</summary>
    Restore,

    /// <summary>Hand the limit back to whoever set it manually.</summary>
    Release
}
