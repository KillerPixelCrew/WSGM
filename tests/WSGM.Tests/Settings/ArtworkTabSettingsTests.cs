using WSGM.Core;
using WSGM.Settings;

namespace WSGM.Tests.Settings;

/// <summary>
///     The artwork page's tab strip, as Settings edits it. The rows' own order is the stored order,
///     so reordering has to survive a save and come back the same way.
/// </summary>
public sealed class ArtworkTabSettingsTests
{
    private static AppConfig Configured(string tabOrder, string defaultTab, bool showHero = true)
    {
        return ConfigStore.Normalize(new AppConfig
        {
            Artwork = new ArtworkConfig { TabOrder = tabOrder, DefaultTab = defaultTab, ShowHero = showHero }
        });
    }

    [Fact]
    public void TheRowsComeUpInTheStoredOrderWithTheStoredVisibility()
    {
        var model = new SettingsViewModel(
            Configured("hero,grid,wide,logo,icon,manage", "grid", false));

        Assert.Equal(["hero", "grid", "wide", "logo", "icon", "manage"],
            model.ArtworkTabs.Select(row => row.Id));
        Assert.False(model.ArtworkTabs[0].Visible);
        Assert.Equal(1, model.ArtworkDefaultTabIndex);
    }

    [Fact]
    public void AStoredOrderMissingATabStillOffersEveryTab()
    {
        // Settings opens against whatever is on disk, including a file written by an older build
        // or edited by hand. A tab left out of the order must not become unreachable.
        var model = new SettingsViewModel(Configured("wide,grid", "wide"));

        Assert.Equal(6, model.ArtworkTabs.Count);
        Assert.Equal(["wide", "grid"], model.ArtworkTabs.Take(2).Select(row => row.Id));
    }

    [Fact]
    public void MovingATabKeepsTheDefaultOnTheTabItNames()
    {
        // The user means "this tab opens first", not "whatever ends up in this position".
        var model = new SettingsViewModel(Configured(ArtworkConfig.DefaultTabOrder, "wide"));
        var wide = model.ArtworkTabs.Single(row => row.Id == "wide");

        model.MoveArtworkTabUpCommand.Execute(wide);

        Assert.Equal("wide", model.ArtworkTabs[0].Id);
        Assert.Equal(0, model.ArtworkDefaultTabIndex);
    }

    [Fact]
    public void MovingPastEitherEndDoesNothing()
    {
        var model = new SettingsViewModel(Configured(ArtworkConfig.DefaultTabOrder, "grid"));
        var expected = model.ArtworkTabs.Select(row => row.Id).ToList();

        model.MoveArtworkTabUpCommand.Execute(model.ArtworkTabs[0]);
        model.MoveArtworkTabDownCommand.Execute(model.ArtworkTabs[^1]);

        Assert.Equal(expected, model.ArtworkTabs.Select(row => row.Id));
    }

    [Fact]
    public void AReorderedStripAndItsHiddenTabsSurviveASave()
    {
        var model = new SettingsViewModel(Configured(ArtworkConfig.DefaultTabOrder, "grid"));
        model.MoveArtworkTabUpCommand.Execute(model.ArtworkTabs.Single(row => row.Id == "icon"));
        model.ArtworkTabs.Single(row => row.Id == "logo").Visible = false;
        model.ArtworkDefaultTabIndex =
            model.ArtworkTabs.ToList().FindIndex(row => row.Id == "hero");

        // The same ApplyTo the save path runs, through the window's own snapshot seam.
        var saved = ConfigStore.Normalize(model.SnapshotForPreview());

        Assert.Equal("grid,wide,hero,icon,logo,manage", saved.Artwork.TabOrder);
        Assert.False(saved.Artwork.ShowLogo);
        Assert.True(saved.Artwork.ShowIcon);
        Assert.Equal("hero", saved.Artwork.DefaultTab);
    }
}
