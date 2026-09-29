using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Plugin.Sdk;

/// <summary>
///     Capability publication surface WSGM gives a common plugin that implements
///     <see cref="ICapabilityPlugin" />. It carries the Device SDK's descriptor, state and command model
///     without the device package's controller, OEM and haptic surfaces.
/// </summary>
/// <remarks>
///     Each publisher gets its own capability router, profile key and overlay section, so capability ids
///     only need to be unique within the package. The host stamps the publisher identity; nothing here
///     carries it.
/// </remarks>
public interface ICapabilityHost
{
    /// <summary>Current capability cycle generation. It changes on start and on every resume.</summary>
    long CycleGeneration { get; }

    /// <summary>Publishes an immutable replacement descriptor set for the current cycle.</summary>
    /// <param name="descriptors">Complete descriptor set; roles must be declared in the manifest.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after WSGM accepted it.</returns>
    ValueTask PublishDescriptorsAsync(CapabilityDescriptorSet descriptors, CancellationToken cancellationToken);

    /// <summary>Publishes one capability observation.</summary>
    /// <param name="state">Live semantic state for a published descriptor.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after WSGM accepted it.</returns>
    ValueTask PublishCapabilityStateAsync(CapabilityState state, CancellationToken cancellationToken);

    /// <summary>Writes one diagnostic line into WSGM's log. Never throws.</summary>
    /// <param name="level">How much the line matters.</param>
    /// <param name="scope">Subsystem producing it, used as the log prefix.</param>
    /// <param name="message">The line; truncated past <see cref="PluginTrace.MaxMessageLength" />.</param>
    void Trace(DeviceTraceLevel level, string scope, string message);

    /// <summary>Records a polled state, writing only when that key's value changed. Never throws.</summary>
    /// <param name="level">Level for the line when it is written.</param>
    /// <param name="scope">Subsystem producing the line.</param>
    /// <param name="key">Stable identity of the thing observed, unique within <paramref name="scope" />.</param>
    /// <param name="message">The current state.</param>
    void TraceChange(DeviceTraceLevel level, string scope, string key, string message);
}

/// <summary>
///     A common plugin that publishes capabilities, required for the <c>wsgm.gpu</c> category. WSGM routes
///     commands, remembers user changes per game and renders the controls as it does for the device
///     package.
/// </summary>
/// <remarks>
///     The plugin receives its <see cref="ICapabilityHost" /> through <see cref="IPluginHost.Capabilities" />
///     in <see cref="IPlugin.StartAsync" /> and publishes descriptors for each new cycle generation.
/// </remarks>
public interface ICapabilityPlugin
{
    /// <summary>Applies one semantic capability command after authoritative plugin revalidation.</summary>
    /// <param name="command">Semantic command from WSGM.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The truthful driver outcome. A failed write is indeterminate and never retried.</returns>
    ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Stores every game's overrides for <see cref="CapabilityProfileScope.NativePerApplication" />
    ///     capabilities in the driver's own per-application profiles.
    /// </summary>
    /// <param name="sync">The complete current set. Anything stored earlier and absent now is removed.</param>
    /// <param name="cancellationToken">Cancels the pass; the next sync carries the full set again.</param>
    /// <returns>What was written, removed and refused.</returns>
    /// <remarks>
    ///     WSGM calls this after start, on resume, whenever a profile or a learned executable changes, and
    ///     when a game starts. The plugin keeps the record of what it wrote in its state directory, so it
    ///     removes only entries it wrote and never touches anyone else's.
    /// </remarks>
    ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(
        ApplicationProfileSync sync,
        CancellationToken cancellationToken);
}

/// <summary>The complete per-application state WSGM wants the driver to hold.</summary>
/// <param name="Revision">Increasing revision; a plugin may skip a sync older than one it applied.</param>
/// <param name="CycleGeneration">Capability cycle the values were resolved against.</param>
/// <param name="Profiles">Every game with at least one executable and one native override.</param>
public sealed record ApplicationProfileSync(
    long Revision,
    long CycleGeneration,
    IReadOnlyList<ApplicationCapabilityProfile> Profiles);

/// <summary>One game's native overrides.</summary>
/// <param name="ProfileId">WSGM's stable game profile identity, for diagnostics and the plugin's record.</param>
/// <param name="DisplayName">Plain game name for diagnostics.</param>
/// <param name="Executables">Executable file names without paths, as the driver matches them.</param>
/// <param name="Values">The game's overrides, each for a published native per-application capability.</param>
public sealed record ApplicationCapabilityProfile(
    string ProfileId,
    string DisplayName,
    IReadOnlyList<string> Executables,
    IReadOnlyList<ApplicationCapabilityValue> Values);

/// <summary>One override.</summary>
/// <param name="CapabilityId">Published capability id.</param>
/// <param name="InstanceId">Published instance id, or null.</param>
/// <param name="Value">Value already validated against the descriptor.</param>
public sealed record ApplicationCapabilityValue(string CapabilityId, string? InstanceId, CapabilityValue Value);

/// <summary>Outcome of one sync.</summary>
/// <param name="Written">Entries written or confirmed.</param>
/// <param name="Removed">Entries removed because WSGM no longer wants them.</param>
/// <param name="Failures">Entries the driver refused; they wait for the next sync.</param>
public sealed record ApplicationProfileSyncResult(
    int Written,
    int Removed,
    IReadOnlyList<ApplicationProfileFailure> Failures);

/// <summary>One refused entry.</summary>
/// <param name="ProfileId">Game profile identity.</param>
/// <param name="Executable">Executable the entry was for.</param>
/// <param name="CapabilityId">Capability id.</param>
/// <param name="Detail">Bounded plain diagnostic text.</param>
public sealed record ApplicationProfileFailure(
    string ProfileId,
    string Executable,
    string CapabilityId,
    string Detail);
