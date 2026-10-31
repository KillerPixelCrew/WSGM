using System.Collections.Generic;

namespace WSGM.Overlay;

/// <summary>A bounded shared-performance snapshot for one overlay projection.</summary>
/// <param name="Visible">Whether the performance section is available for presentation.</param>
/// <param name="Status">Service availability or operation detail.</param>
/// <param name="Rows">Value controls shown with Device power and available to pinning.</param>
/// <param name="ProfileRows">Per-application profile controls; the enable toggle heads the Device overview.</param>
internal sealed record PerformanceOverlaySnapshot(
    bool Visible,
    string Status,
    IReadOnlyList<DescriptorRow> Rows,
    IReadOnlyList<DescriptorRow> ProfileRows);
