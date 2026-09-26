using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceLightingRestoreTests
{
    [Fact]
    public void FirmwareDefaultsDoNotBecomeDesiredConfiguration()
    {
        DeviceLightingRestore restore = new();
        var view = View();
        var desired = view.Projection.DesiredValue;

        Assert.True(Begin(restore, view));
        Assert.False(Begin(restore, view));
        Assert.Same(desired, view.Projection.DesiredValue);
        Assert.Equal(0x123456, desired!.ColorValue);
        Assert.Equal(0xFFFFFF, view.Projection.State.ObservedValue!.ColorValue);
    }

    [Fact]
    public void DelayedReadinessDoesNotConsumeTheRestoreAttempt()
    {
        DeviceLightingRestore restore = new();
        var ready = View();
        var unavailable = ready with
        {
            Projection = ready.Projection with
            {
                State = ready.Projection.State with { Available = false, Quality = HardwareStateQuality.Unknown }
            }
        };

        Assert.False(Begin(restore, unavailable));
        Assert.True(Begin(restore, ready));
        Assert.False(Begin(restore, unavailable));
        Assert.False(Begin(restore, ready));
    }

    [Theory]
    [InlineData(HardwareStateQuality.Stale)]
    [InlineData(HardwareStateQuality.Faulted)]
    public void LightingRestoreRefusesExpiredOrFaultedState(HardwareStateQuality quality)
    {
        DeviceLightingRestore restore = new();
        var view = View();

        Assert.False(Begin(restore, view with
        {
            Projection = view.Projection with { State = view.Projection.State with { Quality = quality } }
        }));
    }

    [Fact]
    public void LightingThatWasNeverReadBackIsStillRestored()
    {
        // Aura on the Ally is write-only; readback is never a precondition for a restore.
        DeviceLightingRestore restore = new();
        var view = View();

        Assert.True(Begin(restore, view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with { Quality = HardwareStateQuality.Unknown, ObservedValue = null }
            }
        }));
    }

    [Theory]
    [InlineData(CommandOutcome.Indeterminate)]
    [InlineData(CommandOutcome.TimedOut)]
    public void AnUncertainManualWriteCannotTriggerAnAutomaticRestore(CommandOutcome outcome)
    {
        DeviceLightingRestore restore = new();
        var view = View() with
        {
            LastResult = new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = outcome,
                CompletedAt = DateTimeOffset.UtcNow
            }
        };

        Assert.False(Begin(restore, view));
    }

    [Fact]
    public void ReconnectAndResumeCanRestoreTheSavedValueInANewCycle()
    {
        DeviceLightingRestore restore = new();
        var first = View();
        Assert.True(Begin(restore, first));
        var next = first with
        {
            Projection = first.Projection with { State = first.Projection.State with { CycleGeneration = 2 } }
        };

        Assert.True(Begin(restore, next));
        Assert.False(Begin(restore, next));
    }

    [Fact]
    public void ANewDesiredColorAfterFailureGetsItsOwnAttempt()
    {
        DeviceLightingRestore restore = new();
        var first = View();
        Assert.True(Begin(restore, first));
        var next = first with
        {
            Projection = first.Projection with { DesiredValue = Color(0x654321) }
        };

        Assert.True(Begin(restore, next));
        Assert.False(Begin(restore, next));
    }

    [Fact]
    public void ConfirmedDesiredReadbackNeedsNoFirmwareWrite()
    {
        DeviceLightingRestore restore = new();
        var view = View();
        view = view with
        {
            Projection = view.Projection with { State = view.Projection.State with { ObservedValue = Color(0x123456) } }
        };

        Assert.False(Begin(restore, view));
    }

    [Fact]
    public void ARefusedRestoreIsTriedAgainUpToTheBound()
    {
        // A refused command wrote nothing. Right after wake the device refuses while it is busy,
        // and giving up after the first refusal left zones at their firmware default.
        DeviceLightingRestore restore = new();
        var view = View();
        for (var attempt = 1; attempt <= DeviceLightingRestore.MaxAttempts; attempt++)
        {
            Assert.Equal(attempt, restore.TryBegin(view));
            Assert.Equal(0, restore.TryBegin(view));
            restore.Complete(view, CommandOutcome.Rejected);
        }

        Assert.Equal(0, restore.TryBegin(view));
    }

    [Fact]
    public void AnAppliedRestoreIsNotRepeatedInTheSameCycle()
    {
        DeviceLightingRestore restore = new();
        var view = View();

        Assert.Equal(1, restore.TryBegin(view));
        restore.Complete(view, CommandOutcome.AppliedUnverified);

        Assert.Equal(0, restore.TryBegin(view));
    }

    [Fact]
    public void AnUncertainRestoreWaitsForANewerReadback()
    {
        DeviceLightingRestore restore = new();
        var completed = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var view = View() with
        {
            LastResult = new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = CommandOutcome.TimedOut,
                CompletedAt = completed
            }
        };
        var stale = view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with { ObservedAt = completed.AddSeconds(-1) }
            }
        };
        var fresh = view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with { ObservedAt = completed.AddSeconds(1) }
            }
        };

        // The readback before the write settles nothing; one taken after it shows the zone does
        // not hold the value, which is the re-read that allows another write.
        Assert.Equal(0, restore.TryBegin(stale));
        Assert.Equal(1, restore.TryBegin(fresh));
    }

    private static bool Begin(DeviceLightingRestore restore, DeviceCapabilityView view)
    {
        return restore.TryBegin(view) > 0;
    }

    private static CapabilityValue Color(int color)
    {
        return new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = color };
    }

    [Fact]
    public void ReturningFromAnAlreadyAppliedProfileAllowsThePreviousColorToRestore()
    {
        DeviceLightingRestore restore = new();
        var first = View();
        Assert.True(Begin(restore, first));
        var second = first with
        {
            Projection = first.Projection with { DesiredValue = Color(0xFFFFFF) }
        };
        Assert.False(restore.CanApply(second));
        Assert.True(Begin(restore, first));
    }

    [Fact]
    public void MissingInvalidPendingAndReadOnlyValuesCannotStartRestoration()
    {
        DeviceLightingRestore restore = new();
        var view = View();
        Assert.False(restore.CanApply(view with { Projection = view.Projection with { DesiredValue = null } }));
        Assert.False(
            restore.CanApply(view with { Projection = view.Projection with { DesiredValueOutOfRange = true } }));
        Assert.False(
            restore.CanApply(view with { Projection = view.Projection with { PendingValue = Color(0x654321) } }));
        Assert.False(restore.CanApply(view with { Descriptor = view.Descriptor with { SupportsWrite = false } }));
        Assert.False(restore.CanApply(view with
        {
            Descriptor = view.Descriptor with { Role = CapabilityRole.PowerSlowLimit }
        }));
        Assert.True(Begin(restore, view));
    }

    private static DeviceCapabilityView View()
    {
        return new DeviceCapabilityView(
            new CapabilityDescriptor
            {
                CapabilityId = "lighting.zone-color",
                InstanceId = "left-ring",
                Role = CapabilityRole.LightingZoneColor,
                ValueKind = CapabilityValueKind.Color,
                Display = new CapabilityDisplay { Key = DisplayKey.Lighting },
                SupportsRead = true,
                SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            },
            new CapabilityProjection
            {
                DesiredValue = Color(0x123456),
                DesiredSource = ProfileSource.Global,
                State = new CapabilityState
                {
                    CapabilityId = "lighting.zone-color",
                    InstanceId = "left-ring",
                    Available = true,
                    Quality = HardwareStateQuality.Observed,
                    CycleGeneration = 1,
                    DescriptorGeneration = 1,
                    ObservedValue = Color(0xFFFFFF)
                }
            }, null);
    }
}
