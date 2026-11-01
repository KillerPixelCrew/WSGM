namespace WSGM.Core;

/// <summary>What caused one AutoTDP trace row.</summary>
internal enum AutoTdpTraceEvent
{
    /// <summary>One controller window.</summary>
    Tick,

    /// <summary>A control generation started.</summary>
    Enabled,

    /// <summary>A control generation ended and its restoration finished.</summary>
    Disabled,

    /// <summary>The running application changed.</summary>
    Application,

    /// <summary>A manual power change paused control.</summary>
    ManualPause,

    /// <summary>Control resumed after a scoped override ended.</summary>
    Resume
}
