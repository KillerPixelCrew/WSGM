using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Bounds reported by a concrete RTSS adapter.</summary>
/// <param name="MinimumFrameLimit">Inclusive minimum frame limit in frames per second.</param>
/// <param name="MaximumFrameLimit">Inclusive maximum frame limit in frames per second.</param>
/// <param name="OverlayLevels">Supported WSGM overlay selector values; callers must not mutate the set.</param>
internal sealed record RtssCapabilities(
    int MinimumFrameLimit,
    int MaximumFrameLimit,
    IReadOnlySet<int> OverlayLevels)
{
    /// <summary>Checks whether bounds are available for a control.</summary>
    /// <param name="control">The control to inspect.</param>
    /// <returns>True for a valid frame-limit range or a nonempty overlay-level set.</returns>
    internal bool Supports(PerformanceControl control)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => MinimumFrameLimit >= 0
                                             && MaximumFrameLimit >= MinimumFrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevels.Count > 0,
            _ => false
        };
    }

    /// <summary>Checks a value against the reported bounds.</summary>
    /// <param name="control">The control to inspect.</param>
    /// <param name="value">The proposed value.</param>
    /// <returns>True when the value is in range or in the supported set; false for unknown controls.</returns>
    internal bool IsValid(PerformanceControl control, int value)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => value >= MinimumFrameLimit && value <= MaximumFrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevels.Contains(value),
            _ => false
        };
    }
}
