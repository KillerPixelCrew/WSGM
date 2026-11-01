namespace WSGM.Core;

/// <summary>What happens to the WSGM browser when its page or Overlay closes.</summary>
public enum BrowserMemoryBehavior
{
    /// <summary>Close the controller and its helper; preserve only the on-disk site profile.</summary>
    UnloadWhenClosed,

    /// <summary>Retain the page, mute it and try to suspend it until the page opens again.</summary>
    KeepAlive
}
