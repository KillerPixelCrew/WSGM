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
