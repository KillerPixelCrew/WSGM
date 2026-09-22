using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceProfileApplierTests
{
    private const string Fan = "thermal.fan-curve";

    private static DeviceAuthoredProfile Profile(int output = 50)
    {
        return new DeviceAuthoredProfile
        {
            ProfileId = "quiet",
            Name = "Quiet",
            CapabilityId = Fan,
            Curve =
            [
                new AuthoredCurvePoint { Input = 0, Output = output },
                new AuthoredCurvePoint { Input = 100, Output = output }
            ]
        };
    }

    private static Resolved<string?> Selected(string? id = "quiet", ProfileSource source = ProfileSource.Global)
    {
        return new Resolved<string?>(id, id is null ? ProfileSource.None : source);
    }

    /// <summary>
    ///     The device answered a write. Unverified is the interesting default: most EC writes
    ///     have no readback, and the applier must still treat that as applied.
    /// </summary>
    private static Task<CapabilityCommandResult> Answer(
        CommandOutcome outcome = CommandOutcome.AppliedUnverified)
    {
        return Task.FromResult(new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = outcome,
            CompletedAt = DateTimeOffset.UnixEpoch
        });
    }

    private static CapabilityDescriptor Descriptor(
        CapabilityValueKind kind = CapabilityValueKind.Curve)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = Fan,
            Role = CapabilityRole.FanCurve,
            ValueKind = kind,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }

    [Fact]
    public async Task AResolvedProfileIsSentAsACurve()
    {
        CapabilityValue? sent = null;

        var outcome = await DeviceProfileApplier.ApplyAsync(
            Profile(),
            Selected(),
            Fan,
            _ => Descriptor(),
            (_, value, _) =>
            {
                sent = value;
                return Answer();
            },
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Applied, outcome);
        Assert.Equal(CapabilityValueKind.Curve, sent?.Kind);
        Assert.Equal(2, sent?.CurveValue.Count);
    }

    [Fact]
    public async Task NoSelectionSendsNothing()
    {
        var sent = false;

        var outcome = await DeviceProfileApplier.ApplyAsync(
            null,
            Selected(null),
            Fan,
            _ => Descriptor(),
            (_, _, _) =>
            {
                sent = true;
                return Answer();
            },
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.NoSelection, outcome);
        Assert.False(sent);
    }

    [Fact]
    public async Task ADanglingSelectionIsRefusedRatherThanReportedAsNoSelection()
    {
        // Different facts: a dangling reference is a mistake the user can fix once they know, and
        // no selection at all is the normal state.
        var outcome = await DeviceProfileApplier.ApplyAsync(
            null,
            Selected("deleted"),
            Fan,
            _ => Descriptor(),
            (_, _, _) => Answer(),
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Refused, outcome);
    }

    [Fact]
    public async Task ACurveTheLiveDeviceWouldRefuseIsNeverSent()
    {
        // Authoring happens with no plugin running, so the device may have changed since. Sending
        // it anyway means the plugin refuses it and the user sees a profile that does nothing.
        var sent = false;

        var outcome = await DeviceProfileApplier.ApplyAsync(
            Profile(500),
            Selected(),
            Fan,
            _ => Descriptor(),
            (_, _, _) =>
            {
                sent = true;
                return Answer();
            },
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Refused, outcome);
        Assert.False(sent);
    }

    [Fact]
    public async Task AnAbsentCapabilityIsRefusedWithoutCallingTheDevice()
    {
        var sent = false;

        var outcome = await DeviceProfileApplier.ApplyAsync(
            Profile(),
            Selected(),
            Fan,
            _ => null,
            (_, _, _) =>
            {
                sent = true;
                return Answer();
            },
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Refused, outcome);
        Assert.False(sent);
    }

    [Fact]
    public async Task ADeviceThatReportsFailureIsNotReportedAsApplied()
    {
        var outcome = await DeviceProfileApplier.ApplyAsync(
            Profile(),
            Selected(),
            Fan,
            _ => Descriptor(),
            (_, _, _) => Answer(CommandOutcome.Rejected),
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Failed, outcome);
    }
}
