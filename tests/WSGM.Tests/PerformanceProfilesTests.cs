using WSGM.Core;

namespace WSGM.Tests;

public sealed class PerformanceProfilesTests
{
    [Fact]
    public void Resolve_NoRunningApplication_UsesTheGlobalProfile()
    {
        PerformanceConfig config = Global();

        EffectivePerformanceProfile effective = PerformanceProfiles.Resolve(config, null);

        Assert.Equal(PerformanceProfileSource.Global, effective.Source);
        Assert.Equal(60, effective.FrameLimit);
        Assert.Equal(15, effective.TdpWatts);
    }

    [Fact]
    public void Resolve_ApplicationWithItsSwitchOff_StillUsesTheGlobalProfile()
    {
        PerformanceConfig config = Global();
        config.Applications.Add(new PerformanceApplicationConfig
        {
            ApplicationId = "forza",
            UsePerGameProfile = false,
            FrameLimit = 30,
        });

        EffectivePerformanceProfile effective = PerformanceProfiles.Resolve(config, "forza");

        Assert.Equal(PerformanceProfileSource.Global, effective.Source);
        Assert.Equal(60, effective.FrameLimit);
    }

    [Fact]
    public void Resolve_ApplicationWithItsSwitchOn_UsesItsOwnValues()
    {
        PerformanceConfig config = Global();
        config.Applications.Add(new PerformanceApplicationConfig
        {
            ApplicationId = "forza",
            UsePerGameProfile = true,
            FrameLimit = 30,
        });

        EffectivePerformanceProfile effective = PerformanceProfiles.Resolve(config, "forza");

        Assert.Equal(PerformanceProfileSource.Application, effective.Source);
        Assert.Equal("forza", effective.ApplicationId);
        Assert.Equal(30, effective.FrameLimit);
    }

    [Fact]
    public void Resolve_ApplicationProfilePinningOneValue_InheritsTheRestRatherThanClearingThem()
    {
        PerformanceConfig config = Global();
        config.Applications.Add(new PerformanceApplicationConfig
        {
            ApplicationId = "forza",
            UsePerGameProfile = true,
            FrameLimit = 30,
        });

        EffectivePerformanceProfile effective = PerformanceProfiles.Resolve(config, "forza");

        Assert.Equal(30, effective.FrameLimit);
        Assert.Equal(15, effective.TdpWatts);
        Assert.True(effective.VariableRefreshRate);
    }

    [Fact]
    public void SetApplicationProfileEnabled_TurningItOffThenOn_RestoresWhatTheUserSetUp()
    {
        PerformanceConfig config = Global();
        PerformanceApplicationConfig? entry =
            PerformanceProfiles.SetApplicationProfileEnabled(config, "forza", true);
        Assert.NotNull(entry);
        entry.FrameLimit = 30;

        PerformanceProfiles.SetApplicationProfileEnabled(config, "forza", false);
        Assert.Equal(60, PerformanceProfiles.Resolve(config, "forza").FrameLimit);

        PerformanceProfiles.SetApplicationProfileEnabled(config, "forza", true);
        Assert.Equal(30, PerformanceProfiles.Resolve(config, "forza").FrameLimit);
    }

    [Fact]
    public void SetApplicationProfileEnabled_CreatesTheEntryOnFirstUse()
    {
        PerformanceConfig config = Global();

        PerformanceProfiles.SetApplicationProfileEnabled(config, "forza", true);

        Assert.Single(config.Applications);
        Assert.True(PerformanceProfiles.UsesApplicationProfile(config, "forza"));
    }

    [Fact]
    public void SetApplicationProfileEnabled_WithNoRunningApplication_ChangesNothing()
    {
        PerformanceConfig config = Global();

        Assert.Null(PerformanceProfiles.SetApplicationProfileEnabled(config, null, true));
        Assert.Empty(config.Applications);
    }

    private static PerformanceConfig Global() => new()
    {
        Enabled = true,
        FrameLimit = 60,
        OverlayLevel = 1,
        TdpWatts = 15,
        VariableRefreshRate = true,
    };
}
