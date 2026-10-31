namespace WSGM.Core;

/// <summary>Visual style of the boot-splash progress spinner.</summary>
public enum SplashSpinnerStyle
{
    /// <summary>The classic in-repo rotating arc ring.</summary>
    Ring,

    /// <summary>LoadingIndicators.Avalonia "Arc" mode.</summary>
    LiArc,

    /// <summary>LoadingIndicators.Avalonia "Arcs" mode.</summary>
    LiArcs,

    /// <summary>LoadingIndicators.Avalonia "ArcsRing" mode.</summary>
    LiArcsRing,

    /// <summary>LoadingIndicators.Avalonia "DoubleBounce" mode.</summary>
    LiDoubleBounce,

    /// <summary>LoadingIndicators.Avalonia "FlipPlane" mode.</summary>
    LiFlipPlane,

    /// <summary>LoadingIndicators.Avalonia "Pulse" mode.</summary>
    LiPulse,

    /// <summary>LoadingIndicators.Avalonia "Ring" mode.</summary>
    LiRing,

    /// <summary>LoadingIndicators.Avalonia "ThreeDots" mode.</summary>
    LiThreeDots,

    /// <summary>LoadingIndicators.Avalonia "Wave" mode.</summary>
    LiWave,

    /// <summary>
    ///     In-repo sweeping line along a screen edge (see
    ///     <see cref="SplashConfig.SweepEdge" />).
    /// </summary>
    SweepLine,

    /// <summary>No spinner at all (no animation timer is created).</summary>
    Off
}
