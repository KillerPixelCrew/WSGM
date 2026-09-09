using WindowsDeviceControl;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DisplayRouteTransitionTests
{
    [Fact]
    public async Task CancelledNativeWriteBlocksNewRouteUntilItSettles()
    {
        TaskCompletionSource<DisplayProfileResult> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Backend backend = new() { ProfileWork = pending.Task };
        DisplayRouteTransition transition = new(backend);
        using CancellationTokenSource cancellation = new();
        var first = transition.RunAsync(Plan, false, cancellation.Token);
        Assert.Equal(["profile"], backend.Calls);
        cancellation.Cancel();
        Assert.False((await first).Completed);
        var blocked = await transition.RunAsync(Plan, true, default);
        Assert.False(blocked.Completed);
        Assert.Equal("previous operation", blocked.Stage);
        Assert.Equal(["profile"], backend.Calls);
        pending.SetResult(new(true, 0, false, false, "Settled"));
        Assert.True((await transition.RunAsync(Plan, true, default)).Completed);
        Assert.Equal(["profile", "action", "wait", "profile"], backend.Calls);
    }

    private static readonly DisplayRoutePlan Plan = new(
        new(new("test", "default"), "route", new Dictionary<string, PluginValue>()),
        new("monitor", null, null, "TV", 0, 0, 1), new(1, [], [], []), TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(true, "action,wait,profile")]
    [InlineData(false, "profile,action")]
    public async Task OrdersRouteAndProfileForTransitionDirection(bool entering, string expected)
    {
        Backend backend = new();
        var result = await new DisplayRouteTransition(backend).RunAsync(Plan, entering, default);
        Assert.True(result.Completed);
        Assert.Equal(expected, string.Join(',', backend.Calls));
    }

    [Fact]
    public async Task DispatchedRouteStillRequiresTargetBeforeProfile()
    {
        Backend backend = new() { ActionOutcome = PluginActionOutcome.Dispatched };
        var result = await new DisplayRouteTransition(backend).RunAsync(Plan, true, default);
        Assert.True(result.Completed);
        Assert.Equal(["action", "wait", "profile"], backend.Calls);
    }

    [Fact]
    public async Task MissingTargetDoesNotApplyProfileOrRepeatAction()
    {
        Backend backend = new() { Present = false };
        var result = await new DisplayRouteTransition(backend).RunAsync(Plan, true, default);
        Assert.False(result.Completed);
        Assert.Equal("target display", result.Stage);
        Assert.Equal(["action", "wait"], backend.Calls);
    }

    [Fact]
    public async Task FailedDesktopProfileDoesNotSwitchAway()
    {
        Backend backend = new() { Applied = false };
        var result = await new DisplayRouteTransition(backend).RunAsync(Plan, false, default);
        Assert.False(result.Completed);
        Assert.Equal(["profile"], backend.Calls);
    }

    [Fact]
    public async Task UnconfirmedActionStopsEntryWithoutRetry()
    {
        Backend backend = new() { ActionOutcome = PluginActionOutcome.Unconfirmed };
        var result = await new DisplayRouteTransition(backend).RunAsync(Plan, true, default);
        Assert.False(result.Completed);
        Assert.Equal(["action"], backend.Calls);
    }

    [Fact]
    public async Task CancelledTransitionDoesNotInvokeAnAction()
    {
        Backend backend = new();
        var result = await new DisplayRouteTransition(backend).RunAsync(Plan, true, new CancellationToken(true));
        Assert.False(result.Completed);
        Assert.Empty(backend.Calls);
    }

    private sealed class Backend : IDisplayRouteBackend
    {
        internal List<string> Calls { get; } = [];
        internal bool Present { get; init; } = true;
        internal bool Applied { get; init; } = true;
        internal PluginActionOutcome ActionOutcome { get; init; } = PluginActionOutcome.AppliedVerified;
        internal Task<DisplayProfileResult>? ProfileWork { get; init; }
        public Task<PluginActionResult> InvokeAsync(DisplayRouteAction action, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            Calls.Add("action");
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), ActionOutcome));
        }
        public Task<DisplayWaitOutcome> WaitAsync(DisplayTargetIdentity target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("wait");
            return Task.FromResult(Present ? DisplayWaitOutcome.Present : DisplayWaitOutcome.TimedOut);
        }
        public Task<DisplayProfileResult> ApplyAsync(DisplayProfile profile, CancellationToken cancellationToken)
        {
            Calls.Add("profile");
            return ProfileWork ?? Task.FromResult(new DisplayProfileResult(Applied, 0, false, false, "Test outcome"));
        }
    }
}
