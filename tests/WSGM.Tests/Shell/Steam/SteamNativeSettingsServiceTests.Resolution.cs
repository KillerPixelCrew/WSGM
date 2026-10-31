using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed partial class SteamNativeSettingsServiceTests
{
    private static readonly DisplayResolution[] TwoResolutions =
    [
        new(1280, 800),
        new(1920, 1200)
    ];

    [Fact]
    public void TwoOrMoreResolutionsMakeTheRowAvailable()
    {
        var state = SteamNativeSettingsService.ProjectResolution(
            TwoResolutions,
            new DisplayResolution(1920, 1200));

        Assert.True(state.Available);
        Assert.Equal(["1280x800", "1920x1200"], state.Options.Select(option => option.Id));
        Assert.Equal("1920x1200", state.Current);
    }

    [Fact]
    public void ASingleResolutionHidesTheRowBecauseItIsNotAChoice()
    {
        // Offering a picker that cannot change anything reads as a broken control rather than an
        // absent feature.
        var state = SteamNativeSettingsService.ProjectResolution(
            [new DisplayResolution(1920, 1200)],
            new DisplayResolution(1920, 1200));

        Assert.False(state.Available);
        Assert.Empty(state.Options);
        Assert.NotEmpty(state.StatusText);
    }

    [Fact]
    public void NoValidatedModesHidesTheRowAndSaysWhy()
    {
        var state = SteamNativeSettingsService.ProjectResolution([], null);

        Assert.False(state.Available);
        Assert.NotEmpty(state.StatusText);
    }

    [Fact]
    public void AnUnreadableCurrentModeStillLeavesTheRowUsable()
    {
        // The options are what the row needs to work; the current value is a label.
        var state = SteamNativeSettingsService.ProjectResolution(TwoResolutions, null);

        Assert.True(state.Available);
        Assert.Empty(state.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("axb")]
    [InlineData("0x0")]
    [InlineData("-1920x1200")]
    public async Task AnUnparseableValueNeverReachesTheDriver(string value)
    {
        // The value arrives from injected JavaScript, so it is parsed rather than trusted.
        List<DisplayResolution> applied = [];
        var service = new DisplayResolutionService(
            () => TwoResolutions,
            (width, height) =>
            {
                applied.Add(new DisplayResolution(width, height));
                return true;
            });

        var result = await SteamNativeSettingsService.SetResolutionAsync(service, value, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task AnOfferedValueIsApplied()
    {
        List<DisplayResolution> applied = [];
        var service = new DisplayResolutionService(
            () => TwoResolutions,
            (width, height) =>
            {
                applied.Add(new DisplayResolution(width, height));
                return true;
            });

        var result = await SteamNativeSettingsService.SetResolutionAsync(service,
            "1280x800",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new DisplayResolution(1280, 800), Assert.Single(applied));
    }

    [Fact]
    public async Task AParseableButUnofferedValueIsStillRefused()
    {
        // Parsing proves the shape, not that the display accepts it.
        List<DisplayResolution> applied = [];
        var service = new DisplayResolutionService(
            () => TwoResolutions,
            (width, height) =>
            {
                applied.Add(new DisplayResolution(width, height));
                return true;
            });

        var result = await SteamNativeSettingsService.SetResolutionAsync(service,
            "3840x2160",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(applied);
    }
}
