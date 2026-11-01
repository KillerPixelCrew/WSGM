using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class GameToolChannelTests
{
    [Fact]
    public void ANightlyAssetNamesItsVersionAndBuildDate()
    {
        var nightly = GameToolCatalog.Current.Find("optiscaler")!.Channel("nightly")!;
        var found = new HostedRelease("nightly-20261031", "nightly-20261031:1:abc", "", "GitHub",
            new ReleaseAsset("OptiScaler_v10.0.0-pre1_20261031.7z", "https://example.test/a.7z"));

        var release = nightly.Normalize(found);

        Assert.Equal("10.0.0-pre1", release.Version);
        Assert.Equal(found.Id, release.Id);
        Assert.Equal(new DateOnly(2026, 10, 31), nightly.BuildDate(release.Asset.Name));
        Assert.Equal("10.0.0-pre1 · nightly 31 Oct 2026", nightly.Display(release.Version, release.Asset.Name));
        Assert.Throws<InvalidDataException>(() => nightly.Normalize(found with
        {
            Asset = found.Asset with { Name = "OptiScaler_latest.7z" }
        }));
    }

    [Fact]
    public void AStableReleaseKeepsItsTagAndShowsNoDate()
    {
        var stable = GameToolCatalog.Current.Find("optiscaler")!.DefaultChannel;
        var found = new HostedRelease("0.9.4", "v0.9.4:1:abc", "", "GitHub",
            new ReleaseAsset("Optiscaler_0.9.4-final.20260718._MM.7z", "https://example.test/a.7z"));

        Assert.Same(found, stable.Normalize(found));
        Assert.Null(stable.BuildDate(found.Asset.Name));
        Assert.Equal("0.9.4", stable.Display(found.Version, found.Asset.Name));
    }
}
