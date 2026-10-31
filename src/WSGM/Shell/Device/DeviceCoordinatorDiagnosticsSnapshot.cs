using System;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Bounded read-only view exposed by the resident device coordinator.</summary>
internal sealed record DeviceCoordinatorDiagnosticsSnapshot
{
    public required DeviceCycleState State { get; init; }

    public HandheldDiagnostic? Handheld { get; init; }

    public required int CapabilityCount { get; init; }

    public required int AvailableCapabilityCount { get; init; }

    public required int FaultedCapabilityCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}
