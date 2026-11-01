using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

public sealed class ApplicationProfileSyncBuilderTests
{
    [Fact]
    public void TheSyncCarriesEveryEnabledGameWithAnExecutableAndANativeValue()
    {
        ProfileConfig config = new();
        config.Games.Add(GameWith("steam:1", [], ["Learned.exe"], true, GpuPublisher, "native"));
        config.Games.Add(GameWith("steam:2", [], [], true, GpuPublisher, "native"));
        config.Games.Add(GameWith("steam:3", ["off.exe"], [], false, GpuPublisher, "native"));
        config.Games.Add(GameWith("process:tool.exe", [], [], true, GpuPublisher, "switched"));
        config.Games.Add(GameWith("steam:4", ["other.exe"], [], true, "gpu:wsgm.other", "native"));
        config.Games.Add(GameWith("steam:5", ["b.exe"], ["a.exe"], true, GpuPublisher, "native"));

        var profiles = ApplicationProfileSyncBuilder.Build(config, GpuPublisher,
        [
            Toggle(CapabilityProfileScope.NativePerApplication, "native"),
            Toggle(CapabilityProfileScope.Switched, "switched")
        ]);

        Assert.Equal(["steam:1", "steam:5"], profiles.Select(profile => profile.ProfileId));
        Assert.Equal(["Learned.exe"], profiles[0].Executables);
        Assert.Equal(["a.exe", "b.exe"], profiles[1].Executables);
        Assert.Equal("native", Assert.Single(profiles[0].Values).CapabilityId);
    }

    [Fact]
    public void AProcessProfileCarriesItsOwnExecutable()
    {
        ProfileConfig config = new();
        config.Games.Add(GameWith("process:tool.exe", [], [], true, GpuPublisher, "native"));

        var profile = Assert.Single(ApplicationProfileSyncBuilder.Build(config, GpuPublisher,
            [Toggle(CapabilityProfileScope.NativePerApplication, "native")]));

        Assert.Equal(["tool.exe"], profile.Executables);
    }

    [Fact]
    public void ANewlyLearnedExecutableChangesTheFingerprint()
    {
        ProfileConfig config = new();
        config.Games.Add(GameWith("steam:1", [], ["Game.exe"], true, GpuPublisher, "native"));
        CapabilityDescriptor[] descriptors = [Toggle(CapabilityProfileScope.NativePerApplication, "native")];
        var before = ApplicationProfileSyncBuilder.Fingerprint(
            ApplicationProfileSyncBuilder.Build(config, GpuPublisher, descriptors));
        var again = ApplicationProfileSyncBuilder.Fingerprint(
            ApplicationProfileSyncBuilder.Build(config, GpuPublisher, descriptors));

        Assert.True(ProfileEdits.LearnExecutable(config, "steam:1", "GameDx12.exe"));
        var after = ApplicationProfileSyncBuilder.Fingerprint(
            ApplicationProfileSyncBuilder.Build(config, GpuPublisher, descriptors));

        Assert.Equal(before, again);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AValueThatNoLongerFitsItsDescriptorIsLeftOut()
    {
        ProfileConfig config = new();
        var game = GameWith("steam:1", ["game.exe"], [], true, GpuPublisher, "native");
        game.Values.SetDevice(GpuPublisher, "native", null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 1 });
        config.Games.Add(game);

        Assert.Empty(ApplicationProfileSyncBuilder.Build(config, GpuPublisher,
            [Toggle(CapabilityProfileScope.NativePerApplication, "native")]));
    }
}
