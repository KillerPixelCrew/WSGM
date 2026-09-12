using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PluginActionSequenceTests
{
    private static PluginActionStep Step(string id, int timeoutSeconds = 30) => new()
    {
        Plugin = new("wsgm.ir", "blaster"),
        ActionId = id,
        TimeoutSeconds = timeoutSeconds,
    };

    [Fact]
    public async Task EntryStopsAtTheFirstStepThatDidNotSucceed()
    {
        Invoker invoker = new()
        {
            { "one", PluginActionOutcome.Dispatched },
            { "two", PluginActionOutcome.Rejected },
            { "three", PluginActionOutcome.Dispatched },
        };

        var results = await new PluginActionSequence(invoker)
            .RunUntilFailureAsync([Step("one"), Step("two"), Step("three")], default);

        Assert.Equal(["one", "two"], invoker.Invoked);
        Assert.Equal(2, results.Count);
        Assert.False(results[1].Succeeded);
    }

    [Fact]
    public async Task LeaveRunsEveryStepAndReportsTheOnesThatFailed()
    {
        Invoker invoker = new()
        {
            { "one", PluginActionOutcome.Rejected },
            { "two", PluginActionOutcome.AppliedVerified },
            { "three", PluginActionOutcome.Unconfirmed },
        };

        var results = await new PluginActionSequence(invoker)
            .RunAllAsync([Step("one"), Step("two"), Step("three")], default);

        Assert.Equal(["one", "two", "three"], invoker.Invoked);
        Assert.Equal([false, true, false], results.Select(result => result.Succeeded));
    }

    [Fact]
    public async Task AStepThatMissesItsOwnDeadlineIsUnconfirmedAndTheSequenceGoesOn()
    {
        // A plugin that never answers must not be treated as "did nothing": the command may
        // already be on the wire, so compensation is owed and no retry is allowed.
        Invoker invoker = new() { { "slow", PluginActionOutcome.Dispatched } };
        invoker.Hang = "slow";

        var results = await new PluginActionSequence(invoker)
            .RunAllAsync([Step("slow", timeoutSeconds: 1), Step("after")], default);

        Assert.Equal(PluginActionOutcome.Unconfirmed, results[0].Outcome);
        Assert.Contains("may still take effect", results[0].Detail, StringComparison.Ordinal);
        Assert.True(PluginActionSequence.NeedsCompensation(results));
    }

    [Fact]
    public async Task AThrowingPluginIsUnconfirmedRatherThanFatal()
    {
        Invoker invoker = new() { { "boom", PluginActionOutcome.Dispatched } };
        invoker.Throw = "boom";

        var results = await new PluginActionSequence(invoker).RunAllAsync([Step("boom")], default);

        Assert.Equal(PluginActionOutcome.Unconfirmed, Assert.Single(results).Outcome);
    }

    [Fact]
    public void OnlyRejectionsProveNothingHappened()
    {
        PluginActionStepResult rejected = new(Step("a"), PluginActionOutcome.Rejected, "");
        PluginActionStepResult dispatched = new(Step("b"), PluginActionOutcome.Dispatched, "");

        Assert.False(PluginActionSequence.NeedsCompensation([rejected]));
        Assert.True(PluginActionSequence.NeedsCompensation([rejected, dispatched]));
        Assert.False(PluginActionSequence.NeedsCompensation([]));
    }

    private sealed class Invoker : IPluginActionInvoker, System.Collections.IEnumerable
    {
        private readonly Dictionary<string, PluginActionOutcome> _outcomes = [];

        internal List<string> Invoked { get; } = [];

        internal string? Hang { get; set; }

        internal string? Throw { get; set; }

        internal void Add(string actionId, PluginActionOutcome outcome) => _outcomes[actionId] = outcome;

        public System.Collections.IEnumerator GetEnumerator() => _outcomes.GetEnumerator();

        public async Task<PluginActionResult> InvokeAsync(
            PluginActionStep step, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            Invoked.Add(step.ActionId!);
            if (step.ActionId == Throw) { throw new InvalidOperationException("the endpoint went away"); }
            if (step.ActionId == Hang) { await Task.Delay(Timeout.Infinite, cancellationToken); }
            return new(Guid.NewGuid(), _outcomes.GetValueOrDefault(step.ActionId!, PluginActionOutcome.Rejected));
        }
    }
}
