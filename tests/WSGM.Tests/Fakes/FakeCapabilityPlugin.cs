using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Tests.Fakes;

/// <summary>A graphics plugin that applies every command unverified and records its syncs.</summary>
/// <param name="id">The plugin id.</param>
internal sealed class FakeCapabilityPlugin(string id) : IPlugin, ICapabilityPlugin
{
    internal int Commands { get; private set; }

    /// <summary>The capability of every command, in the order they arrived.</summary>
    internal List<string> Received { get; } = [];

    /// <summary>Runs inside each command before it answers, such as publishing the state it produced.</summary>
    internal Func<CapabilityCommand, ValueTask>? OnCommand { get; set; }

    /// <summary>What <see cref="StopAsync" /> answers; false leaves the stop unconfirmed.</summary>
    internal bool Released { get; init; } = true;

    /// <summary>Whether every sync reports one entry the driver refused.</summary>
    internal bool RefuseSyncs { get; init; }

    internal List<ApplicationProfileSync> Syncs { get; } = [];

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        Commands++;
        Received.Add(command.CapabilityId);
        if (OnCommand is { } onCommand)
        {
            await onCommand(command);
        }

        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedUnverified,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        Syncs.Add(sync);
        return ValueTask.FromResult(new ApplicationProfileSyncResult(sync.Profiles.Count, 0,
            RefuseSyncs ? [new ApplicationProfileFailure("game", "game.exe", "graphics.toggle", "Refused.")] : []));
    }

    public string Id => id;

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(PluginHealth.Ready);
    }

    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Released);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
