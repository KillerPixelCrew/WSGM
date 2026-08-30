using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceOverlayAuthoredProfileTests
{
    private static DeviceAuthoredProfile Profile(string id, string name) => new()
    {
        ProfileId = id,
        Name = name,
        CapabilityId = "thermal.fan-curve",
    };

    [Fact]
    public void NoAuthoredProfilesShowsNoRowAtAll()
    {
        // Unlike the hardware-profile row, which is always present because the user cannot create
        // one. These are created in Settings, so a row offering a choice between nothing would
        // invite a press that cannot do anything.
        Assert.Null(DeviceOverlayBridge.AuthoredProfileView([], null, false));
    }

    [Fact]
    public void AProfileChosenForEverythingSaysSo()
    {
        DeviceOverlayAuthoredProfile? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet"), Profile("loud", "Loud")],
            "quiet",
            applicationScoped: false);

        Assert.Equal("QUIET", row?.TrailingText);
        Assert.Contains("everything", row?.Description);
    }

    [Fact]
    public void AProfileChosenForOneGameSaysThatInstead()
    {
        // The same word with very different consequences: this is the difference the user opens the
        // row to check mid-game.
        DeviceOverlayAuthoredProfile? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            applicationScoped: true);

        Assert.Contains("this game only", row?.Description);
    }

    [Fact]
    public void NothingChosenReadsAsNoneRatherThanEmpty()
    {
        DeviceOverlayAuthoredProfile? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            null,
            applicationScoped: false);

        Assert.Equal("NONE", row?.TrailingText);
        Assert.Equal(DeviceOverlayStatus.None, row?.Status);
    }

    [Fact]
    public void ASelectionNamingADeletedProfileIsSaidPlainlyNotShownAsNone()
    {
        // None is a state the user chose; this is not, and showing them identically hides a
        // selection that has silently stopped working.
        DeviceOverlayAuthoredProfile? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            applicationScoped: true);

        Assert.Equal("MISSING", row?.TrailingText);
        Assert.Equal(DeviceOverlayStatus.Warning, row?.Status);
    }

    [Fact]
    public void TheRowReportsStateAndDoesNotOfferACycleYet()
    {
        // Choosing needs a write path through the overlay source that does not exist. A row that
        // takes a press and does nothing is worse than one that plainly reports state.
        DeviceOverlayAuthoredProfile? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            applicationScoped: false);

        Assert.False(row?.CanCycle);
    }
}
