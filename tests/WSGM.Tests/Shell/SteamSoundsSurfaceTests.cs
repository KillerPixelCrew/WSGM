using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamSoundsSurfaceTests
{
    private static SoundPack Pack(string id, string? storeId = null, string version = "1.0", string? error = null)
    {
        return new SoundPack(id, id + " name", "Author", version, "Description", storeId, error,
            new Dictionary<string, string[]>(), new HashSet<string>())
        {
            Assets = ["a.wav", "b.wav"],
            Compatibility = new SoundPackCompatibility(["deck_ui_navigation.wav"], ["deck_ui_toast.wav"],
                ["deck_ui_launch_game.wav"], ["unknown.wav"], ["missing.wav is missing"])
        };
    }

    private static ThemeStoreSummary Listing(string id, string version)
    {
        return new ThemeStoreSummary(id, id, "Listing " + id, version, "Audio", [], 3, "", "Uploader", ["blob"],
            "download", 7, 2, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static SoundPackState State(params SoundPack[] packs)
    {
        return new SoundPackState(packs, "store", true, "Refused.", "Mapped.",
            [Listing("s1", "1.0"), Listing("s2", "2.0"), Listing("s3", "1.0")], 30, 2)
        {
            Integration = "Connected."
        };
    }

    [Fact]
    public void TheProjectionCarriesTheChoiceTheStatusAndThePage()
    {
        var state = SteamSoundsSurface.Project(State(Pack("store", "s1")), 9, blob => "https://blobs/" + blob);

        Assert.Equal("store", state.Selected);
        Assert.True(state.Busy);
        Assert.Equal("Refused.", state.Error);
        Assert.Equal("Mapped.", state.Compatibility);
        Assert.Equal("Connected.", state.Integration);
        Assert.Equal(9, state.Revision);
        Assert.Equal(2, state.Browse.Page);
        Assert.Equal(30, state.Browse.Total);
        Assert.Equal(ThemeStoreClient.SoundsPageSize, state.Browse.PageSize);
    }

    [Fact]
    public void AListingSaysWhichVersionIsInstalledSoThePageCanOfferAnUpdate()
    {
        var state = SteamSoundsSurface.Project(State(Pack("one", "s1"), Pack("two", "s2", "1.5"), Pack("local")), 1,
            blob => "https://blobs/" + blob);

        Assert.Equal("1.0", state.Browse.Items[0].InstalledVersion);
        Assert.Equal("1.5", state.Browse.Items[1].InstalledVersion);
        Assert.Null(state.Browse.Items[2].InstalledVersion);
        Assert.Equal("https://blobs/blob", state.Browse.Items[0].ImageUrl);
        Assert.Equal("Uploader", state.Browse.Items[0].Author);
        Assert.Equal("1 Oct 2026", state.Browse.Items[0].Updated);
    }

    [Fact]
    public void APackListsWhatItReplacesWhatKeepsSteamsSoundAndWhatIsWrong()
    {
        var pack = SteamSoundsSurface.Project(State(Pack("one")), 1, blob => blob).Packs[0];

        Assert.Equal(new[] { "deck_ui_navigation.wav" }, pack.Mapped);
        Assert.Equal(new[] { "deck_ui_toast.wav", "deck_ui_launch_game.wav" }, pack.Defaults);
        Assert.Equal(new[] { "missing.wav is missing", "Unknown client resource: unknown.wav" }, pack.Problems);
        Assert.Equal(new[] { "a.wav", "b.wav" }, pack.Assets);
        Assert.NotNull(pack.Compatibility);
    }

    [Fact]
    public void AnUnusablePackOffersNothingToPreview()
    {
        var pack = SteamSoundsSurface.Project(State(Pack("broken", error: "Bad manifest.")), 1, blob => blob).Packs[0];

        Assert.Equal("Bad manifest.", pack.Error);
        Assert.Empty(pack.Assets);
    }
}
