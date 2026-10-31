namespace WSGM.Core;

/// <summary>The two bounded RTSS controls exposed through shared performance state.</summary>
internal enum PerformanceControl
{
    /// <summary>RTSS frame limit in frames per second; zero removes the limit.</summary>
    FrameLimit,

    /// <summary>WSGM OSD selector: off, three presets, or the custom layout.</summary>
    OverlayLevel
}
