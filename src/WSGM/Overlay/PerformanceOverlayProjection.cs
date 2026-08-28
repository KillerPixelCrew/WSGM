using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Overlay;

/// <summary>A bounded shared-performance snapshot for one overlay projection.</summary>
internal sealed record PerformanceOverlaySnapshot(
    bool Visible,
    string Status,
    IReadOnlyList<DescriptorRow> Rows);

/// <summary>
/// Client seam for the shared performance service. The overlay observes and invokes it but never
/// starts, stops, repairs, installs, or owns the underlying RTSS lifecycle.
/// </summary>
internal interface IPerformanceOverlaySource
{
    event Action? Changed;

    IDisposable AcquireObservation();

    PerformanceOverlaySnapshot Snapshot();

    Task InvokeAsync(DescriptorRow row, CancellationToken cancellationToken = default);
}
