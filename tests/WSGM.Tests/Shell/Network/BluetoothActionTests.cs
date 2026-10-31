using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class BluetoothActionTests
{
    [Fact]
    public async Task AcceptedAudioRequestIsPublishedAsTheObservedStateWithoutReadback()
    {
        List<(string Container, bool Connect)> writes = [];
        using RadioManager manager = new((_, _, _) => throw new InvalidOperationException(),
            (container, connect) => writes.Add((container, connect)));
        var entry = AudioEntry();
        Assert.True(await manager.SetAudioConnectionAsync(entry, true));
        Assert.Equal([("abc", true)], writes);
        Assert.True(entry.AudioActive);
        Assert.False(entry.Busy);
    }

    [Fact]
    public async Task RefusedAudioRequestReportsFailureWithoutRetrying()
    {
        var writes = 0;
        using RadioManager manager = new((_, _, _) => throw new InvalidOperationException(),
            (_, _) =>
            {
                writes++;
                throw new InvalidOperationException("No endpoint accepted the Bluetooth audio request.");
            });
        var entry = AudioEntry();
        Assert.False(await manager.SetAudioConnectionAsync(entry, true));
        Assert.Equal(1, writes);
        Assert.False(entry.AudioActive);
        Assert.False(entry.Busy);
        Assert.NotEmpty(manager.StatusText);
    }

    [Fact]
    public async Task CancelledAudioRequestDoesNotTouchWindows()
    {
        using RadioManager manager = new((_, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException("A cancelled request must not reach Windows."));
        var entry = AudioEntry();
        Assert.False(await manager.SetAudioConnectionAsync(entry, true, new CancellationToken(true)));
        Assert.False(entry.AudioActive);
        Assert.False(entry.Busy);
    }

    [Fact]
    public void PairDispatchesSelectedEndpointAndBlocksDuplicateAttempts()
    {
        List<string> writes = [];
        // The attempt never ends on its own, as a real pairing waits on the device and the user.
        using RadioManager manager = new((id, _, _) =>
            {
                writes.Add(id);
                return new TaskCompletionSource<WindowsRadio.PairingResult>().Task;
            },
            (_, _) => throw new InvalidOperationException());
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
            (_, _) => { });
        BluetoothDeviceEntry entry = new("endpoint") { CanPair = true };
        Assert.False(manager.BeginPairing(entry));
        Assert.False(entry.Busy);
        Assert.NotEmpty(manager.StatusText);
    }

    private static BluetoothDeviceEntry AudioEntry()
    {
        return new BluetoothDeviceEntry("logical") { Paired = true, AudioConnectable = true, ContainerId = "abc" };
    }
}
