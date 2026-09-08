using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AutoTdpPowerPairTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairControlRaisesBothAndRestoresDifferentOriginalLimits(bool manualOverride)
    {
        DeviceCapabilityView[] views = [View("primary", 12, true), View("boost", 17, false)];
        List<(string Id, int Watts, bool Pair)> writes = [];
        Task<CapabilityCommandResult> Write(string id, CapabilityValue value, bool pair)
        {
            int watts = value.IntegerValue!.Value;
            writes.Add((id, watts, pair));
            for (int i = 0; i < views.Length; i++)
            {
                if (views[i].Descriptor.CapabilityId == id || pair)
                {
                    views[i] = views[i] with
                    {
                        Projection = views[i].Projection with
                        { State = views[i].Projection.State with { ObservedValue = value } }
                    };
                }
            }
            return Task.FromResult(new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.AppliedVerified,
                ReadbackValue = value,
                CompletedAt = DateTimeOffset.UtcNow,
            });
        }
        AutoTdpService service = new(new Frames(), () => views,
            (power, value, pair, _) => Write(power.Descriptor.CapabilityId, value, pair), () => 16.6);
        service.Apply(true);
        for (int i = 0; i < 3; i++) { await service.TickAsync(CancellationToken.None); }
        Assert.Contains(writes, w => w == ("primary", 13, true));
        int restorePrimary = manualOverride ? 14 : 12;
        int restoreBoost = manualOverride ? 20 : 17;
        if (manualOverride)
        {
            views[0] = View("primary", restorePrimary, true);
            views[1] = View("boost", restoreBoost, false);
            service.NoteManualChange(restorePrimary);
            Assert.False(service.OwnsPower);
        }
        await service.DisposeAsync();
        Assert.Equal(new[] { ("primary", restorePrimary, true), ("boost", restoreBoost, false) }, writes.TakeLast(2));
        Assert.Equal(restorePrimary, views[0].Projection.State.ObservedValue?.IntegerValue);
        Assert.Equal(restoreBoost, views[1].Projection.State.ObservedValue?.IntegerValue);
    }

    [Fact]
    public async Task MissingCompanionBlocksControlInsteadOfWritingOnlyThePrimary()
    {
        int writes = 0;
        Task<CapabilityCommandResult> Write(DeviceCapabilityView _, CapabilityValue value, bool pair, CancellationToken token)
        { writes++; throw new InvalidOperationException("No write should be dispatched."); }
        await using AutoTdpService service = new(new Frames(), () => [View("primary", 12, true)], Write, () => 16.6);
        Assert.False(service.Availability.Available);
        service.Apply(true);
        for (int i = 0; i < 8; i++) { await service.TickAsync(CancellationToken.None); }
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task UncertainPowerNeedsNewerReadbackBeforeAutomaticWritesResume()
    {
        DateTimeOffset completed = DateTimeOffset.UtcNow;
        var primary = View("primary", 12, true);
        primary = primary with
        {
            LastResult = new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.Indeterminate,
                CompletedAt = completed,
            },
            Projection = primary.Projection with
            {
                Progress = CommandProgress.Uncertain,
                State = primary.Projection.State with { ObservedAt = completed.AddSeconds(-1) },
            },
        };
        DeviceCapabilityView[] views = [primary, View("boost", 17, false)];
        await using AutoTdpService service = new(new Frames(), () => views,
            (_, _, _, _) => throw new InvalidOperationException("No command was requested."), () => 16.6);
        Assert.False(service.Availability.Available);
        views[0] = primary with
        {
            Projection = primary.Projection with
            { State = primary.Projection.State with { ObservedAt = completed.AddSeconds(1) } }
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
            { State = primary.Projection.State with { CycleGeneration = 2 } }
        };
        await using AutoTdpService service = new(new Frames(), () => [primary, View("boost", 17, false)],
            (_, _, _, _) => throw new InvalidOperationException("No command was requested."), () => 16.6);
        Assert.False(service.Availability.Available);
    }

    private sealed class Frames : IFrametimeSource
    {
        public IReadOnlyList<RtssFrametimeSample> ReadLive() => [new(1, "game.exe", 22, 60, 100)];
    }

    private static DeviceCapabilityView View(string id, int watts, bool primary) => new(
        new CapabilityDescriptor
        {
            CapabilityId = id,
            Role = primary ? CapabilityRole.PowerSustainedLimit : CapabilityRole.PowerSlowLimit,
            PairedPowerLimitId = primary ? "boost" : null,
            ValueKind = CapabilityValueKind.Integer,
            Display = new() { Key = DisplayKey.SustainedPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 37,
            Step = 1,
            Unit = CapabilityUnit.Watt,
            Persistence = CapabilityPersistence.Volatile,
        }, new CapabilityProjection
        {
            State = new CapabilityState
            {
                CapabilityId = id,
                Available = true,
                Quality = HardwareStateQuality.Verified,
                DescriptorGeneration = 1,
                CycleGeneration = 1,
                ObservedValue = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
            },
        }, null);
}
