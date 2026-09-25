using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

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
    public void UncertainPreviousWriteIsNotAdmitted(CommandOutcome outcome)
    {
        var admission = DeviceDesiredWriteAdmission.TryAdmit(View() with
        {
            LastResult = new CapabilityCommandResult
            {
                CommandId = Guid.NewGuid(),
                Outcome = outcome,
                CompletedAt = DateTimeOffset.UtcNow
            }
        });

        Assert.False(admission.Admitted);
        Assert.Equal(DeviceDesiredWriteSkipReason.PreviousResultUncertain, admission.SkipReason);
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
}
