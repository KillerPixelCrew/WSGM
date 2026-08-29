using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Sdk.Plugin;

/// <summary>The device-package entry point loaded by one DeviceHost process.</summary>
/// <remarks>
/// The interface is semantic at the host boundary. Implementations own their hardware transports
/// internally; none of those transports or handles can be returned through this API.
/// </remarks>
public interface IDevicePlugin : IAsyncDisposable
{
    /// <summary>Stable package identifier from <c>plugin.wsgm.json</c>.</summary>
    string PackageId { get; }

    /// <summary>Detects an exact supported device without acquiring a mutable resource.</summary>
    /// <param name="context">Normalized host observations.</param>
    /// <param name="cancellationToken">Cancels detection.</param>
    /// <returns>Exact detection outcome.</returns>
    ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken);

    /// <summary>Begins one process-long device cycle.</summary>
    /// <param name="context">Host adapter, cycle generation, and package-owned state directory.</param>
    /// <param name="cancellationToken">Cancels startup and requires acquired services to unwind.</param>
    /// <returns>The plugin's aggregate operational state after startup.</returns>
    ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken);

    /// <summary>Applies one semantic capability command after authoritative plugin revalidation.</summary>
    /// <param name="command">Semantic command from WSGM.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The truthful hardware outcome.</returns>
    ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    /// <summary>Quiesces volatile work for suspend or session lock.</summary>
    /// <param name="context">Bounded quiescence deadline.</param>
    /// <param name="cancellationToken">Cancels waiting at the deadline.</param>
    /// <returns>A task completing when no new long operation can begin.</returns>
    ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken cancellationToken);

    /// <summary>Revalidates identity and reacquires resources after resume.</summary>
    /// <param name="context">New device generation and deadline.</param>
    /// <param name="cancellationToken">Cancels resume.</param>
    /// <returns>The plugin's aggregate operational state after resume.</returns>
    ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken);

    /// <summary>Returns bounded, plugin-owned diagnostic facts without exposing transports.</summary>
    /// <param name="cancellationToken">Cancels diagnostic collection.</param>
    /// <returns>Current service and recovery details.</returns>
    ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>Applies canonical virtual-target output to the physical device.</summary>
    /// <param name="frame">Bounded semantic haptic frame.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>A task completing when the frame was handled or explicitly dropped.</returns>
    ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken);

    /// <summary>Stops controller acquisition and restores its original topology.</summary>
    /// <param name="context">Handoff scope and deadline.</param>
    /// <param name="cancellationToken">Cancels waiting while still requiring best-effort cleanup.</param>
    /// <returns>Verified or explicitly unverified release.</returns>
    ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken);

    /// <summary>Enables or disables physical-controller ownership while other resources continue.</summary>
    /// <param name="context">Wanted state, fresh generation, and deadline.</param>
    /// <param name="cancellationToken">Cancels acquisition or bounded release.</param>
    /// <returns>A task completing after controller resource state was republished.</returns>
    ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken);

    /// <summary>Restores and releases every remaining resource.</summary>
    /// <param name="context">Terminal reason and cleanup deadline.</param>
    /// <param name="cancellationToken">Cancels waiting at the host deadline.</param>
    /// <returns>Whether cleanup was verified, unverified, or failed.</returns>
    ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken);
}

/// <summary>Read-only observations supplied for exact device detection.</summary>
public sealed record PluginDetectionContext
{
    /// <summary>Normalized machine and device identity.</summary>
    public required DeviceIdentitySnapshot Identity { get; init; }

}

/// <summary>Outcome of exact plugin detection.</summary>
public sealed record PluginDetectionResult
{
    /// <summary>Whether an exact device definition matched.</summary>
    public required bool Matched { get; init; }

    /// <summary>Matched device definition, present only when <see cref="Matched"/> is true.</summary>
    public string? DeviceDefinitionId { get; init; }

    /// <summary>Why detection failed or stayed passive.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Inputs to one process-long start.</summary>
public sealed record PluginStartContext
{
    /// <summary>Semantic publication surface implemented by DeviceHost.</summary>
    public required IPluginHostAdapter Host { get; init; }

    /// <summary>Cycle generation owning all handles opened during startup.</summary>
    public required long CycleGeneration { get; init; }

    /// <summary>Exact matched device definition.</summary>
    public required string DeviceDefinitionId { get; init; }

    /// <summary>Private writable directory for package-owned durable state.</summary>
    /// <remarks>
    /// The plugin owns the schema and atomicity of every file below this directory. The host does
    /// not interpret or merge those files, and the plugin must keep them bounded.
    /// </remarks>
    public required string StateDirectory { get; init; }

    /// <summary>Whether physical-controller acquisition should be attempted.</summary>
    public required bool ControllerManagementEnabled { get; init; }
}

/// <summary>The plugin's aggregate state after starting or resuming its direct services.</summary>
public sealed record PluginStartResult
{
    /// <summary>Whether services are active, passive, or partly degraded.</summary>
    public required PluginOperationalState State { get; init; }

    /// <summary>Structured reason when the aggregate state is not active.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Aggregate result of starting or resuming direct plugin services.</summary>
public enum PluginOperationalState
{
    /// <summary>No mutable service could be acquired.</summary>
    Passive,

    /// <summary>Every required service started successfully.</summary>
    Active,

    /// <summary>At least one service is usable and at least one is unavailable.</summary>
    Degraded,
}

/// <summary>Bounded plugin-owned diagnostic facts.</summary>
public sealed record PluginDiagnostics
{
    /// <summary>Short stable keys and sanitized values for current service and recovery state.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Truthful aggregate result of terminal plugin cleanup.</summary>
public sealed record PluginStopResult
{
    /// <summary>Whether restoration was verified, unverified, or failed.</summary>
    public required PluginStopStatus Status { get; init; }

    /// <summary>Structured detail when restoration was not fully verified.</summary>
    public CapabilityReason? Reason { get; init; }
}

/// <summary>Aggregate terminal cleanup status.</summary>
public enum PluginStopStatus
{
    /// <summary>Every changed temporary state was restored and verified.</summary>
    Clean,

    /// <summary>Cleanup ran, but at least one restoration could not be confirmed.</summary>
    Unverified,

    /// <summary>At least one restoration failed.</summary>
    Failed,
}

/// <summary>Bounded suspend or lock quiescence.</summary>
/// <param name="Deadline">UTC deadline after which the host stops waiting.</param>
public sealed record PluginQuiesceContext(DateTimeOffset Deadline);

/// <summary>Bounded resume into a fresh cycle generation.</summary>
/// <param name="CycleGeneration">Generation that all newly opened handles belong to.</param>
/// <param name="Deadline">UTC deadline after which the host stops waiting.</param>
public sealed record PluginResumeContext(long CycleGeneration, DateTimeOffset Deadline);

/// <summary>Controller-only or full release request.</summary>
/// <param name="Scope">Whether the process-long device cycle continues.</param>
/// <param name="Deadline">UTC deadline for stopping acquisition and restoring topology.</param>
public sealed record PluginControllerReleaseContext(HandoffScope Scope, DateTimeOffset Deadline);

/// <summary>Controller-only ownership transition inside a continuing device cycle.</summary>
/// <param name="Enabled">Whether physical acquisition should be active.</param>
/// <param name="CycleGeneration">Fresh generation for handles opened while enabling.</param>
/// <param name="Deadline">UTC transition deadline.</param>
public sealed record PluginControllerManagementContext(
    bool Enabled,
    long CycleGeneration,
    DateTimeOffset Deadline);

/// <summary>What the plugin established while releasing its physical controller.</summary>
public sealed record PluginControllerRelease
{
    /// <summary>Furthest handoff step the plugin completed.</summary>
    public required ControllerHandoffStep Step { get; init; }

    /// <summary>Whether the original mode and resulting topology were verified.</summary>
    public required ControllerHandoffResult Result { get; init; }

    /// <summary>Physical identities observed after release.</summary>
    public IReadOnlyList<PhysicalDeviceIdentity> ReleasedDevices { get; init; } = [];
}

/// <summary>Why one process-long device cycle is ending.</summary>
public enum PluginStopReason
{
    /// <summary>WSGM is exiting normally.</summary>
    WsgmExiting,

    /// <summary>The user disabled Device Integration.</summary>
    IntegrationDisabled,

    /// <summary>WSGM is updating and uses the compressed cleanup budget.</summary>
    Updating,

    /// <summary>The interactive session is ending.</summary>
    SessionEnding,

    /// <summary>WSGM is being uninstalled after a bounded restoration attempt.</summary>
    Uninstalling,

    /// <summary>The startup owner canceled after plugin activation may have begun.</summary>
    StartCanceled,

    /// <summary>Startup failed after plugin activation may have begun.</summary>
    StartFailed,
}

/// <summary>Terminal cleanup request.</summary>
/// <param name="Reason">Why the cycle is ending.</param>
/// <param name="Deadline">UTC deadline for plugin-owned restoration.</param>
public sealed record PluginStopContext(
    PluginStopReason Reason,
    DateTimeOffset Deadline);
