using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.CapabilityBuilders;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

public sealed class CapabilityUserWritesTests
{
    [Theory]
    [InlineData(CapabilityProfileScope.Switched, false, true, ProfileLayer.Active)]
    [InlineData(CapabilityProfileScope.Switched, true, true, ProfileLayer.Active)]
    [InlineData(CapabilityProfileScope.GlobalOnly, false, true, ProfileLayer.Global)]
    [InlineData(CapabilityProfileScope.GlobalOnly, true, true, ProfileLayer.Global)]
    [InlineData(CapabilityProfileScope.NativePerApplication, false, true, ProfileLayer.Global)]
    [InlineData(CapabilityProfileScope.NativePerApplication, true, false, ProfileLayer.Active)]
    public void AUserWriteFollowsItsProfileScope(CapabilityProfileScope scope, bool gameInForce, bool command,
        ProfileLayer layer)
    {
        Assert.Equal(new CapabilityUserWrite(command, layer), CapabilityUserWrites.Decide(scope, gameInForce));
    }

    [Fact]
    public async Task AGlobalOnlyValueIsSavedToGlobalWhileAGameProfileIsOn()
    {
        var profiles = await RunningGameProfilesAsync();
        var view = View(Toggle(CapabilityProfileScope.GlobalOnly), null, null);

        Assert.True(await CapabilityUserWrites.PersistAsync(profiles, GpuPublisher, view, Flag(true),
            CancellationToken.None));

        var snapshot = profiles.Current;
        Assert.NotNull(snapshot.Config.Global.FindDevice(GpuPublisher, "graphics.toggle", null));
        Assert.Null(snapshot.Game!.Values.FindDevice(GpuPublisher, "graphics.toggle", null));
    }

    [Fact]
    public async Task ASwitchedValueIsSavedToTheRunningGame()
    {
        var profiles = await RunningGameProfilesAsync();
        var view = View(Toggle(CapabilityProfileScope.Switched), null, null);

        Assert.True(await CapabilityUserWrites.PersistAsync(profiles, GpuPublisher, view, Flag(true),
            CancellationToken.None));

        Assert.NotNull(profiles.Current.Game!.Values.FindDevice(GpuPublisher, "graphics.toggle", null));
        Assert.Null(profiles.Current.Config.Global.FindDevice(GpuPublisher, "graphics.toggle", null));
    }

    [Fact]
    public async Task AValueThatAlreadyMatchesItsLayerWritesNothing()
    {
        var profiles = await RunningGameProfilesAsync();
        var view = View(Toggle(CapabilityProfileScope.GlobalOnly), Flag(true), Flag(true), ProfileSource.Global);

        Assert.False(await CapabilityUserWrites.PersistAsync(profiles, GpuPublisher, view, Flag(true),
            CancellationToken.None));
        Assert.Empty(profiles.Current.Config.Global.Device);
    }

    [Fact]
    public async Task ANativeValueSetInAGameIsStoredForThatGameAndAccepted()
    {
        var profiles = await RunningGameProfilesAsync();
        var view = View(Toggle(CapabilityProfileScope.NativePerApplication), null, null);

        var result = await CapabilityUserWrites.StoreForApplicationAsync(profiles, GpuPublisher, view, Flag(true),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Accepted, result.Outcome);
        Assert.NotNull(profiles.Current.Game!.Values.FindDevice(GpuPublisher, "graphics.toggle", null));
        Assert.Null(profiles.Current.Config.Global.FindDevice(GpuPublisher, "graphics.toggle", null));
    }

    [Fact]
    public async Task ANativeValueThatDoesNotFitItsDescriptorIsRefusedAndNotStored()
    {
        var profiles = await RunningGameProfilesAsync();
        var view = View(Toggle(CapabilityProfileScope.NativePerApplication), null, null);

        var result = await CapabilityUserWrites.StoreForApplicationAsync(profiles, GpuPublisher, view,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 3 }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason?.Code);
        Assert.Empty(profiles.Current.Game!.Values.Device);
    }

    private static async Task<ProfileService> RunningGameProfilesAsync()
    {
        var profiles = Profiles();
        profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:42", 42, "game.exe"));
        await profiles.SetGameEnabledAsync(true);
        return profiles;
    }
}
