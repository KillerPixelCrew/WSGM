using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CommonPluginActionTests
{
    private static DateTimeOffset Deadline => DateTimeOffset.UtcNow.AddSeconds(5);

    [Fact]
    public async Task NamedActionsDistinguishDispatchFromVerifiedExternalState()
    {
        PluginHost host = new(action => action());
        Provider plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        var result = await host.InvokeActionAsync(registration.Identity, 1, "send", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.SessionAutomation, Deadline, default);
        Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
        Assert.Equal(result.OperationId, plugin.Request!.OperationId);
        Assert.Equal(PluginActionOrigin.SessionAutomation, plugin.Request.Origin);
        Assert.Equal(new PluginValue(Number: 1), plugin.Request.Arguments["value"]);
        Assert.Equal(PluginUiKind.Slider, Assert.Single(registration.Actions!.Contributions).Kind);
        await Close(registration);
    }

    [Fact]
    public async Task InvalidArgumentsAndStaleGenerationDoNotDispatch()
    {
        PluginHost host = new(action => action());
        Provider plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        var result = await registration.InvokeActionAsync(1, "send", new Dictionary<string, PluginValue> { ["value"] = new(Number: 11) },
            PluginActionOrigin.User, Deadline, default);
        Assert.Equal(PluginActionOutcome.Rejected, result.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.InvokeActionAsync(2, "send", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, Deadline, default));
        Assert.Equal(0, plugin.Dispatches);
        Assert.False(registration.Quarantined);
        await Close(registration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrMismatchedRepliesStayUnconfirmedWithoutRetry(bool throws)
    {
        PluginHost host = new(action => action());
        Provider plugin = new() { Fail = throws, WrongIdentity = !throws };
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        var result = await registration.InvokeActionAsync(1, "send", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, Deadline, default);
        Assert.Equal(PluginActionOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(1, plugin.Dispatches);
        await Close(registration);
        Assert.Equal(1, plugin.Dispatches);
    }

    [Fact]
    public async Task StopCancelsTheActiveActionAndWaitsBeforeDisposal()
    {
        PluginHost host = new(action => action());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Provider plugin = new()
        {
            Work = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); },
        };
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, default);
        var action = registration.InvokeActionAsync(1, "send", new Dictionary<string, PluginValue>(), PluginActionOrigin.User, Deadline, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Close(registration);
        Assert.Equal(PluginActionOutcome.Unconfirmed, (await action).Outcome);
        Assert.True(plugin.Stopped);
        Assert.True(plugin.Disposed);
        Assert.Equal(1, plugin.Dispatches);
    }

    [Fact]
    public void UiContributionsCannotReferToMissingActionsOrMismatchedArgumentTypes()
    {
        var missing = new Provider { Ui = [new("quick", "Quick", "remote", PluginUiKind.Action, ActionId: "missing")] };
        Assert.Throws<ArgumentException>(() => new CommonPluginActions(missing));
        var wrongType = new Provider { Ui = [new("quick", "Quick", "remote", PluginUiKind.Toggle, "value", "send", "value")] };
        Assert.Throws<ArgumentException>(() => new CommonPluginActions(wrongType));
    }

    private static PluginRegistration Admit(PluginHost host, Provider plugin) => host.Admit(plugin, new(plugin.Id, "one"),
        PluginCategories.Infrared, PluginCategoryPolicy.Multiple, false, 1, "fixture-state");

    private static async Task Close(PluginRegistration registration)
    { Assert.True(await registration.StopAsync(Deadline, default)); await registration.DisposeAsync(); }

    private sealed class Provider : IPlugin, IPluginActions, IPluginUi
    {
        public string Id => "test.remote";
        public IReadOnlyList<PluginAction> Actions =>
            [new("send", "Send command", [new("value", "Value", PluginSettingKind.Number, new(Number: 1), 0, 10)])];
        public IReadOnlyList<PluginUiContribution> Contributions => Ui;
        internal IReadOnlyList<PluginUiContribution> Ui { get; init; } =
            [new("level", "Level", "remote", PluginUiKind.Slider, "value", "send", "value")];
        internal bool Fail { get; init; }
        internal bool WrongIdentity { get; init; }
        internal Func<CancellationToken, Task>? Work { get; init; }
        internal int Dispatches { get; private set; }
        internal PluginActionRequest? Request { get; private set; }
        internal bool Stopped { get; private set; }
        internal bool Disposed { get; private set; }
        public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken) => ValueTask.FromResult(PluginHealth.Ready);
        public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context, CancellationToken cancellationToken)
        {
            Dispatches++;
            Request = request;
            if (Fail) { throw new IOException("Fixture endpoint failure after dispatch"); }
            if (Work is not null) { await Work(cancellationToken); }
            return new(WrongIdentity ? Guid.NewGuid() : request.OperationId, PluginActionOutcome.Dispatched);
        }
        public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
        { Stopped = true; return ValueTask.FromResult(true); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
