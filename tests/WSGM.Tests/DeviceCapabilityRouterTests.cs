using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceCapabilityRouterTests
{
    [Fact]
    public async Task DisconnectedRouterRejectsACommandWithAnActionableReason()
    {
        await using DeviceCapabilityRouter router = new(cycleGeneration: 7, action => action());

        CapabilityCommandResult result = await router.ExecuteAsync(
            "power.sustained",
            instanceId: null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 },
            TimeSpan.FromSeconds(1));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.HostUnavailable, result.Reason?.Code);
        Assert.True(result.Reason!.Retryable);
    }

    [Fact]
    public async Task OnlyTheNewestPostedSnapshotCanReachTheUi()
    {
        List<Action> posted = [];
        await using DeviceCapabilityRouter router = new(cycleGeneration: 7, posted.Add);
        var notifications = 0;
        router.Changed += _ => notifications++;

        router.SetTemporaryDesired(
            "power.sustained",
            instanceId: null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 });
        router.ClearTemporaryDesired();

        Assert.Equal(2, posted.Count);
        posted[0]();
        Assert.Equal(0, notifications);
        posted[1]();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task DisposalClosesCommandAdmissionWithoutDisposingAnOwnedGate()
    {
        DeviceCapabilityRouter router = new(cycleGeneration: 7, action => action());
        await router.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.ExecuteAsync(
            "power.sustained",
            instanceId: null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 },
            TimeSpan.FromSeconds(1)));
    }
}
