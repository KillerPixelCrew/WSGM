using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SteamControllerOwnershipAdapterTests
{
    [Fact]
    public async Task PhysicalReleasePrecedesSteamAccessAndSteamBlockPrecedesPhysicalRestore()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => { calls.Add("release-physical"); return Task.FromResult(true); },
            _ => { calls.Add("restore-physical"); return Task.FromResult(SteamPhysicalRestoreResult.Restored); },
            new Gate(calls));
        Assert.True(await adapter.ReleaseAsync(CancellationToken.None));
        Assert.True(await adapter.RestoreAsync(CancellationToken.None));
        Assert.Equal(["support", "release-physical", "pass-through", "block-steam", "restore-physical", "end-block"], calls);
    }

    [Fact]
    public async Task UnsupportedNativePayloadDoesNotReleasePhysicalController()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => throw new InvalidOperationException("must not touch physical controller"),
            _ => Task.FromResult(SteamPhysicalRestoreResult.Restored), new Gate(calls) { Supported = false });
        Assert.False(await adapter.ReleaseAsync(CancellationToken.None));
        Assert.Equal(["support"], calls);
    }

    [Fact]
    public async Task UnverifiedPhysicalReleaseNeverGrantsSteamPassThrough()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(false), _ => Task.FromResult(SteamPhysicalRestoreResult.Restored), new Gate(calls));
        Assert.False(await adapter.ReleaseAsync(CancellationToken.None));
        Assert.Equal(["support"], calls);
    }

    [Fact]
    public async Task OverlappingNativeOverridePreventsPhysicalReacquisition()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true),
            _ => throw new InvalidOperationException("Steam still owns input"),
            new Gate(calls) { RestoreAllowed = false });
        Assert.False(await adapter.RestoreAsync(CancellationToken.None));
        Assert.Equal(["block-steam"], calls);
    }

    [Fact]
    public async Task UnverifiedPhysicalRestorationRetainsTransitionBlockUntilTeardown()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true), _ => Task.FromResult(SteamPhysicalRestoreResult.Unverified), new Gate(calls));
        Assert.False(await adapter.RestoreAsync(CancellationToken.None));
        Assert.Equal(["block-steam"], calls);
    }

    [Fact]
    public async Task SteamExitDiscardsDeadClaimsAndRestoresPhysicalOwnership()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true),
            _ => { calls.Add("restore-physical"); return Task.FromResult(SteamPhysicalRestoreResult.Restored); },
            new Gate(calls), () => false);
        Assert.True(await adapter.RestoreAsync(CancellationToken.None));
        Assert.Equal(["dispose", "restore-physical"], calls);
    }

    [Fact]
    public async Task RetiredDeviceOwnerDropsNativeClaimsWithoutBlockingOrReacquiring()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true),
            _ => throw new InvalidOperationException("The retired owner must not reacquire"),
            new Gate(calls), ownerIsCurrent: _ => Task.FromResult(false));
        Assert.True(await adapter.RestoreAsync(CancellationToken.None));
        Assert.Equal(["dispose"], calls);
    }

    [Fact]
    public async Task OwnerChangingDuringRestoreDropsTheTemporaryBlock()
    {
        List<string> calls = [];
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true),
            _ => Task.FromResult(SteamPhysicalRestoreResult.OwnerChanged), new Gate(calls));
        Assert.True(await adapter.RestoreAsync(CancellationToken.None));
        Assert.Equal(["block-steam", "dispose"], calls);
    }

    [Fact]
    public async Task DisconnectWaitsWithoutNativeBlockThenReacquiresOnceAfterReconnection()
    {
        List<string> calls = [];
        int present = 0;
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true),
            _ => { calls.Add("restore-physical"); return Task.FromResult(SteamPhysicalRestoreResult.Restored); },
            new Gate(calls), physicalIsPresent: _ => Task.FromResult(Volatile.Read(ref present) == 1));
        Task<bool> restore = adapter.RestoreAsync(CancellationToken.None);
        Assert.False(restore.IsCompleted);
        Assert.Empty(calls);
        Volatile.Write(ref present, 1);
        Assert.True(await restore.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(["block-steam", "restore-physical", "end-block"], calls);
    }

    [Fact]
    public async Task RetiringOwnerDuringDisconnectWaitReleasesClaimsWithoutReacquisition()
    {
        List<string> calls = [];
        int current = 1;
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true), _ => throw new InvalidOperationException("Retired device"),
            new Gate(calls), ownerIsCurrent: _ => Task.FromResult(Volatile.Read(ref current) == 1),
            physicalIsPresent: _ => Task.FromResult(false));
        Task<bool> restore = adapter.RestoreAsync(CancellationToken.None);
        Assert.Empty(calls);
        Volatile.Write(ref current, 0);
        Assert.True(await restore.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(["dispose"], calls);
    }

    [Fact]
    public async Task ShutdownCancelsDisconnectWaitWithoutAnyHardwareAcquisition()
    {
        List<string> calls = [];
        using CancellationTokenSource cancellation = new();
        using SteamControllerOwnershipAdapter adapter = new(
            _ => Task.FromResult(true), _ => throw new InvalidOperationException("No acquisition during shutdown"),
            new Gate(calls), physicalIsPresent: _ => Task.FromResult(false));
        Task<bool> restore = adapter.RestoreAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.Empty(calls);
    }

    private sealed class Gate(List<string> calls) : ISteamControllerGate
    {
        internal bool Supported { get; init; } = true;
        internal bool RestoreAllowed { get; init; } = true;
        public bool SupportsPassThrough { get { calls.Add("support"); return Supported; } }
        public bool OriginalSteamExited => false;
        public bool BeginPassThrough() { calls.Add("pass-through"); return true; }
        public bool BeginRestore() { calls.Add("block-steam"); return RestoreAllowed; }
        public void EndRestore() => calls.Add("end-block");
        public void Dispose() => calls.Add("dispose");
    }
}
