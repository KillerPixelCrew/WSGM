using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Plugin.Sdk;

/// <summary>The resident session's current product mode.</summary>
public enum PluginSessionMode
{
    /// <summary>Explorer desktop with WSGM resident.</summary>
    Desktop,
    /// <summary>WSGM Game Mode.</summary>
    Game,
}

/// <summary>Stable identity for an installed plugin instance.</summary>
/// <param name="PluginId">Manifest package identity.</param>
/// <param name="InstanceId">Host-assigned instance identity within the package.</param>
public sealed record PluginInstanceIdentity(string PluginId, string InstanceId);

/// <summary>Lifecycle scope supplied by the host. A changed generation retires previous publications.</summary>
/// <param name="Instance">Host-selected plugin instance.</param>
/// <param name="Generation">Positive host-owned lifecycle generation.</param>
/// <param name="Mode">Current resident-session mode.</param>
/// <param name="Deadline">Deadline for cooperative completion; timeout does not prove work stopped.</param>
/// <param name="StateDirectory">Host-assigned private state directory, not a security boundary.</param>
public sealed record PluginContext(PluginInstanceIdentity Instance, long Generation, PluginSessionMode Mode,
    DateTimeOffset Deadline, string StateDirectory);

/// <summary>Observable plugin health independent of any device capability.</summary>
public enum PluginHealth
{
    /// <summary>Ready to receive supported operations.</summary>
    Ready,
    /// <summary>Loaded but waiting for an external prerequisite.</summary>
    Unavailable,
    /// <summary>A failure requires host intervention or explicit restart.</summary>
    Failed,
}

/// <summary>A generation-scoped health publication.</summary>
/// <param name="Instance">Publishing instance.</param>
/// <param name="Generation">Lifecycle generation supplied by the host.</param>
/// <param name="Health">Current aggregate health.</param>
/// <param name="Detail">Bounded plain diagnostic text.</param>
public sealed record PluginHealthPublication(PluginInstanceIdentity Instance, long Generation, PluginHealth Health, string? Detail);

/// <summary>Common publication boundary. Calls may originate off-thread; the host validates and dispatches UI state.</summary>
public interface IPluginHost
{
    /// <summary>Publishes health for the admitted instance and generation.</summary>
    /// <param name="publication">Health observation; stale generations must be discarded.</param>
    void PublishHealth(PluginHealthPublication publication);
    /// <summary>Publishes effective state without altering desired configuration.</summary>
    /// <param name="publication">Origin-tagged observation with an increasing generation-scoped sequence.</param>
    void PublishState(PluginStatePublication publication) { }
}

/// <summary>Common lifecycle shared by Device adapters and independent plugins.</summary>
/// <remarks>Accepted plugins currently run in-process. Cancellation requires cooperative unwind, not automatic retry.</remarks>
public interface IPlugin : IAsyncDisposable
{
    /// <summary>Package identity, which must match the admitted manifest.</summary>
    string Id { get; }
    /// <summary>Starts one generation, unwinding acquired resources if startup fails.</summary>
    /// <param name="host">Generation-validating host publication boundary.</param>
    /// <param name="context">Instance, mode and deadline.</param>
    /// <param name="cancellationToken">Cancels startup and requires cooperative cleanup.</param>
    /// <returns>Initial plugin health.</returns>
    ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken);
    /// <summary>Receives a Desktop/Game transition without unloading the resident plugin.</summary>
    /// <param name="context">Current mode, generation and deadline.</param>
    /// <param name="cancellationToken">Cancels waiting; obsolete work must be retired.</param>
    /// <returns>Completion after the plugin has handled the transition.</returns>
    ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken);
    /// <summary>Quiesces external work for system suspend. Plugins without suspend work may keep the default.</summary>
    /// <param name="context">Current generation and quiescence deadline.</param>
    /// <param name="cancellationToken">Cancels waiting without proving work stopped.</param>
    /// <returns>Completion after quiescence.</returns>
    ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Revalidates resources after system resume into a new host generation.</summary>
    /// <param name="context">New generation and resume deadline.</param>
    /// <param name="cancellationToken">Cancels resume.</param>
    /// <returns>Completion after revalidation; publish updated health as needed.</returns>
    ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Stops publication and releases resources before disposal.</summary>
    /// <param name="context">Stopping generation and cleanup deadline.</param>
    /// <param name="cancellationToken">Cancels waiting without claiming cleanup succeeded.</param>
    /// <returns>True only when resource release was confirmed.</returns>
    ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken);
}
