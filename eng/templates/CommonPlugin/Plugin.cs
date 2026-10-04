using WSGM.Plugin.Sdk;

namespace ExamplePlugin.Common;

// No external resources are acquired by this constructor or example.
public sealed class Plugin : IPlugin, IPluginActions, IPluginUi
{
    private IPluginHost? _host;
    private long _sequence;
    private int _count;

    public string Id => "__PLUGIN_ID__";
    public IReadOnlyList<PluginAction> Actions => [new("increment", "Increment example counter", [])];
    public IReadOnlyList<PluginUiContribution> Contributions =>
    [
        new("counter", "Example counter", "example", PluginUiKind.Status, StateKey: "count"),
        new("increment", "Increment", "example", PluginUiKind.Action, ActionId: "increment")
    ];

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _host = host;
        Publish(context, PluginStateOrigin.Initialization);
        return ValueTask.FromResult(PluginHealth.Ready);
    }

    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(PluginContext context, CancellationToken token)
    {
        Publish(context, PluginStateOrigin.Initialization);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.ActionId != "increment")
        {
            return ValueTask.FromResult(new PluginActionResult(request.OperationId, PluginActionOutcome.Rejected));
        }

        _count++;
        Publish(context, PluginStateOrigin.Action, request.OperationId);
        return ValueTask.FromResult(new PluginActionResult(request.OperationId, PluginActionOutcome.AppliedVerified));
    }

    private void Publish(PluginContext context, PluginStateOrigin origin, Guid? operation = null)
    {
        _host?.PublishState(new PluginStatePublication(context.Instance, context.Generation, ++_sequence, "count",
            new PluginValue(Number: _count), origin, OperationId: operation));
    }

    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken token)
    {
        _host = null;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        _host = null;
        return ValueTask.CompletedTask;
    }
}
