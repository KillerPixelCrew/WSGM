using System;
using System.Collections.Generic;

namespace WSGM.Device.Sdk.Lifecycle;

/// <summary>A bounded read-only snapshot of the active plugin cycle.</summary>
public sealed record DeviceDiagnosticsSnapshot
{
    /// <summary>Active package identifier.</summary>
    public required string PackageId { get; init; }

    /// <summary>Matched device definition.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public required DeviceCycleState CycleState { get; init; }

    /// <summary>Process/reconnect cycle generation producing the snapshot.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Bounded plugin-owned service and recovery facts.</summary>
    public IReadOnlyDictionary<string, string> PluginValues { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>When the snapshot was captured, in UTC.</summary>
    public required DateTimeOffset CapturedAt { get; init; }
}
