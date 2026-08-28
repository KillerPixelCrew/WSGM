using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Sdk.Generators.Tests;

public sealed class CapabilityRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_RevalidatesEveryCommandBeforeCallingHandler()
    {
        int revalidations = 0;
        int handlerCalls = 0;
        CapabilityRegistry registry = CreateRegistry(
            _ =>
            {
                revalidations++;
                return ValueTask.FromResult(HealthySnapshot());
            },
            (execution, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Applied(execution.Command));
            });

        CapabilityCommandResult first = await registry.ExecuteAsync(CreateCommand());
        CapabilityCommandResult second = await registry.ExecuteAsync(CreateCommand());

        Assert.Equal(CommandOutcome.AppliedUnverified, first.Outcome);
        Assert.Equal(CommandOutcome.AppliedUnverified, second.Outcome);
        Assert.Equal(2, revalidations);
        Assert.Equal(2, handlerCalls);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsChangedIdentityWithoutCallingHandler()
    {
        int handlerCalls = 0;
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot() with { IdentityVerified = false }),
            (execution, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Applied(execution.Command));
            });

        CapabilityCommandResult result = await registry.ExecuteAsync(CreateCommand());

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason?.Code);
        Assert.Equal(0, handlerCalls);
    }

    [Theory]
    [InlineData(ResourceState.Passive, CapabilityReasonCode.ResourceConflict)]
    [InlineData(ResourceState.Degraded, CapabilityReasonCode.TransportFaulted)]
    [InlineData(ResourceState.Faulted, CapabilityReasonCode.TransportFaulted)]
    [InlineData(ResourceState.Idle, CapabilityReasonCode.ResourceReleased)]
    public async Task ExecuteAsync_RejectsResourceThatCannotAcceptCommands(
        ResourceState state,
        CapabilityReasonCode expectedReason)
    {
        int handlerCalls = 0;
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot() with { ResourceState = state }),
            (execution, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Applied(execution.Command));
            });

        CapabilityCommandResult result = await registry.ExecuteAsync(CreateCommand());

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(expectedReason, result.Reason?.Code);
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsStaleDescriptorGenerationFromFreshSnapshot()
    {
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot() with { DescriptorGeneration = 2 }),
            (execution, _) => ValueTask.FromResult(Applied(execution.Command)));

        CapabilityCommandResult result = await registry.ExecuteAsync(CreateCommand());

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.GenerationChanged, result.Reason?.Code);
    }

    [Fact]
    public async Task ExecuteAsync_DowngradesVerifiedResultWithoutReadback()
    {
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot()),
            (execution, _) => ValueTask.FromResult(new CapabilityCommandResult
            {
                CommandId = execution.Command.CommandId,
                Outcome = CommandOutcome.AppliedVerified,
                CompletedAt = Now,
            }));

        CapabilityCommandResult result = await registry.ExecuteAsync(CreateCommand());

        Assert.Equal(CommandOutcome.AppliedUnverified, result.Outcome);
        Assert.Equal(CapabilityReasonCode.TransportFaulted, result.Reason?.Code);
        Assert.Null(result.ReadbackValue);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsIndeterminateWhenCancellationOccursInsideHandler()
    {
        using var cancellation = new CancellationTokenSource();
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot()),
            (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<CapabilityCommandResult>(
                    new OperationCanceledException(cancellation.Token));
            });

        CapabilityCommandResult result = await registry.ExecuteAsync(
            CreateCommand(),
            cancellation.Token);

        Assert.Equal(CommandOutcome.Indeterminate, result.Outcome);
        Assert.Equal(RollbackResult.RestoredUnverified, result.Rollback);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnknownCapability()
    {
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot()),
            (execution, _) => ValueTask.FromResult(Applied(execution.Command)));
        CapabilityCommand command = CreateCommand() with { CapabilityId = "unknown" };

        CapabilityCommandResult result = await registry.ExecuteAsync(command);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.Unsupported, result.Reason?.Code);
    }

    [Fact]
    public void Constructor_RejectsDuplicateCapabilityInstances()
    {
        CapabilityRegistration registration = CreateRegistration(
            _ => ValueTask.FromResult(HealthySnapshot()),
            (execution, _) => ValueTask.FromResult(Applied(execution.Command)));

        Assert.Throws<ArgumentException>(() => new CapabilityRegistry(
            descriptorGeneration: 1,
            deviceGeneration: 4,
            [registration, registration],
            new FixedTimeProvider(Now)));
    }

    [Fact]
    public async Task FixtureRunner_ReportsExpectationMismatchWithoutHardware()
    {
        CapabilityRegistry registry = CreateRegistry(
            _ => ValueTask.FromResult(HealthySnapshot()),
            (execution, _) => ValueTask.FromResult(Applied(execution.Command)));

        IReadOnlyList<CapabilityFixtureResult> results = await CapabilityFixtureRunner.RunAsync(
            registry,
            [new CapabilityFixtureCase
            {
                Name = "incorrect expectation",
                Command = CreateCommand(),
                ExpectedOutcome = CommandOutcome.Rejected,
                ExpectedReasonCode = CapabilityReasonCode.Unsupported,
            }]);

        CapabilityFixtureResult result = Assert.Single(results);
        Assert.False(result.Matched);
        Assert.Equal(CommandOutcome.AppliedUnverified, result.Actual.Outcome);
    }

    private static CapabilityRegistry CreateRegistry(
        PluginCommandRevalidator revalidator,
        PluginCommandHandler handler) => new(
            descriptorGeneration: 1,
            deviceGeneration: 4,
            [CreateRegistration(revalidator, handler)],
            new FixedTimeProvider(Now));

    private static CapabilityRegistration CreateRegistration(
        PluginCommandRevalidator revalidator,
        PluginCommandHandler handler) => new(
            resourceId: "power",
            descriptor: new CapabilityDescriptor
            {
                CapabilityId = "power.limit",
                Role = CapabilityRole.PowerSustainedLimit,
                ValueKind = CapabilityValueKind.Integer,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                SupportsRead = true,
                SupportsWrite = true,
                Minimum = 8,
                Maximum = 30,
                Step = 1,
                Unit = CapabilityUnit.Watt,
                Persistence = CapabilityPersistence.Volatile,
            },
            revalidate: revalidator,
            handler: handler);

    private static PluginCommandSnapshot HealthySnapshot() => new()
    {
        IdentityVerified = true,
        FirmwareVerified = true,
        ResourceState = ResourceState.Owned,
        DescriptorGeneration = 1,
        DeviceGeneration = 4,
        OnAcPower = true,
        CurrentValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 15,
        },
    };

    private static CapabilityCommand CreateCommand() => new()
    {
        CommandId = Guid.NewGuid(),
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        CapabilityId = "power.limit",
        RequestedValue = new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 18,
        },
        ExpectedDescriptorGeneration = 1,
        ExpectedDeviceGeneration = 4,
        Deadline = Now.AddMinutes(1),
    };

    private static CapabilityCommandResult Applied(CapabilityCommand command) => new()
    {
        CommandId = command.CommandId,
        Outcome = CommandOutcome.AppliedUnverified,
        CompletedAt = Now,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
