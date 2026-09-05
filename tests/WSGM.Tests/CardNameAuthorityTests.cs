using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>What names a tracked card. A card reader hands every card the same drive
/// letter, and Steam's <c>libraryfolders.vdf</c> label belongs to the registration at
/// that path, so a swap leaves the new card's content id carrying the previous card's
/// label. The card's own <c>libraryfolder.vdf</c> marker is the only name that travels
/// with the media, so it is the only one discovery follows.</summary>
public class CardNameAuthorityTests
{
    private static LibraryTabManager.Discovered Card(
        string contentId, string markerLabel, string fallbackName = "Library (E:)")
        => new(contentId, fallbackName, [], 'E', markerLabel);

    private static AppConfig ConfigWith(params (string ContentId, string Name)[] cards)
    {
        var config = new AppConfig();
        foreach (var (contentId, name) in cards)
        {
            config.CardLibraries.Add(new CardLibraryConfig
            {
                ContentId = contentId,
                Name = name,
                Enabled = true,
            });
        }
        return config;
    }

    private static string NameOf(AppConfig config, string contentId)
        => config.CardLibraries.Single(card => card.ContentId == contentId).Name;

    [Fact]
    public void AnUntrackedCardIsAddedEnabledUnderItsMarkerLabel()
    {
        var config = new AppConfig();

        LibraryTabManager.MergeDiscovery(config, [Card("777", "Indies")]);

        var card = Assert.Single(config.CardLibraries);
        Assert.Equal("777", card.ContentId);
        Assert.Equal("Indies", card.Name);
        Assert.True(card.Enabled);
    }

    [Fact]
    public void AnUntrackedCardWithNoMarkerLabelTakesTheScansFallbackName()
    {
        var config = new AppConfig();

        LibraryTabManager.MergeDiscovery(config, [Card("777", "", "Library (E:)")]);

        Assert.Equal("Library (E:)", NameOf(config, "777"));
    }

    [Fact]
    public void ATrackedCardFollowsItsOwnMarkerLabel()
    {
        // A rename made in Steam, or by a previous WSGM build, reaches WSGM only
        // because it is written into the card's marker.
        var config = ConfigWith(("777", "SDCard9"));

        LibraryTabManager.MergeDiscovery(config, [Card("777", "Retro")]);

        Assert.Equal("Retro", NameOf(config, "777"));
    }

    [Fact]
    public void OneCardsLabelNeverRenamesADifferentCard()
    {
        // The reported defect. Two cards pass through the same reader path; Steam
        // carried the first card's label onto the second card's registration and the
        // merge adopted it, so both cards ended up called SDCard9. Discovery no longer
        // reads that label, so each card keeps the name on its own media.
        var config = ConfigWith(("9696", "SDCard9"), ("1010", "SDCard10"));

        LibraryTabManager.MergeDiscovery(config, [Card("1010", "SDCard10")]);

        Assert.Equal("SDCard9", NameOf(config, "9696"));
        Assert.Equal("SDCard10", NameOf(config, "1010"));
    }

    [Fact]
    public void ACardWithNoMarkerLabelKeepsTheNameItWasGivenHere()
    {
        // Nothing on the media contradicts the tracked name, so a rename made in the
        // card manager must survive the next scan rather than fall back to the
        // drive-letter guess.
        var config = ConfigWith(("777", "Handhelds"));

        LibraryTabManager.MergeDiscovery(config, [Card("777", "", "Library (E:)")]);

        Assert.Equal("Handhelds", NameOf(config, "777"));
    }

    [Fact]
    public void AnEjectedCardIsLeftUntouched()
    {
        var config = ConfigWith(("9696", "SDCard9"), ("1010", "SDCard10"));
        config.CardLibraries.Single(card => card.ContentId == "9696").AppIds.Add(4242);

        LibraryTabManager.MergeDiscovery(config, [Card("1010", "SDCard10")]);

        var ejected = config.CardLibraries.Single(card => card.ContentId == "9696");
        Assert.Equal("SDCard9", ejected.Name);
        Assert.Equal([4242L], ejected.AppIds);
    }

    [Fact]
    public void AForgottenCardStaysForgottenWhileItIsStillInserted()
    {
        var config = new AppConfig();
        config.ForgottenInsertedCardIds.Add("777");

        LibraryTabManager.MergeDiscovery(config, [Card("777", "Indies")]);

        Assert.Empty(config.CardLibraries);
        Assert.Equal(["777"], config.ForgottenInsertedCardIds);
    }

    [Fact]
    public void ForgettingIsReleasedOnceTheCardHasLeftTheReader()
    {
        var config = new AppConfig();
        config.ForgottenInsertedCardIds.Add("777");

        LibraryTabManager.MergeDiscovery(config, []);

        Assert.Empty(config.ForgottenInsertedCardIds);
    }

    [Fact]
    public void InstalledAppIdsAreRefreshedFromTheMediaOnEveryScan()
    {
        var config = ConfigWith(("777", "Indies"));
        config.CardLibraries[0].AppIds.AddRange([1L, 2L]);

        LibraryTabManager.MergeDiscovery(
            config,
            [new LibraryTabManager.Discovered("777", "Library (E:)", [3L], 'E', "Indies")]);

        Assert.Equal([3L], config.CardLibraries[0].AppIds);
    }

    // ---- the rename's authoritative write ----
    //
    // RenameCardAsync itself orchestrates ConfigStore, Steam's CEF client and the
    // Windows volume label, none of which the unattended suite may touch. Its one
    // decisive step is the marker write: everything else in that method is now
    // conditional on this returning null, so this is where the contract is proven.

    private static string NewLibrary(TemporaryDirectory temp, string contentId, string label)
    {
        var library = Directory.CreateDirectory(temp.GetPath("SteamLibrary")).FullName;
        File.WriteAllText(
            Path.Combine(library, "libraryfolder.vdf"),
            SteamLibraryVdf.BuildMarker(contentId, @"C:\Steam\steam.exe", label));
        return library;
    }

    [Fact]
    public void RenamingWritesTheNewNameOntoTheCard()
    {
        using var temp = new TemporaryDirectory();
        var library = NewLibrary(temp, "777", "SDCard9");

        Assert.Null(LibraryTabManager.TrySetMarkerLabel(library, "777", "Handhelds"));

        Assert.True(SteamLibraryVdf.TryReadMarker(library, out var id, out var label));
        Assert.Equal("777", id);
        Assert.Equal("Handhelds", label);
    }

    [Fact]
    public void RenamingIsRefusedWhenTheCardInTheReaderIsADifferentOne()
    {
        // The card was swapped between resolving its drive letter and the write. The
        // letter still resolves, so only the identity check stands between the rename
        // and relabelling somebody else's card.
        using var temp = new TemporaryDirectory();
        var library = NewLibrary(temp, "1010", "SDCard10");

        var note = LibraryTabManager.TrySetMarkerLabel(library, "9696", "Handhelds");

        Assert.Equal("The card still carries its old name.", note);
        Assert.True(SteamLibraryVdf.TryReadMarker(library, out var id, out var label));
        Assert.Equal("1010", id);
        Assert.Equal("SDCard10", label);
    }

    [Fact]
    public void RenamingIsRefusedWhenTheMarkerHasGone()
    {
        using var temp = new TemporaryDirectory();

        Assert.Equal(
            "The card still carries its old name.",
            LibraryTabManager.TrySetMarkerLabel(temp.Root, "777", "Handhelds"));
    }

    [Fact]
    public void RenamingAnUnlabelledCardGivesItsMarkerALabel()
    {
        using var temp = new TemporaryDirectory();
        var library = Directory.CreateDirectory(temp.GetPath("SteamLibrary")).FullName;
        File.WriteAllText(
            Path.Combine(library, "libraryfolder.vdf"),
            "\"libraryfolder\"\n{\n\t\"contentid\"\t\t\"777\"\n}\n");

        Assert.Null(LibraryTabManager.TrySetMarkerLabel(library, "777", "Handhelds"));

        Assert.True(SteamLibraryVdf.TryReadMarker(library, out _, out var label));
        Assert.Equal("Handhelds", label);
    }

    [Fact]
    public void RenamingTwiceLeavesOneLabelAndTheSameIdentity()
    {
        using var temp = new TemporaryDirectory();
        var library = NewLibrary(temp, "777", "SDCard9");

        Assert.Null(LibraryTabManager.TrySetMarkerLabel(library, "777", "Handhelds"));
        Assert.Null(LibraryTabManager.TrySetMarkerLabel(library, "777", "Retro"));

        var marker = File.ReadAllText(Path.Combine(library, "libraryfolder.vdf"));
        Assert.Single(SteamLibraryVdf.ValuesOf(marker, "label"));
        Assert.True(SteamLibraryVdf.TryReadMarker(library, out var id, out var label));
        Assert.Equal("777", id);
        Assert.Equal("Retro", label);
    }

    [Fact]
    public void RenamingLeavesNoTemporaryFileBehind()
    {
        using var temp = new TemporaryDirectory();
        var library = NewLibrary(temp, "777", "SDCard9");

        Assert.Null(LibraryTabManager.TrySetMarkerLabel(library, "777", "Handhelds"));

        Assert.Equal(
            ["libraryfolder.vdf"],
            Directory.GetFiles(library).Select(Path.GetFileName));
    }

    [Fact]
    public void ARenamedCardKeepsItsNameThroughTheNextScan()
    {
        // The whole point of writing the media: discovery reads the name back and must
        // agree with it rather than revert.
        using var temp = new TemporaryDirectory();
        var library = NewLibrary(temp, "777", "SDCard9");
        var config = ConfigWith(("777", "SDCard9"));

        Assert.Null(LibraryTabManager.TrySetMarkerLabel(library, "777", "Handhelds"));
        Assert.True(SteamLibraryVdf.TryReadMarker(library, out _, out var label));
        LibraryTabManager.MergeDiscovery(config, [Card("777", label)]);

        Assert.Equal("Handhelds", NameOf(config, "777"));
    }

    [Fact]
    public void MergingTheSameScanTwiceIsIdempotent()
    {
        // One sync merges twice: into the snapshot it builds tabs from, then into the
        // freshly locked config it saves.
        var config = ConfigWith(("777", "SDCard9"));
        List<LibraryTabManager.Discovered> scan = [Card("777", "Retro")];

        LibraryTabManager.MergeDiscovery(config, scan);
        LibraryTabManager.MergeDiscovery(config, scan);

        Assert.Equal("Retro", NameOf(config, "777"));
        Assert.Single(config.CardLibraries);
    }
}
