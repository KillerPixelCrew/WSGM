using System.Reflection;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class McuDisposalTests
{
    [Fact]
    public async Task DisposalLetsTheCurrentOwnerReleaseAndWaitersRejectWithoutOpeningHardware()
    {
        WindowsClawMcuTransport transport = new();
        // Hold the same gate as a transport operation, before any native endpoint is opened.
        SemaphoreSlim gate = Assert.IsType<SemaphoreSlim>(typeof(WindowsClawMcuTransport)
            .GetField("_serializer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport));
        await gate.WaitAsync();
        Task read = transport.ReadProfileAsync(0, 1, CancellationToken.None).AsTask();
        Task write = transport.WriteProfileAsync(0, new byte[1], CancellationToken.None).AsTask();
        Task mode = transport.SwitchModeAsync(ClawControllerMode.XInput, "test-location",
            DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None).AsTask();
        Assert.False(read.IsCompleted);
        Assert.False(write.IsCompleted);
        Assert.False(mode.IsCompleted);

        await transport.DisposeAsync();
        gate.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => write.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => mode.WaitAsync(TimeSpan.FromSeconds(2)));
        await transport.DisposeAsync();
        Assert.Equal(1, gate.CurrentCount);
    }
}
