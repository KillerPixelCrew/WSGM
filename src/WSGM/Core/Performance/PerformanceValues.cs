namespace WSGM.Core;

/// <summary>Desired or observed values. Null means the corresponding control has no value.</summary>
/// <param name="FrameLimit">Frame limit in frames per second; zero is unlimited, null is unspecified.</param>
/// <param name="OverlayLevel">WSGM overlay selector value, or null when unspecified.</param>
internal sealed record PerformanceValues(int? FrameLimit, int? OverlayLevel)
{
    /// <summary>No value for either control.</summary>
    internal static readonly PerformanceValues Empty = new(null, null);

    /// <summary>Returns the value for one control.</summary>
    /// <param name="control">The control to select.</param>
    /// <returns>The stored value, or null for an unknown control.</returns>
    internal int? ValueFor(PerformanceControl control)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => FrameLimit,
            PerformanceControl.OverlayLevel => OverlayLevel,
            _ => null
        };
    }

    /// <summary>Returns a copy with one control changed.</summary>
    /// <param name="control">The control to change.</param>
    /// <param name="value">The replacement value; this method does not validate bounds.</param>
    /// <returns>The updated copy, or this instance for an unknown control.</returns>
    internal PerformanceValues With(PerformanceControl control, int value)
    {
        return control switch
        {
            PerformanceControl.FrameLimit => this with { FrameLimit = value },
            PerformanceControl.OverlayLevel => this with { OverlayLevel = value },
            _ => this
        };
    }
}
