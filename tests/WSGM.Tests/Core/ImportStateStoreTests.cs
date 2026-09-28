using WSGM.Core;
using WSGM.Device.Tests;

namespace WSGM.Tests.Core;

/// <summary>
///     The import records on disk. The file is user-reachable, so the load path's job is to make a
///     hand-edited or corrupted one harmless rather than to trust it.
/// </summary>
public sealed class ImportStateStoreTests
{
    [Fact]
    public void ARecordWithANullStringIsDroppedRatherThanThrowing()
    {
        // Dereferencing one threw out of the scan, and out of every scan after it, so the importer
        // stayed unusable until the user found and deleted the file themselves.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, """
                                {"Entries":[
                                  {"Source":"xbox","Key":"A_x!App","Name":null,"Target":null,
                                   "LaunchOptions":null,"Mode":"ControllerOnly"},
                                  {"Source":"xbox","Key":"B_y!App","Name":"Kept","Target":"t",
                                   "LaunchOptions":"o","Mode":"ControllerOnly"}
                                ]}
                                """);

        var entries = new ImportStateStore(path).Entries();

        Assert.Equal("B_y!App", Assert.Single(entries).Key);
    }

    [Fact]
    public void AnOversizedOrUnknownRecordIsDropped()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, $$"""
                                  {"Entries":[
                                    {"Source":"xbox","Key":"{{new string('k', 600)}}","Name":"Long",
                                     "Target":"t","LaunchOptions":"o","Mode":"ControllerOnly"},
                                    {"Source":"xbox","Key":"C_z!App","Name":"Mode","Target":"t",
                                     "LaunchOptions":"o","Mode":"NotAMode"}
                                  ]}
                                  """);

        Assert.Empty(new ImportStateStore(path).Entries());
    }

    [Fact]
    public void AFileThatIsNotJsonIsSetAsideRatherThanWrittenOver()
    {
        // The only memory of which shortcuts are WSGM's: kept for recovery, never overwritten.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, "not json at all");

        Assert.Empty(new ImportStateStore(path).Entries());
        var aside = Assert.Single(Directory.GetFiles(temporary.Root, "library-import.json.corrupt-*"));
        Assert.Equal("not json at all", File.ReadAllText(aside));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileThatCannotBeOpenedFailsWithoutBeingReplaced()
    {
        // Antivirus holding the file at startup must not cost every record: the read fails, nothing
        // is cached, and the next read finds the file intact.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        new ImportStateStore(path).SaveChoice(new ImportChoice { Source = "xbox", Key = "A_x!App", Excluded = true });
        ImportStateStore store = new(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<ImportStateException>(() => store.Entries());
        }

        Assert.Equal("A_x!App", Assert.Single(store.Choices()).Key);
    }

    [Fact]
    public void AFileFromANewerWsgmIsLeftAlone()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, """{"Version":99,"Entries":[],"Choices":[]}""");

        Assert.Throws<ImportStateException>(() => new ImportStateStore(path).Entries());
        Assert.Contains("99", File.ReadAllText(path));
    }

    [Fact]
    public void PastTheBoundTheNewestRecordsAreKept()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        var rows = string.Join(",", Enumerable.Range(0, 1030).Select(index =>
            $$"""{"Source":"epic","Key":"k{{index}}","Name":"n","Target":"t","LaunchOptions":"o","Mode":"ControllerOnly"}"""));
        File.WriteAllText(path, $$"""{"Entries":[{{rows}}]}""");

        var entries = new ImportStateStore(path).Entries();

        Assert.Equal(1024, entries.Count);
        Assert.Equal("k1029", entries[^1].Key);
        Assert.DoesNotContain(entries, entry => entry.Key == "k0");
    }

    [Fact]
    public void SavingAnAppliedTitleSettlesItsChoiceButKeepsAFixedMatch()
    {
        // The record now says what Steam has; the match is the one thing Steam keeps no trace of.
        using TemporaryDirectory temporary = new();
        ImportStateStore store = new(temporary.GetPath("library-import.json"));
        store.SaveChoice(new ImportChoice
        {
            Source = "epic", Key = "Moonlit", Route = "direct", MatchProvider = "steamgriddb", MatchId = "7",
            MatchName = "Moonlit", Artwork = [new ArtworkPick { Asset = ArtworkAsset.Grid, Url = "https://x/a.png" }]
        });

        store.SaveApplied(new ImportedEntry
        {
            Source = "epic", Key = "Moonlit", AppId = 9, Name = "Moonlit", Target = "t", LaunchOptions = "o",
            Mode = nameof(ImportMode.ControllerOnly), Route = "direct"
        });

        var choice = Assert.Single(store.Choices());
        Assert.Equal(("7", "", 0), (choice.MatchId, choice.Route, choice.Artwork.Count));
        Assert.Equal(9u, Assert.Single(store.Entries()).AppId);
    }

    [Fact]
    public void SavingAnAppliedTitleWithNoFixedMatchDropsItsChoice()
    {
        using TemporaryDirectory temporary = new();
        ImportStateStore store = new(temporary.GetPath("library-import.json"));
        store.SaveChoice(new ImportChoice { Source = "epic", Key = "Moonlit", Route = "direct" });

        store.SaveApplied(new ImportedEntry
        {
            Source = "epic", Key = "Moonlit", AppId = 9, Name = "Moonlit", Target = "t", LaunchOptions = "o",
            Mode = nameof(ImportMode.ControllerOnly), Route = "direct"
        });

        Assert.Empty(store.Choices());
    }

    [Fact]
    public void ForgettingATitleDropsItsRecordAndItsChoiceTogether()
    {
        using TemporaryDirectory temporary = new();
        ImportStateStore store = new(temporary.GetPath("library-import.json"));
        store.Save(new ImportedEntry
        {
            Source = "epic", Key = "Moonlit", AppId = 9, Name = "Moonlit", Target = "t", LaunchOptions = "o",
            Mode = nameof(ImportMode.ControllerOnly)
        });
        store.SaveChoice(new ImportChoice { Source = "epic", Key = "Moonlit", MatchId = "7" });

        store.Forget("EPIC", "moonlit");

        Assert.Empty(store.Entries());
        Assert.Empty(store.Choices());
    }

    [Fact]
    public void SeveralChoicesAreSavedInOneWrite()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        ImportStateStore store = new(path);

        store.SaveChoices(
        [
            new ImportChoice { Source = "epic", Key = "A", Excluded = true },
            new ImportChoice { Source = "epic", Key = "B", Excluded = true }
        ]);

        Assert.Equal(["A", "B"], new ImportStateStore(path).Choices().Select(choice => choice.Key));
    }

    [Fact]
    public void PruningDropsOnlyTheChoicesOfTitlesAFullyReadSourceNoLongerLists()
    {
        using TemporaryDirectory temporary = new();
        ImportStateStore store = new(temporary.GetPath("library-import.json"));
        store.SaveChoices(
        [
            new ImportChoice { Source = "epic", Key = "Gone", Excluded = true },
            new ImportChoice { Source = "epic", Key = "Listed", Excluded = true },
            new ImportChoice { Source = "gog", Key = "Unread", Excluded = true }
        ]);
        HashSet<(string Source, string Key)> listed = new(ImportPlan.Identity) { ("epic", "Listed") };

        store.PruneChoices(listed, source => source == "epic");

        Assert.Equal(["Listed", "Unread"], store.Choices().Select(choice => choice.Key).Order());
    }

    [Fact]
    public void ASavedRecordSurvivesAReload()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        new ImportStateStore(path).Save(new ImportedEntry
        {
            Source = "xbox", Key = "A_x!App", Name = "Moonlit", AppId = 42,
            Target = "t", LaunchOptions = "o", Mode = nameof(ImportMode.SteamIntegration)
        });

        var entry = Assert.Single(new ImportStateStore(path).Entries());

        Assert.Equal(42u, entry.AppId);
        Assert.Equal(nameof(ImportMode.SteamIntegration), entry.Mode);
    }

    [Fact]
    public void AChoiceSurvivesAReload()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        new ImportStateStore(path).SaveChoice(new ImportChoice
        {
            Source = "xbox", Key = "A_x!App", Mode = nameof(ImportMode.ControllerOnly), Excluded = true
        });

        var choice = Assert.Single(new ImportStateStore(path).Choices());

        Assert.Equal(ImportMode.ControllerOnly, choice.PickedMode());
        Assert.True(choice.Excluded);
    }

    [Fact]
    public void AChoiceThatDecidesNothingIsNotKept()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        ImportStateStore store = new(path);
        store.SaveChoice(new ImportChoice { Source = "xbox", Key = "A_x!App", Excluded = true });

        store.SaveChoice(new ImportChoice { Source = "xbox", Key = "A_x!App" });

        Assert.Empty(new ImportStateStore(path).Choices());
    }

    [Fact]
    public void AChoiceNamingNoKnownModeIsDroppedOnLoad()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, """
                                {"Entries":[],"Choices":[
                                  {"Source":"xbox","Key":"A_x!App","Mode":"Sideways"},
                                  {"Source":"xbox","Key":"B_y!App","Mode":null,"Excluded":true},
                                  {"Source":"xbox","Key":"C_z!App","Mode":"","Excluded":true}
                                ]}
                                """);

        Assert.Equal("C_z!App", Assert.Single(new ImportStateStore(path).Choices()).Key);
    }

    [Fact]
    public void ForgettingATitleLeavesTheOthersChoices()
    {
        using TemporaryDirectory temporary = new();
        ImportStateStore store = new(temporary.GetPath("library-import.json"));
        store.SaveChoice(new ImportChoice { Source = "xbox", Key = "A_x!App", Excluded = true });
        store.SaveChoice(new ImportChoice { Source = "xbox", Key = "B_y!App", Excluded = true });

        store.Forget("xbox", "A_x!App");

        Assert.Equal("B_y!App", Assert.Single(store.Choices()).Key);
    }

    [Fact]
    public void ANumericModeThatNamesNothingIsDroppedOnLoad()
    {
        // Numeric strings parse as an enum whether or not they name a value.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, """
                                {"Entries":[],"Choices":[{"Source":"xbox","Key":"A_x!App","Mode":"999"}]}
                                """);

        Assert.Empty(new ImportStateStore(path).Choices());
    }

    [Fact]
    public void ACollectionIsKeptPerGroupAndForgottenWithoutAnId()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        ImportStateStore store = new(path);

        store.SaveCollection(new ImportedCollection
            { Group = "epic", Id = "uc-1", Name = "Epic Games", AppIds = [5, 5, 0, 6] });
        store.SaveCollection(new ImportedCollection { Group = "EPIC", Id = "uc-2", Name = "Epic Games", AppIds = [7] });

        var kept = Assert.Single(new ImportStateStore(path).Collections());
        Assert.Equal("uc-2", kept.Id);
        Assert.Equal([7u], kept.AppIds);

        store.SaveCollection(new ImportedCollection { Group = "epic", Id = "", Name = "Epic Games" });
        Assert.Empty(new ImportStateStore(path).Collections());
    }

    [Fact]
    public void ACollectionRecordIsBoundedLikeEveryOther()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("library-import.json");
        File.WriteAllText(path, """
                                {"Collections":[
                                  {"Group":"xbox","Id":null,"Name":"Xbox","AppIds":[1]},
                                  {"Group":"epic","Id":"uc-1","Name":"Epic Games","AppIds":[0,3,3]}
                                ]}
                                """);

        var kept = Assert.Single(new ImportStateStore(path).Collections());

        Assert.Equal("epic", kept.Group);
        Assert.Equal([3u], kept.AppIds);
    }
}
