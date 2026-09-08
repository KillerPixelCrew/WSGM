using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceLightingRestoreTests
{
    [Fact]
    public void FirmwareDefaultsDoNotBecomeDesiredConfiguration()
    {
        DeviceLightingRestore restore = new();
        DeviceCapabilityView view = View();
        var desired = view.Projection.DesiredValue;

        Assert.True(restore.TryBegin(view));
        Assert.False(restore.TryBegin(view));
        Assert.Same(desired, view.Projection.DesiredValue);
        Assert.Equal(0x123456, desired!.ColorValue);
        Assert.Equal(0xFFFFFF, view.Projection.State.ObservedValue!.ColorValue);
    }

    [Fact]
    public void DelayedReadinessDoesNotConsumeTheRestoreAttempt()
    {
        DeviceLightingRestore restore = new();
        DeviceCapabilityView ready = View();
        DeviceCapabilityView unavailable = ready with
        {
            Projection = ready.Projection with
            {
                State = ready.Projection.State with { Available = false, Quality = HardwareStateQuality.Unknown },
            },
        };

        Assert.False(restore.TryBegin(unavailable));
        Assert.True(restore.TryBegin(ready));
        Assert.False(restore.TryBegin(unavailable));
        Assert.False(restore.TryBegin(ready));
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
                CompletedAt = DateTimeOffset.UtcNow,
            },
        };

        Assert.False(restore.TryBegin(view));
    }

    [Fact]
    public void ReconnectAndResumeCanRestoreTheSavedValueInANewCycle()
    {
        DeviceLightingRestore restore = new();
        var first = View();
        Assert.True(restore.TryBegin(first));
        var next = first with
        {
            Projection = first.Projection with { State = first.Projection.State with { CycleGeneration = 2 } },
        };

        Assert.True(restore.TryBegin(next));
        Assert.False(restore.TryBegin(next));
    }

    [Fact]
    public void ANewDesiredColorAfterFailureGetsItsOwnAttempt()
    {
        DeviceLightingRestore restore = new();
        var first = View();
        Assert.True(restore.TryBegin(first));
        var next = first with
        {
            Projection = first.Projection with { DesiredValue = Color(0x654321) },
        };

        Assert.True(restore.TryBegin(next));
        Assert.False(restore.TryBegin(next));
    }

    [Fact]
    public void ConfirmedDesiredReadbackNeedsNoFirmwareWrite()
    {
        DeviceLightingRestore restore = new();
        var view = View();
        view = view with
        {
            Projection = view.Projection with { State = view.Projection.State with { ObservedValue = Color(0x123456) } },
        };

        Assert.False(restore.TryBegin(view));
    }

    private static CapabilityValue Color(int color) => new() { Kind = CapabilityValueKind.Color, ColorValue = color };

    [Fact]
    public void ReturningFromAnAlreadyAppliedProfileAllowsThePreviousColorToRestore()
    {
        DeviceLightingRestore restore = new();
        var first = View();
        Assert.True(restore.TryBegin(first));
        var second = first with
        {
            Projection = first.Projection with { DesiredValue = Color(0xFFFFFF) },
        };
        Assert.False(restore.CanApply(second));
        Assert.True(restore.TryBegin(first));
    }

    [Fact]
    public void MissingInvalidPendingAndReadOnlyValuesCannotStartRestoration()
    {
        DeviceLightingRestore restore = new();
        var view = View();
        Assert.False(restore.CanApply(view with { Projection = view.Projection with { DesiredValue = null } }));
        Assert.False(restore.CanApply(view with { Projection = view.Projection with { DesiredValueOutOfRange = true } }));
        Assert.False(restore.CanApply(view with { Projection = view.Projection with { PendingValue = Color(0x654321) } }));
        Assert.False(restore.CanApply(view with { Descriptor = view.Descriptor with { SupportsWrite = false } }));
        Assert.False(restore.CanApply(view with { Descriptor = view.Descriptor with { Role = CapabilityRole.PowerSlowLimit } }));
        Assert.True(restore.TryBegin(view));
    }

    private static DeviceCapabilityView View() => new(
        new CapabilityDescriptor
        {
            CapabilityId = "lighting.zone-color",
            InstanceId = "left-ring",
            Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Lighting },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
        },
        new CapabilityProjection
        {
            DesiredValue = Color(0x123456),
            DesiredSource = DeviceDesiredValueSource.GlobalDefault,
            State = new CapabilityState
            {
                CapabilityId = "lighting.zone-color",
                InstanceId = "left-ring",
                Available = true,
                Quality = HardwareStateQuality.Observed,
                CycleGeneration = 1,
                DescriptorGeneration = 1,
                ObservedValue = Color(0xFFFFFF),
            },
        }, null);
}
