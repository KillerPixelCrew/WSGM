namespace WSGM.Core;

/// <summary>What a foreground window means for per-application policy.</summary>
public enum ForegroundApplicationKind
{
    /// <summary>An ordinary application whose identity should drive per-application settings.</summary>
    Application,

    /// <summary>
    ///     Shell furniture, a system dialog, or WSGM itself: the foreground changed, but what the user
    ///     is <em>doing</em> did not.
    /// </summary>
    Restricted
}
