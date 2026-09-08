using WSGM.Shell;

namespace WSGM.Tests;

public sealed class BluetoothActionTests
{
    [Fact]
    public async Task AudioWritesOnceAndWaitsForMatchingReadback()
    {
        int writes = 0, reads = 0;
        BluetoothAudioConnection connection = new((_, _) => writes++,
            () => [new("{ABC}", ++reads >= 3)], (_, _) => Task.CompletedTask);
        Assert.True(await connection.ApplyAsync("abc", true, default));
        Assert.Equal(1, writes);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task AudioTimeoutDoesNotRetryTheWrite()
    {
        int writes = 0;
        BluetoothAudioConnection connection = new((_, _) => writes++,
            () => [new("abc", false)], (_, _) => Task.CompletedTask);
        Assert.False(await connection.ApplyAsync("abc", true, default));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task CancelledAudioRequestDoesNotTouchWindows()
    {
        BluetoothAudioConnection connection = new((_, _) => throw new InvalidOperationException(),
            () => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.ApplyAsync("abc", true, new CancellationToken(true)));
    }

    [Fact]
    public void PairDispatchesSelectedEndpointAndBlocksDuplicateAttempts()
    {
        List<string> writes = [];
        using RadioManager manager = new((id, _, _) => writes.Add(id),
            new BluetoothAudioConnection((_, _) => throw new InvalidOperationException(), () => []));
        BluetoothDeviceEntry entry = new("logical") { PairingEndpointId = "pairable-le", CanPair = true };
        Assert.True(manager.BeginPairing(entry));
        Assert.True(entry.Busy);
        Assert.False(manager.BeginPairing(entry));
        Assert.Equal(["pairable-le"], writes);
        Assert.True(manager.CancelPairing(entry));
        Assert.True(entry.Busy);
    }

    [Fact]
    public void PairDispatchFailureClearsBusyAndReportsFailure()
    {
        using RadioManager manager = new((_, _, _) => throw new InvalidOperationException("radio unavailable"),
            new BluetoothAudioConnection((_, _) => { }, () => []));
        BluetoothDeviceEntry entry = new("endpoint") { CanPair = true };
        Assert.False(manager.BeginPairing(entry));
        Assert.False(entry.Busy);
        Assert.NotEmpty(manager.StatusText);
    }
}
