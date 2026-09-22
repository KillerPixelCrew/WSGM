using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.ControllerBuilders;

namespace WSGM.Tests.Shell;

public sealed class AutoTdpServiceTests
{
    private const string PowerCapability = "power.primary-limit";

    private const string GameExecutable = @"C:\Games\game.exe";

    [Fact]
    public async Task ManualPowerIntentCancelsAnAdmittedAutomaticWriteAndKeepsItsNewRestoreTarget()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22)];
        await harness.Service.TickAsync(CancellationToken.None);
        await harness.Service.TickAsync(CancellationToken.None);
        harness.PendingWrite =
            new TaskCompletionSource<CapabilityCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tick = harness.Service.TickAsync(CancellationToken.None);
        await WaitForWriteCountAsync(harness, 1);
        harness.Service.NoteManualChange(19);
        await tick.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(harness.Service.OwnsPower);
        await harness.Service.DisposeAsync();
        Assert.Equal(19, harness.Writes[^1].Value.IntegerValue);
    }

    [Fact]
    public async Task MissingLimiterRefusesAutoTdpWithoutAnInventedSixtyFpsTarget()
    {
        Harness harness = new() { TargetFrametimeMs = 0 };
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22)];
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.False(harness.Service.Enabled);
        Assert.False(harness.Service.Availability.Available);
        Assert.Contains("Requires frame-rate limit", harness.Service.Availability.Detail);
        Assert.Empty(harness.Writes);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task LosingTheLimiterStopsControlAndRestoresThePreviousLimit()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22)];
        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Single(harness.Writes);
        harness.TargetFrametimeMs = 0;
        Assert.True(harness.Service.RefreshPrerequisites());
        Assert.False(harness.Service.Enabled);
        await WaitForStateAsync(harness, AutoTdpState.Off);
        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
        Assert.Equal(2, harness.Writes.Count);
        harness.TargetFrametimeMs = 1000d / 60;
        harness.Service.RefreshPrerequisites();
        Assert.True(harness.Service.Availability.Available);
        Assert.False(harness.Service.Enabled);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task ADisabledServiceNeverWritesPower()
    {
        Harness harness = new() { Frametimes = { Live = [Rendering(22.0)] } };

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Empty(harness.Writes);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
    }

    [Fact]
    public async Task AMissingPowerCapabilityIsReportedRatherThanGuessed()
    {
        Harness harness = new([]);
        harness.Service.Apply(true);

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(AutoTdpState.Unavailable, harness.Service.Status.State);
        Assert.Empty(harness.Writes);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task NoRenderingApplicationHoldsInsteadOfActing()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [];

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        Assert.Empty(harness.Writes);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task SustainedMissesRaiseThePowerLimitThroughTheCapability()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];

        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(PowerCapability, Assert.Single(harness.Writes).CapabilityId);
        Assert.Equal(17, harness.Writes[0].Value.IntegerValue);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task SeveralRenderersWithoutAnIdentityAreNotGuessedBetween()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live =
        [
            Rendering(22.0),
            Rendering(22.0, @"C:\Games\other.exe", 2)
        ];

        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Empty(harness.Writes);
        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task TheRunningApplicationPicksItsOwnRendererOutOfSeveral()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running());
        harness.Frametimes.Live =
        [
            Rendering(9.0, @"C:\Games\other.exe", 2),
            Rendering(22.0)
        ];

        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(17, Assert.Single(harness.Writes).Value.IntegerValue);
        await harness.Service.DisposeAsync();
    }

    [Theory]
    [InlineData(CommandOutcome.AppliedVerified)]
    [InlineData(CommandOutcome.Rejected)]
    public async Task AnInFlightTickOutcomeCannotPublishStatusForAnOldApplication(
        CommandOutcome outcome)
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running());
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 1; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        TaskCompletionSource<CapabilityCommandResult> pendingWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.PendingWrite = pendingWrite;
        var previousTick = harness.Service.TickAsync(CancellationToken.None);
        await WaitForWriteCountAsync(harness, 1);

        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\other.exe",
            2,
            "steam:71"));
        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\newest.exe",
            3,
            "steam:72"));
        pendingWrite.SetResult(new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = outcome,
            CompletedAt = DateTimeOffset.UtcNow
        });
        await previousTick;

        Assert.Equal("steam:72", harness.Service.Status.ApplicationId);
        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        Assert.Equal("context-changed", harness.Service.Status.Detail);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AnApplicationChangeCancelsItsInFlightWriteAndDisposalCompletes()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running());
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 1; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        TaskCompletionSource<CapabilityCommandResult> pendingWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.PendingWrite = pendingWrite;
        var previousTick = harness.Service.TickAsync(CancellationToken.None);
        await WaitForWriteCountAsync(harness, 1);

        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\other.exe",
            2,
            "steam:71"));
        await previousTick.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pendingWrite.Task.IsCompleted);
        Assert.Equal("steam:71", harness.Service.Status.ApplicationId);
        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        Assert.Equal("context-changed", harness.Service.Status.Detail);
        await harness.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnApplicationChangeBeforeDispatchPreventsTheOldPowerWrite()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running());
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 1; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        using ManualResetEventSlim capabilitiesEntered = new();
        using ManualResetEventSlim continueCapabilities = new();
        harness.BeforeCapabilitiesRead = () =>
        {
            capabilitiesEntered.Set();
            Assert.True(continueCapabilities.Wait(TimeSpan.FromSeconds(2)));
        };
        var previousTick = Task.Run(() => harness.Service.TickAsync(CancellationToken.None));
        Assert.True(capabilitiesEntered.Wait(TimeSpan.FromSeconds(2)));

        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\other.exe",
            2,
            "steam:71"));
        continueCapabilities.Set();
        await previousTick;

        Assert.Empty(harness.Writes);
        Assert.Equal("steam:71", harness.Service.Status.ApplicationId);
        Assert.Equal("context-changed", harness.Service.Status.Detail);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AnInFlightTickCannotRestoreStatusAfterAutoTdpIsDisabled()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running());
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 1; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        TaskCompletionSource<CapabilityCommandResult> pendingWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.PendingWrite = pendingWrite;
        var inFlightTick = harness.Service.TickAsync(CancellationToken.None);
        await WaitForWriteCountAsync(harness, 1);

        harness.Service.Apply(false);
        await inFlightTick.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForStateAsync(harness, AutoTdpState.Off);

        Assert.False(pendingWrite.Task.IsCompleted);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
        await harness.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnUnavailableTickCannotPublishAfterTheApplicationChanges()
    {
        Harness harness = new([]);
        using ManualResetEventSlim capabilitiesEntered = new();
        using ManualResetEventSlim continueCapabilities = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running());

        // Availability reads capabilities too, so the gate is installed after enabling: holding the
        // caller's own thread inside Apply would deadlock the test rather than the tick it targets.
        // The tick's own prerequisite check reads them once more before it captures the application,
        // and that read is let through: the read this test must hold open is the one after capture,
        // which is what makes the publish belong to the application that has since been replaced.
        var reads = 0;
        harness.BeforeCapabilitiesRead = () =>
        {
            if (Interlocked.Increment(ref reads) == 1 || continueCapabilities.IsSet)
            {
                return;
            }

            capabilitiesEntered.Set();
            Assert.True(continueCapabilities.Wait(TimeSpan.FromSeconds(2)));
        };
        var previousTick = Task.Run(() => harness.Service.TickAsync(CancellationToken.None));
        Assert.True(capabilitiesEntered.Wait(TimeSpan.FromSeconds(2)));
        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\other.exe",
            2,
            "steam:71"));
        continueCapabilities.Set();
        await previousTick;

        Assert.Equal("steam:71", harness.Service.Status.ApplicationId);
        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        Assert.Equal("context-changed", harness.Service.Status.Detail);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AnOlderApplicationSnapshotCannotReplaceTheNewestOne()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\newest.exe",
            3,
            "steam:72"));

        harness.Service.ApplyRunningApplication(Running(
            @"C:\Games\older.exe",
            2,
            "steam:71"));

        Assert.Equal("steam:72", harness.Service.Status.ApplicationId);
        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        Assert.Equal("context-changed", harness.Service.Status.Detail);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AManualChangePausesControlAndStopsFurtherWrites()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];
        await harness.Service.TickAsync(CancellationToken.None);

        harness.Service.NoteManualChange(24);
        for (var tick = 0; tick < 10; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Empty(harness.Writes);
        Assert.Equal(AutoTdpState.Paused, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AManualChangeWhileDisabledKeepsTheServiceOff()
    {
        Harness harness = new();

        harness.Service.NoteManualChange(24);

        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
        Assert.Null(harness.Service.Status.Watts);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task DisposingRestoresTheLimitAutoTdpTookOverFrom()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        await harness.Service.DisposeAsync();

        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
    }

    [Fact]
    public async Task TurningTheServiceOffRestoresTheLimitWithoutDisposal()
    {
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        harness.Service.Apply(false);
        await WaitForWriteCountAsync(harness, 2);

        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AWriteTheDeviceRefusedIsNotTreatedAsAppliedControl()
    {
        // The controller has already moved its believed wattage by the time the write returns, so
        // an outcome that is not "written" leaves every later decision resting on a limit the
        // hardware may never have taken.
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];
        harness.Outcome = CommandOutcome.Rejected;

        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Single(harness.Writes);
        Assert.Equal(AutoTdpState.Unavailable, harness.Service.Status.State);
        Assert.Contains("did not accept", harness.Service.Status.Detail, StringComparison.Ordinal);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task ControlResumesFromTheObservedLimitAfterAnUnappliedWrite()
    {
        // Re-basing costs one window and is the only honest way back: continuing would judge frames
        // against a limit the device never took. Control is not abandoned either — once writes are
        // accepted again the service goes on controlling from what the hardware reports.
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];
        harness.Outcome = CommandOutcome.TimedOut;
        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Single(harness.Writes);
        Assert.Equal(AutoTdpState.Unavailable, harness.Service.Status.State);

        harness.Outcome = CommandOutcome.AppliedVerified;
        for (var tick = 0;
             tick < AutoTdpController.SettleWindows + AutoTdpController.SustainedMisses;
             tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(2, harness.Writes.Count);
        Assert.Equal(AutoTdpState.Controlling, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AnUnconfirmedRestoreIsNotReportedAsRestored()
    {
        // "The previous limit was restored" for a value the device refused is the one message that
        // makes the handheld's real power state undiagnosable from a log.
        Harness harness = new();
        harness.Service.Apply(true);
        harness.Frametimes.Live = [Rendering(22.0)];
        for (var tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        harness.Outcome = CommandOutcome.Rejected;
        var failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.DisposeAsync().AsTask());

        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
        Assert.Contains("was not confirmed", harness.Service.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("could not verify restoration", failure.Message, StringComparison.Ordinal);
    }

    private static async Task WaitForWriteCountAsync(Harness harness, int count)
    {
        for (var attempt = 0; attempt < 100 && harness.Writes.Count < count; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(harness.Writes.Count >= count, $"Expected at least {count} power writes.");
    }

    private static async Task WaitForStateAsync(Harness harness, AutoTdpState state)
    {
        for (var attempt = 0; attempt < 100 && harness.Service.Status.State != state; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(state, harness.Service.Status.State);
    }

    private static RtssFrametimeSample Rendering(
        double frametimeMs,
        string executable = GameExecutable,
        uint processId = 1)
    {
        return new RtssFrametimeSample(processId, executable, frametimeMs, 60, 100);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairControlRaisesBothAndRestoresDifferentOriginalLimits(bool manualOverride)
    {
        DeviceCapabilityView[] views = [View("primary", 12, true), View("boost", 17, false)];
        List<(string Id, int Watts, bool Pair)> writes = [];
        AutoTdpService service = new(
            new FakeFrametimeSource { Live = [new RtssFrametimeSample(1, "game.exe", 22, 60, 100)] }, () => views,
            (power, value, pair, _) => Write(power.Descriptor.CapabilityId, value, pair), () => 16.6);
        service.Apply(true);
        for (var i = 0; i < 3; i++)
        {
            await service.TickAsync(CancellationToken.None);
        }

        Assert.Contains(writes, w => w == ("primary", 13, true));
        var restorePrimary = manualOverride ? 14 : 12;
        var restoreBoost = manualOverride ? 20 : 17;
        if (manualOverride)
        {
            views[0] = View("primary", restorePrimary, true);
            views[1] = View("boost", restoreBoost, false);
            service.NoteManualChange(restorePrimary);
            Assert.False(service.OwnsPower);
        }

        await service.DisposeAsync();
        Assert.Equal([("primary", restorePrimary, true), ("boost", restoreBoost, false)], writes.TakeLast(2));
        Assert.Equal(restorePrimary, views[0].Projection.State.ObservedValue?.IntegerValue);
        Assert.Equal(restoreBoost, views[1].Projection.State.ObservedValue?.IntegerValue);
        return;

        Task<CapabilityCommandResult> Write(string id, CapabilityValue value, bool pair)
        {
            var watts = value.IntegerValue!.Value;
            writes.Add((id, watts, pair));
            for (var i = 0; i < views.Length; i++)
            {
                if (views[i].Descriptor.CapabilityId == id || pair)
                {
                    views[i] = views[i] with
                    {
                        Projection = views[i].Projection with
                        {
                            State = views[i].Projection.State with { ObservedValue = value }
                        }
                    };
                }
            }

            return Task.FromResult(new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.AppliedVerified,
                ReadbackValue = value,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }
    }

    [Fact]
    public async Task MissingCompanionBlocksControlInsteadOfWritingOnlyThePrimary()
    {
        var writes = 0;
        await using AutoTdpService service =
            new(new FakeFrametimeSource { Live = [new RtssFrametimeSample(1, "game.exe", 22, 60, 100)] },
                () => [View("primary", 12, true)], Write, () => 16.6);
        Assert.False(service.Availability.Available);
        service.Apply(true);
        for (var i = 0; i < 8; i++)
        {
            await service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(0, writes);
        return;

        Task<CapabilityCommandResult> Write(DeviceCapabilityView _, CapabilityValue value, bool pair,
            CancellationToken token)
        {
            writes++;
            throw new InvalidOperationException("No write should be dispatched.");
        }
    }

    [Fact]
    public async Task UncertainPowerNeedsNewerReadbackBeforeAutomaticWritesResume()
    {
        var completed = DateTimeOffset.UtcNow;
        var primary = View("primary", 12, true);
        primary = primary with
        {
            LastResult = new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.Indeterminate,
                CompletedAt = completed
            },
            Projection = primary.Projection with
            {
                Progress = CommandProgress.Uncertain,
                State = primary.Projection.State with { ObservedAt = completed.AddSeconds(-1) }
            }
        };
        DeviceCapabilityView[] views = [primary, View("boost", 17, false)];
        await using AutoTdpService service = new(
            new FakeFrametimeSource { Live = [new RtssFrametimeSample(1, "game.exe", 22, 60, 100)] }, () => views,
            (_, _, _, _) => throw new InvalidOperationException("No command was requested."), () => 16.6);
        Assert.False(service.Availability.Available);
        views[0] = primary with
        {
            Projection = primary.Projection with
            {
                State = primary.Projection.State with { ObservedAt = completed.AddSeconds(1) }
            }
        };
        Assert.True(service.Availability.Available);
    }

    [Fact]
    public async Task CompanionFromAnEarlierCycleCannotSupplyTheRestoreSnapshot()
    {
        var primary = View("primary", 12, true);
        primary = primary with
        {
            Projection = primary.Projection with
            {
                State = primary.Projection.State with { CycleGeneration = 2 }
            }
        };
        await using AutoTdpService service = new(
            new FakeFrametimeSource { Live = [new RtssFrametimeSample(1, "game.exe", 22, 60, 100)] },
            () => [primary, View("boost", 17, false)],
            (_, _, _, _) => throw new InvalidOperationException("No command was requested."), () => 16.6);
        Assert.False(service.Availability.Available);
    }

    private static DeviceCapabilityView View(string id, int watts, bool primary)
    {
        return new DeviceCapabilityView(
            new CapabilityDescriptor
            {
                CapabilityId = id,
                Role = primary ? CapabilityRole.PowerSustainedLimit : CapabilityRole.PowerSlowLimit,
                PairedPowerLimitId = primary ? "boost" : null,
                ValueKind = CapabilityValueKind.Integer,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                SupportsRead = true,
                SupportsWrite = true,
                Minimum = 8,
                Maximum = 37,
                Step = 1,
                Unit = CapabilityUnit.Watt,
                Persistence = CapabilityPersistence.Volatile
            }, new CapabilityProjection
            {
                State = new CapabilityState
                {
                    CapabilityId = id,
                    Available = true,
                    Quality = HardwareStateQuality.Verified,
                    DescriptorGeneration = 1,
                    CycleGeneration = 1,
                    ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts }
                }
            }, null);
    }

    [Theory]
    [InlineData(null, 60, (int)PerformanceReadbackQuality.Unavailable, 0)]
    [InlineData(0, 60, (int)PerformanceReadbackQuality.Verified, 0)]
    [InlineData(60, 0, (int)PerformanceReadbackQuality.Verified, 0)]
    [InlineData(60, 60, (int)PerformanceReadbackQuality.AppliedUnverified, 0)]
    [InlineData(60, 60, (int)PerformanceReadbackQuality.Verified, 60)]
    [InlineData(30, 30, (int)PerformanceReadbackQuality.Verified, 30)]
    public void OnlyAnActiveVerifiedLimiterProvidesTheControlTarget(
        int? observed, int? desired, int quality, int expectedFps)
    {
        PerformanceState state = new(new RtssProbe(RtssAvailability.Ready, null, null, 1, null, null),
            null, false, ProfileSource.Global, ProfileSource.Global,
            new PerformanceValues(desired, 0), new PerformanceValues(observed, 0), (PerformanceReadbackQuality)quality,
            PerformanceReadbackQuality.Verified, DateTimeOffset.UtcNow, PerformanceCommandState.Idle);
        Assert.Equal(expectedFps == 0 ? 0 : 1000d / expectedFps, AutoTdpService.TargetFrametime(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QamAndOverlayUseTheSameUnavailableReasonEvenWhenTheSettingWasOn(bool enabled)
    {
        AutoTdpAvailability availability = new(false, "Requires frame-rate limit.", null);
        var qam = DeviceCoordinatorNativeQamAutoTdpService.Project(enabled, null, true, availability);
        var overlay = DeviceOverlayBridge.AutoTdpView(enabled, null, availability);
        Assert.False(qam.Available);
        Assert.False(qam.Enabled);
        Assert.False(overlay.CanInvoke);
        Assert.Equal(availability.Detail, qam.StatusText);
        Assert.Equal(availability.Detail, overlay.Description);
    }

    private sealed record Write(string CapabilityId, CapabilityValue Value);

    private sealed class FakeFrametimeSource : IFrametimeSource
    {
        internal IReadOnlyList<RtssFrametimeSample> Live { get; set; } = [];

        public IReadOnlyList<RtssFrametimeSample> ReadLive()
        {
            return Live;
        }
    }

    private sealed class Harness
    {
        internal Harness(IReadOnlyList<DeviceCapabilityView>? capabilities = null)
        {
            var views = capabilities ?? [PowerView(15)];
            Service = new AutoTdpService(
                Frametimes,
                () =>
                {
                    BeforeCapabilitiesRead?.Invoke();
                    return views;
                },
                (power, value, _, cancellationToken) =>
                {
                    Writes.Add(new Write(power.Descriptor.CapabilityId, value));
                    if (PendingWrite is not { } pending)
                    {
                        return Task.FromResult(new CapabilityCommandResult
                        {
                            CommandId = Guid.NewGuid(),
                            Outcome = Outcome,
                            ReadbackValue = value,
                            CompletedAt = DateTimeOffset.UtcNow
                        });
                    }

                    PendingWrite = null;
                    return pending.Task.WaitAsync(cancellationToken);
                },
                () => TargetFrametimeMs);
        }

        internal FakeFrametimeSource Frametimes { get; } = new();

        internal double TargetFrametimeMs { get; set; } = 16.6;

        internal Action? BeforeCapabilitiesRead { get; set; }

        /// <summary>What the capability layer reports for the next write.</summary>
        internal CommandOutcome Outcome { get; set; } = CommandOutcome.AppliedVerified;

        internal TaskCompletionSource<CapabilityCommandResult>? PendingWrite { get; set; }

        internal List<Write> Writes { get; } = [];

        internal AutoTdpService Service { get; }

        private static DeviceCapabilityView PowerView(int watts)
        {
            return new DeviceCapabilityView(
                new CapabilityDescriptor
                {
                    CapabilityId = PowerCapability,
                    Role = CapabilityRole.PowerSustainedLimit,
                    ValueKind = CapabilityValueKind.Integer,
                    Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                    SupportsRead = true,
                    SupportsWrite = true,
                    Minimum = 8,
                    Maximum = 30,
                    Step = 2,
                    Persistence = CapabilityPersistence.Volatile
                },
                new CapabilityProjection
                {
                    State = new CapabilityState
                    {
                        CapabilityId = PowerCapability,
                        Available = true,
                        Quality = HardwareStateQuality.Verified,
                        ObservedValue = new CapabilityValue
                        {
                            Kind = CapabilityValueKind.Integer,
                            IntegerValue = watts
                        },
                        DescriptorGeneration = 1,
                        CycleGeneration = 1
                    }
                },
                null);
        }
    }
}
