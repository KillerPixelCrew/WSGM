using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Sdk.Tests.Services;

/// <summary>The gate both first-party packages run commands, transitions and observation behind.</summary>
/// <remarks>A failed post-command publication is traced through the process-wide sink, hence the collection.</remarks>
[Collection("plugin-trace")]
public sealed class DeviceCommandSerializerTests
{
    private const string Scenario = "power.scenario";
    private const string Sustained = "power.primary-limit";
    private readonly List<string> _calls = [];
    private bool _accepting = true;
    private Func<CancellationToken, ValueTask> _publish = _ => ValueTask.CompletedTask;

    [Fact]
    public async Task DisposalDoesNotInvalidateAnInFlightTransitionOrItsQueuedSuccessor()
    {
        using var serializer = Create();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = serializer.RunAsync(async () => await release.Task, CancellationToken.None).AsTask();
        var followed = false;
        var next = serializer.RunAsync(() =>
        {
            followed = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).AsTask();
        try
        {
            Assert.False(next.IsCompleted);
            serializer.Dispose();
            release.SetResult();
            await Task.WhenAll(first, next);
            Assert.True(followed);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
    }

    [Fact]
    public async Task PostCommandPublicationIsCancelledByTheCommandsActiveDeadline()
    {
        CancellationToken observed = default;
        _publish = async token =>
        {
            observed = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var serializer = Create();
        var command = Command(Scenario) with { Deadline = Deadline.After(TimeSpan.FromMilliseconds(100)) };

        var result = await serializer.ExecuteAsync(command, Applied, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(CapabilityReasonCode.HostUnavailable, result.Reason?.Code);
    }

    [Fact]
    public async Task AnAlreadyExpiredCommandSkipsItsPostCommandRefresh()
    {
        using var serializer = Create();
        var command = Command(Scenario) with { Deadline = Deadline.Expired };

        var result = await serializer.ExecuteAsync(command, Applied, CancellationToken.None);

        Assert.Equal(["execute"], _calls);
        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public async Task ACommandIsRefusedWhileTheCycleIsNotAccepting()
    {
        _accepting = false;
        using var serializer = Create();

        var result = await serializer.ExecuteAsync(Command(Sustained), Applied, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.Quiescing, result.Reason?.Code);
        Assert.Contains("Test device cycle", result.Reason?.Detail);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task AnAppliedCommandRefreshesItsCapabilityAndRepublishes()
    {
        _publish = _ =>
        {
            _calls.Add("publish");
            return ValueTask.CompletedTask;
        };
        using var serializer = Create();

        var result = await serializer.ExecuteAsync(Command(Sustained), Applied, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
        Assert.Equal(["execute", "refresh " + Sustained, "publish"], _calls);
    }

    [Fact]
    public async Task ARejectedCommandIsNotRepublished()
    {
        using var serializer = Create();

        var result = await serializer.ExecuteAsync(
            Command(Sustained),
            _ =>
            {
                _calls.Add("execute");
                return ValueTask.FromResult(CommandResults.Rejected(
                    Command(Sustained),
                    CapabilityReasonCode.ValueOutOfRange,
                    "Out of range."));
            },
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(["execute"], _calls);
    }

    [Fact]
    public async Task AScenarioWhoseLimitsCannotBePublishedIsIndeterminate()
    {
        // A scenario change resets the power limits, and a host orders its next watt writes from them.
        _publish = _ => throw new IOException("The host is gone.");
        using var serializer = Create();

        var result = await serializer.ExecuteAsync(Command(Scenario), Applied, CancellationToken.None);

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(CapabilityReasonCode.HostUnavailable, result.Reason?.Code);
        Assert.Null(result.ReadbackValue);
    }

    [Fact]
    public async Task AnotherCommandKeepsItsResultWhenThePublicationFails()
    {
        _publish = _ => throw new IOException("The host is gone.");
        using var serializer = Create();

        var result = await serializer.ExecuteAsync(Command(Sustained), Applied, CancellationToken.None);

        Assert.Equal(CommandOutcome.AppliedVerified, result.Outcome);
    }

    [Fact]
    public async Task ACommandWaitsForATransitionAndIsRefusedWhenItQuiesced()
    {
        using var serializer = Create();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = serializer.RunAsync(async () =>
        {
            _accepting = false;
            await release.Task;
        }, CancellationToken.None).AsTask();

        // Admitted before the transition marked the cycle quiescing, then held at the gate.
        _accepting = true;
        var command = serializer.ExecuteAsync(Command(Sustained), Applied, CancellationToken.None).AsTask();
        _accepting = false;
        Assert.False(command.IsCompleted);

        release.SetResult();
        await transition;
        var result = await command;

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Contains("started quiescing", result.Reason?.Detail);
        Assert.Empty(_calls);
    }

    private DeviceCommandSerializer Create()
    {
        CapabilityDescriptorSet surface = new()
        {
            Generation = 1,
            CycleGeneration = 1,
            Descriptors =
            [
                Descriptor(Scenario, CapabilityRole.ScenarioMode, CapabilityValueKind.Choice),
                Descriptor(Sustained, CapabilityRole.PowerSustainedLimit, CapabilityValueKind.Integer)
            ]
        };
        return new DeviceCommandSerializer(
            "Test",
            () => _accepting ? surface : null,
            (capabilityId, _) =>
            {
                _calls.Add("refresh " + capabilityId);
                return ValueTask.CompletedTask;
            },
            token => _publish(token));
    }

    private ValueTask<CapabilityCommandResult> Applied(CancellationToken cancellationToken)
    {
        _calls.Add("execute");
        return ValueTask.FromResult(CommandResults.Verified(Command(Sustained), CapabilityValue.Integer(15)));
    }

    private static CapabilityCommand Command(string capabilityId)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.Empty,
            CapabilityId = capabilityId,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = 1,
            Deadline = Deadline.After(TimeSpan.FromSeconds(10))
        };
    }

    private static CapabilityDescriptor Descriptor(string id, CapabilityRole role, CapabilityValueKind kind)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = role,
            ValueKind = kind,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = id },
            Persistence = CapabilityPersistence.Volatile
        };
    }
}
