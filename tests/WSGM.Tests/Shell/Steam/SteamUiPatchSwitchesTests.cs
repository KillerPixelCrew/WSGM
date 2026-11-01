using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>Which Steam UI patches are wanted for the surface switches in force.</summary>
public sealed class SteamUiPatchSwitchesTests
{
    [Theory]
    [InlineData(SteamPageSurface.PatchId, true)]
    [InlineData(SteamHomeCarouselSurface.PatchId, true)]
    [InlineData(SteamUiBridgePatch.PatchId, false)]
    [InlineData(SteamLibraryBadgeSurface.PatchId, false)]
    [InlineData(SteamExtensionsTabSurface.PatchId, false)]
    [InlineData(SteamGameContextMenuSurface.PatchId, false)]
    [InlineData(SteamDownloadSort.PatchId, false)]
    public void OnlyThePatchesBigPicturesOwnDocumentCarriesWaitForBigPicture(string patchId, bool bigPictureOnly)
    {
        // The desktop client's document has no Big Picture router and no Home: those two failed every
        // retry there while every other patch verified (tester log, 2026-11-01).
        Assert.Equal(bigPictureOnly, SteamUiPatchSwitches.BigPictureOnly(patchId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSwitchesCarryWhetherBigPictureIsUp(bool bigPicture)
    {
        var switches = SteamUiSurfaceSwitches.From(new AppConfig(), true, null, false, bigPicture);

        Assert.Equal(bigPicture, switches.BigPicture);
        Assert.NotEqual(switches, switches with { BigPicture = !bigPicture });
    }
}
