using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>The first message sent by a newly spawned DeviceHost.</summary>
public sealed record DeviceHostHello
{
    /// <summary>One-time launch nonce, encoded as Base64.</summary>
    public required string Nonce { get; init; }

    /// <summary>Validated package the host was asked to load.</summary>
    public required string PackageId { get; init; }

    /// <summary>Package version the host found on disk.</summary>
    public required string PackageVersion { get; init; }

    /// <summary>Runtime version of the DeviceHost executable.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Interactive session the host belongs to.</summary>
    public required uint SessionId { get; init; }

    /// <summary>Fresh process/reconnect cycle generation assigned by the coordinator.</summary>
    public required long CycleGeneration { get; init; }
}

/// <summary>The coordinator's bounded answer to <see cref="DeviceHostHello"/>.</summary>
public sealed record DeviceHostHelloAck
{
    /// <summary>Whether the host may continue loading the package.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Expected package identifier, used to detect launch confusion.</summary>
    public required string PackageId { get; init; }

    /// <summary>Human-readable refusal detail with no secrets.</summary>
    public string? Detail { get; init; }
}

/// <summary>Starts exact detection and one process-long plugin activation.</summary>
public sealed record DeviceStartRequest
{
    /// <summary>Normalized machine identity observed by WSGM.</summary>
    public required DeviceIdentitySnapshot Identity { get; init; }

    /// <summary>Process/reconnect cycle generation assigned to this start.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Whether the plugin should acquire the physical controller.</summary>
    public required bool ControllerManagementEnabled { get; init; }

    /// <summary>UTC deadline for detection and activation.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>A bounded lifecycle request carrying only a deadline.</summary>
public sealed record DeviceLifecycleRequest
{
    /// <summary>UTC deadline after which the host must stop waiting.</summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>Device generation to use after resume.</summary>
    public long? CycleGeneration { get; init; }
}

/// <summary>Requests terminal plugin restoration and release.</summary>
public sealed record DeviceStopRequest
{
    /// <summary>Why the device cycle is ending.</summary>
    public required DeviceStopReason Reason { get; init; }

    /// <summary>UTC cleanup deadline.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>Wire-safe reason for terminal plugin deactivation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceStopReason>))]
public enum DeviceStopReason
{
    /// <summary>WSGM is exiting normally.</summary>
    WsgmExiting,

    /// <summary>The user disabled Device Integration.</summary>
    IntegrationDisabled,

    /// <summary>WSGM is updating under a compressed cleanup budget.</summary>
    Updating,

    /// <summary>The interactive session is ending.</summary>
    SessionEnding,

    /// <summary>WSGM is being uninstalled after a bounded restoration attempt.</summary>
    Uninstalling,

    /// <summary>A caller canceled startup after the plugin may already have acquired hardware.</summary>
    StartCanceled,

    /// <summary>Startup failed after the plugin may already have acquired hardware.</summary>
    StartFailed,
}

/// <summary>Reports the host's current process-long lifecycle state.</summary>
public sealed record DeviceLifecycleNotification
{
    /// <summary>Current cycle state.</summary>
    public required DeviceCycleState State { get; init; }

    /// <summary>Current process/reconnect cycle generation.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Matched device definition when detection succeeded.</summary>
    public string? DeviceDefinitionId { get; init; }

    /// <summary>Structured reason when the state is passive or degraded.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Closed physical-device identity publication.</summary>
public sealed record DevicePhysicalIdentitiesNotification
{
    /// <summary>Interfaces owned by the plugin.</summary>
    public IReadOnlyList<PhysicalDeviceIdentity> Devices { get; init; } = [];

    /// <summary>What the physical controller can do with haptic output, when it has any.</summary>
    /// <remarks>
    /// Travels with the identities rather than in its own message because it describes the same
    /// controller and changes at the same moments. Absent means the plugin drives no haptics, which
    /// is not the same as a device whose channels are all unsupported: WSGM sends no output frames
    /// at all rather than sending frames that the plugin silently discards.
    /// </remarks>
    public HapticCapabilities? Output { get; init; }
}

/// <summary>Closed OEM-control descriptor publication.</summary>
public sealed record DeviceOemControlsNotification
{
    /// <summary>Logical controls assignable by WSGM.</summary>
    public IReadOnlyList<OemControlDescriptor> Controls { get; init; } = [];
}

/// <summary>Identifies one in-flight semantic command to cancel.</summary>
public sealed record DeviceCancelCommandRequest
{
    /// <summary>Command identifier originally carried by <see cref="CapabilityCommand"/>.</summary>
    public required Guid CommandId { get; init; }
}

/// <summary>Requests a controller-only or full topology handoff.</summary>
public sealed record DeviceControllerHandoffRequest
{
    /// <summary>Whether only the controller or the whole cycle is being released.</summary>
    public required HandoffScope Scope { get; init; }

    /// <summary>UTC deadline for verified restoration.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>Changes controller ownership without ending the device cycle.</summary>
public sealed record DeviceControllerManagementRequest
{
    /// <summary>Whether physical acquisition should become active.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Fresh device generation for a new acquisition.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>UTC deadline for acquisition or release.</summary>
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>Wire form of the plugin's controller release acknowledgment.</summary>
public sealed record DeviceControllerHandoffResponse
{
    /// <summary>Furthest step completed.</summary>
    public required ControllerHandoffStep Step { get; init; }

    /// <summary>Whether restoration was verified.</summary>
    public required ControllerHandoffResult Result { get; init; }

    /// <summary>Physical identities observed after release.</summary>
    public IReadOnlyList<PhysicalDeviceIdentity> ReleasedDevices { get; init; } = [];
}

/// <summary>Empty read-only diagnostics request.</summary>
public sealed record DeviceDiagnosticsRequest;

/// <summary>Bounded acknowledgment for a semantic request with no richer result.</summary>
public sealed record DeviceOperationAck
{
    /// <summary>Whether the operation was accepted and completed.</summary>
    public required bool Completed { get; init; }

    /// <summary>Sanitized detail when completion was refused.</summary>
    public string? Detail { get; init; }
}

/// <summary>A bounded structured protocol error.</summary>
public sealed record DeviceProtocolError
{
    /// <summary>Stable error code for diagnostics and tests.</summary>
    public required string Code { get; init; }

    /// <summary>Sanitized human-readable detail.</summary>
    public required string Detail { get; init; }

    /// <summary>Whether the peer may continue using the connection.</summary>
    public required bool Recoverable { get; init; }
}
