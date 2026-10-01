using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Shell;

public sealed class DeviceDesiredWriteAdmissionTests
{
    [Fact]
    public void FreshIdleWriteWithDifferentObservedValueIsAdmitted()
    {
        var admission = DeviceDesiredWriteAdmission.TryAdmit(View());

        Assert.True(admission.Admitted);
        Assert.Equal(25, admission.DesiredValue!.IntegerValue);
        Assert.Null(admission.SkipReason);
    }

    [Fact]
    public void StateThatWasNeverReadBackIsStillAdmitted()
    {
        var view = View();

        var admission = DeviceDesiredWriteAdmission.TryAdmit(view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with { Quality = HardwareStateQuality.Unknown, ObservedValue = null }
            }
        });

        Assert.True(admission.Admitted);
    }

    [Theory]
    [InlineData(HardwareStateQuality.Stale)]
    [InlineData(HardwareStateQuality.Faulted)]
    public void ExpiredOrFaultedStateIsNotAdmitted(HardwareStateQuality quality)
    {
        var view = View();

        var admission = DeviceDesiredWriteAdmission.TryAdmit(view with
        {
            Projection = view.Projection with { State = view.Projection.State with { Quality = quality } }
        });

        Assert.False(admission.Admitted);
        Assert.Equal(DeviceDesiredWriteSkipReason.UntrustedState, admission.SkipReason);
    }

    [Fact]
    public void MatchingObservedValueIsReportedAsAlreadyApplied()
    {
        var view = View();

        var admission = DeviceDesiredWriteAdmission.TryAdmit(view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with { ObservedValue = CapabilityValue.Integer(25) }
            }
        });

        Assert.False(admission.Admitted);
        Assert.Equal(DeviceDesiredWriteSkipReason.AlreadyApplied, admission.SkipReason);
    }

    [Theory]
    [InlineData(CommandOutcome.Indeterminate)]
    [InlineData(CommandOutcome.TimedOut)]
    public void AnUncertainWriteIsNotRepeatedButADifferentValueGoesAhead(CommandOutcome outcome)
    {
        // The same value may already be on the device, so it is not written again automatically; no
        // readback is waited for, because a device like the Ally never delivers one.
        var uncertain = new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = outcome,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var repeat = DeviceDesiredWriteAdmission.TryAdmit(View() with
        {
            LastResult = uncertain,
            LastCommandValue = CapabilityValue.Integer(25)
        });
        var another = DeviceDesiredWriteAdmission.TryAdmit(View() with
        {
            LastResult = uncertain,
            LastCommandValue = CapabilityValue.Integer(15)
        });

        Assert.False(repeat.Admitted);
        Assert.Equal(DeviceDesiredWriteSkipReason.PreviousResultUncertain, repeat.SkipReason);
        Assert.True(another.Admitted);
    }

    private static DeviceCapabilityView View()
    {
        return new DeviceCapabilityView(
            new CapabilityDescriptor
            {
                CapabilityId = "power.slow-limit",
                Role = CapabilityRole.PowerSlowLimit,
                ValueKind = CapabilityValueKind.Integer,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                SupportsRead = true,
                SupportsWrite = true,
                Persistence = CapabilityPersistence.Volatile
            },
            new CapabilityProjection
            {
                DesiredValue = CapabilityValue.Integer(25),
                DesiredSource = ProfileSource.Global,
                State = new CapabilityState
                {
                    CapabilityId = "power.slow-limit",
                    Available = true,
                    Quality = HardwareStateQuality.Observed,
                    CycleGeneration = 1,
                    DescriptorGeneration = 1,
                    ObservedValue = CapabilityValue.Integer(20)
                }
            }, null);
    }

    [Theory]
    [InlineData(CapabilityProfileScope.GlobalOnly)]
    [InlineData(CapabilityProfileScope.NativePerApplication)]
    public void OutsideTheSwitchedScopeOnlyTheGlobalValueIsRestored(CapabilityProfileScope scope)
    {
        // The device still holds the game's value; the restore writes Global's, never the game's.
        var view = CapabilityBuilders.View(CapabilityBuilders.Toggle(scope), CapabilityBuilders.Flag(true),
            CapabilityBuilders.Flag(false), ProfileSource.Game);
        view = view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with { ObservedValue = CapabilityBuilders.Flag(true) }
            }
        };

        var admission = DeviceDesiredWriteAdmission.TryAdmit(view);

        Assert.True(admission.Admitted);
        Assert.False(admission.DesiredValue!.BooleanValue);
    }

    [Fact]
    public void ANativeValueWithNoGlobalValueIsNotRestored()
    {
        var view = CapabilityBuilders.View(CapabilityBuilders.Toggle(CapabilityProfileScope.NativePerApplication),
            CapabilityBuilders.Flag(true), null, ProfileSource.Game);

        Assert.Equal(DeviceDesiredWriteSkipReason.MissingDesiredValue,
            DeviceDesiredWriteAdmission.TryAdmit(view).SkipReason);
    }
}
