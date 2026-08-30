using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceProfileApplierTests
{
    private const string Fan = "thermal.fan-curve";

    private static DeviceAuthoredProfile Profile(int output = 50) => new()
    {
        ProfileId = "quiet",
        Name = "Quiet",
        CapabilityId = Fan,
        Curve =
        [
            new AuthoredCurvePoint { Input = 0, Output = output },
            new AuthoredCurvePoint { Input = 100, Output = output },
        ],
    };

    private static DeviceProfileSelection Selection(string? global = "quiet") => new()
    {
        CapabilityId = Fan,
        GlobalProfileId = global,
    };

    private static CapabilityDescriptor Descriptor(
        CapabilityValueKind kind = CapabilityValueKind.Curve) => new()
        {
            CapabilityId = Fan,
            Role = CapabilityRole.FanCurve,
            ValueKind = kind,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
        };

    [Fact]
    public async Task AResolvedProfileIsSentAsACurve()
    {
        CapabilityValue? sent = null;
        DeviceProfileApplier applier = new(
            _ => Descriptor(),
            (_, value, _) =>
            {
                sent = value;
                return Task.FromResult(true);
            });

        DeviceProfileApplyOutcome outcome = await applier.ApplyAsync(
            [Selection()],
            [Profile()],
            Fan,
            null,
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Applied, outcome);
        Assert.Equal(CapabilityValueKind.Curve, sent?.Kind);
        Assert.Equal(2, sent?.CurveValue.Count);
    }

    [Fact]
    public async Task NoSelectionSendsNothing()
    {
        bool sent = false;
        DeviceProfileApplier applier = new(
            _ => Descriptor(),
            (_, _, _) =>
            {
                sent = true;
                return Task.FromResult(true);
            });

        DeviceProfileApplyOutcome outcome = await applier.ApplyAsync(
            [],
            [Profile()],
            Fan,
            null,
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.NoSelection, outcome);
        Assert.False(sent);
    }

    [Fact]
    public async Task ADanglingSelectionIsRefusedRatherThanReportedAsNoSelection()
    {
        // Different facts: a dangling reference is a mistake the user can fix once they know, and
        // no selection at all is the normal state.
        DeviceProfileApplier applier = new(_ => Descriptor(), (_, _, _) => Task.FromResult(true));

        DeviceProfileApplyOutcome outcome = await applier.ApplyAsync(
            [Selection("deleted")],
            [Profile()],
            Fan,
            null,
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Refused, outcome);
    }

    [Fact]
    public async Task ACurveTheLiveDeviceWouldRefuseIsNeverSent()
    {
        // Authoring happens with no plugin running, so the device may have changed since. Sending
        // it anyway means the plugin refuses it and the user sees a profile that does nothing.
        bool sent = false;
        DeviceProfileApplier applier = new(
            _ => Descriptor(),
            (_, _, _) =>
            {
                sent = true;
                return Task.FromResult(true);
            });

        DeviceProfileApplyOutcome outcome = await applier.ApplyAsync(
            [Selection()],
            [Profile(output: 500)],
            Fan,
            null,
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Refused, outcome);
        Assert.False(sent);
    }

    [Fact]
    public async Task AnAbsentCapabilityIsRefusedWithoutCallingTheDevice()
    {
        bool sent = false;
        DeviceProfileApplier applier = new(
            _ => null,
            (_, _, _) =>
            {
                sent = true;
                return Task.FromResult(true);
            });

        DeviceProfileApplyOutcome outcome = await applier.ApplyAsync(
            [Selection()],
            [Profile()],
            Fan,
            null,
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Refused, outcome);
        Assert.False(sent);
    }

    [Fact]
    public async Task ADeviceThatReportsFailureIsNotReportedAsApplied()
    {
        DeviceProfileApplier applier = new(_ => Descriptor(), (_, _, _) => Task.FromResult(false));

        DeviceProfileApplyOutcome outcome = await applier.ApplyAsync(
            [Selection()],
            [Profile()],
            Fan,
            null,
            CancellationToken.None);

        Assert.Equal(DeviceProfileApplyOutcome.Failed, outcome);
    }

    [Fact]
    public async Task TheApplicationOverrideIsTheProfileThatGetsSent()
    {
        DeviceAuthoredProfile loud = Profile(90);
        loud.ProfileId = "loud";
        CapabilityValue? sent = null;
        DeviceProfileApplier applier = new(
            _ => Descriptor(),
            (_, value, _) =>
            {
                sent = value;
                return Task.FromResult(true);
            });

        DeviceProfileSelection selection = Selection();
        selection.ApplicationOverrides =
        [
            new DeviceApplicationProfileSelection
            {
                ApplicationId = "steam:42",
                ProfileId = "loud",
            },
        ];

        await applier.ApplyAsync([selection], [Profile(), loud], Fan, "steam:42", CancellationToken.None);

        Assert.Equal(90, sent?.CurveValue[0].Output);
    }
}
