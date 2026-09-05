using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Identity;

using WSGM.Device.Tests;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

[Collection("plugin-trace")]
public sealed class AcPowerObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcPowerFailureKeepsTheDeviceAvailableWithAConservativePowerSource(bool comFailure)
    {
        WindowsClawIdentityReader reader = Reader(() => throw (comFailure
            ? new COMException("test failure") : new UnauthorizedAccessException("test refusal")));

        ClawIdentityState identity = await reader.ReadAsync(CancellationToken.None);

        Assert.True(identity.ExactMachineMatch);
        Assert.False(identity.OnAcPower);
    }

    [Fact]
    public async Task CancellationDoesNotWaitForABlockedAcPowerQuery()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsClawIdentityReader reader = Reader(() =>
        {
            entered.SetResult();
            release.Wait();
            exited.SetResult();
            return true;
        });
        using CancellationTokenSource cancellation = new();
        try
        {
            Task<ClawIdentityState> read = reader.ReadAsync(cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static WindowsClawIdentityReader Reader(Func<bool> readPower)
    {
        FakeWmiTransport wmi = new();
        wmi.SetResponse("Get_WMI", 0, new byte[32]);
        wmi.SetResponse("Get_EC", 0, new byte[32]);
        return new WindowsClawIdentityReader(wmi, () => new DeviceIdentitySnapshot
        {
            SystemManufacturer = ClawHardwareFacts.Manufacturer,
            BaseboardProduct = ClawHardwareFacts.BoardProduct,
            SystemSku = ClawHardwareFacts.SystemSku,
        }, () => [], readPower);
    }
}
