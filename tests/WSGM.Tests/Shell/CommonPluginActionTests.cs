using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using static WSGM.Tests.Builders.PluginBuilders;

namespace WSGM.Tests.Shell;

public sealed class CommonPluginActionTests
{
    private static readonly PluginUiContribution Status = new("temperature", "Temperature", "device",
        PluginUiKind.Status, "temperature");

    [Fact]
    public async Task SessionAutomationInvokerUsesTheAdmittedInstanceAndItsCurrentGeneration()
    {
        PluginHost host = new(action => action());
        Provider plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, CancellationToken.None);
        try
        {
            var result = await new PluginHostActionInvoker(host).InvokeAsync(
                new PluginActionStep { Plugin = registration.Identity, ActionId = "send" },
                Deadline, CancellationToken.None);
            Assert.Equal(PluginActionOutcome.Dispatched, result.Outcome);
            Assert.Equal(PluginActionOrigin.SessionAutomation, plugin.Request!.Origin);
            Assert.Equal(1, plugin.Dispatches);
        }
        finally
        {
            await Close(registration);
        }
    }

    [Fact]
    public async Task NamedActionsDistinguishDispatchFromVerifiedExternalState()
    {
        PluginHost host = new(action => action());
        Provider plugin = new();
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, CancellationToken.None);
        var result = await host.InvokeActionAsync(registration.Identity, 1, "send",
            new Dictionary<string, PluginValue>(),
            PluginActionOrigin.SessionAutomation, Deadline, CancellationToken.None);
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
        await registration.StartAsync(Deadline, CancellationToken.None);
        var result = await registration.InvokeActionAsync(1, "send",
            new Dictionary<string, PluginValue> { ["value"] = new(Number: 11) },
            PluginActionOrigin.User, Deadline, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.Rejected, result.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.InvokeActionAsync(2, "send",
            new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, Deadline, CancellationToken.None));
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
        await registration.StartAsync(Deadline, CancellationToken.None);
        var result = await registration.InvokeActionAsync(1, "send", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, Deadline, CancellationToken.None);
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
            Work = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var registration = Admit(host, plugin);
        await registration.StartAsync(Deadline, CancellationToken.None);
        var action = registration.InvokeActionAsync(1, "send", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, Deadline, CancellationToken.None);
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
        var missing = new Provider
            { Ui = [new PluginUiContribution("quick", "Quick", "remote", PluginUiKind.Action, ActionId: "missing")] };
        Assert.Throws<ArgumentException>(() => new CommonPluginActions(missing));
        var wrongType = new Provider
        {
            Ui = [new PluginUiContribution("quick", "Quick", "remote", PluginUiKind.Toggle, "value", "send", "value")]
        };
        Assert.Throws<ArgumentException>(() => new CommonPluginActions(wrongType));

        var unsafeLabel = new Provider
        {
            Ui = [new PluginUiContribution("quick", "Quick‮label", "remote", PluginUiKind.Action, ActionId: "send")]
        };
        Assert.Throws<ArgumentException>(() => new CommonPluginActions(unsafeLabel));
    }

    [Fact]
    public void WidgetLinksAreCapturedWithoutRetainingMutablePluginLists()
    {
        List<string> links = ["temperature"];
        PluginWidget widget = new("thermal", "Thermals", links, NavigationCategory: "device");
        var captured = CommonPluginActions.CaptureWidgets([widget], [Status]);
        links[0] = "changed";
        Assert.Equal("temperature", Assert.Single(Assert.Single(captured).ContributionIds));
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets([widget], [Status]));
    }

    [Fact]
    public void DuplicateWidgetsAndUnknownNavigationAreRejected()
    {
        PluginWidget widget = new("thermal", "Thermals", ["temperature"]);
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets([widget, widget], [Status]));
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets(
            [widget with { NavigationCategory = "missing" }], [Status]));
        Assert.Throws<ArgumentException>(() => CommonPluginActions.CaptureWidgets(
            [widget with { ContributionIds = ["temperature", "temperature"] }], [Status]));
    }

    private sealed class Provider : IPlugin, IPluginActions, IPluginUi
    {
        internal IReadOnlyList<PluginUiContribution> Ui { get; init; } =
            [new("level", "Level", "remote", PluginUiKind.Slider, "value", "send", "value")];

        internal bool Fail { get; init; }
        internal bool WrongIdentity { get; init; }
        internal Func<CancellationToken, Task>? Work { get; init; }
        internal int Dispatches { get; private set; }
        internal PluginActionRequest? Request { get; private set; }
        internal bool Stopped { get; private set; }
        internal bool Disposed { get; private set; }
        public string Id => "test.remote";

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
            Stopped = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<PluginAction> Actions =>
        [
            new("send", "Send command",
                [new PluginSetting("value", "Value", PluginSettingKind.Number, new PluginValue(Number: 1), 0, 10)])
        ];

        public async ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request,
            PluginContext context, CancellationToken cancellationToken)
        {
            Dispatches++;
            Request = request;
            if (Fail)
            {
                throw new IOException("Fixture endpoint failure after dispatch");
            }

            if (Work is not null)
            {
                await Work(cancellationToken);
            }

            return new PluginActionResult(WrongIdentity ? Guid.NewGuid() : request.OperationId,
                PluginActionOutcome.Dispatched);
        }

        public IReadOnlyList<PluginUiContribution> Contributions => Ui;
    }
}
