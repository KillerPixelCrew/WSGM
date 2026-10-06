using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class SteamLibraryVdfTests
{
    // ---- config splice ----

    private const string TwoEntryConfig =
        "\"libraryfolders\"\n"
        + "{\n"
        + "\t\"0\"\n"
        + "\t{\n"
        + "\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n"
        + "\t\t\"contentid\"\t\t\"111\"\n"
        + "\t\t\"apps\"\n"
        + "\t\t{\n"
        + "\t\t\t\"2810\"\t\t\"4586967312\"\n"
        + "\t\t}\n"
        + "\t}\n"
        + "\t\"1\"\n"
        + "\t{\n"
        + "\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n"
        + "\t\t\"contentid\"\t\t\"222\"\n"
        + "\t\t\"apps\"\n"
        + "\t\t{\n"
        + "\t\t}\n"
        + "\t}\n"
        + "}\n";

    [Fact]
    public void ParsingAMarkerPairsTheContentIdWithItsOwnLabel()
    {
        Assert.True(SteamLibraryVdf.TryParseMarker(
            SteamLibraryVdf.BuildMarker("222", @"C:\Steam\steam.exe", "Named card"),
            out var id, out var label));

        Assert.Equal("222", id);
        Assert.Equal("Named card", label);
    }

    [Fact]
    public void ParsingMultipleContentIdBlocksDoesNotBorrowAnotherBlocksLabel()
    {
        Assert.True(SteamLibraryVdf.TrySetLabel(TwoEntryConfig, "111", "Right label", out var first));
        Assert.True(SteamLibraryVdf.TrySetLabel(first!, "222", "Other label", out var text));

        Assert.True(SteamLibraryVdf.TryParseMarker(text!, out var id, out var label));

        Assert.Equal("111", id);
        Assert.Equal("Right label", label);
    }
    // ---- content id ----

    [Fact]
    public void GeneratedContentIdsArePositiveInt64AndAvoidCollisions()
    {
        var taken = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var id = SteamLibraryVdf.GenerateContentId(taken);
            Assert.True(long.TryParse(id, out var value) && value > 0);
            Assert.True(taken.Add(id), "generated a duplicate content id");
        }
    }

    // ---- marker VDF ----

    [Fact]
    public void MarkerMatchesSteamsExactDialect()
    {
        var marker = SteamLibraryVdf.BuildMarker(
            "5167503016717445825", @"C:\Program Files (x86)\Steam\steam.exe");

        Assert.Equal(
            "\"libraryfolder\"\n"
            + "{\n"
            + "\t\"contentid\"\t\t\"5167503016717445825\"\n"
            + "\t\"label\"\t\t\"\"\n"
            + "\t\"launcher\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\\\\steam.exe\"\n"
            + "}\n",
            marker);
        Assert.DoesNotContain("\r", marker);
    }

    [Fact]
    public void ReadingAMarkerReturnsBothItsIdentityAndItsName()
    {
        // The card's own name, which is what discovery follows: Steam's config label
        // belongs to a path registration and survives a card swap.
        using var temp = new TemporaryDirectory();
        var library = Directory.CreateDirectory(temp.GetPath("SteamLibrary")).FullName;
        File.WriteAllText(
            Path.Combine(library, "libraryfolder.vdf"),
            SteamLibraryVdf.BuildMarker("222", @"C:\Steam\steam.exe", "SDCard10"));

        Assert.True(SteamLibraryMarker.TryRead(library, out var contentId, out var label));
        Assert.Equal("222", contentId);
        Assert.Equal("SDCard10", label);
    }

    [Fact]
    public void ReadingAnUnlabelledMarkerReportsAnEmptyName()
    {
        using var temp = new TemporaryDirectory();
        var library = Directory.CreateDirectory(temp.GetPath("SteamLibrary")).FullName;
        File.WriteAllText(
            Path.Combine(library, "libraryfolder.vdf"),
            SteamLibraryVdf.BuildMarker("222", @"C:\Steam\steam.exe"));

        Assert.True(SteamLibraryMarker.TryRead(library, out var contentId, out var label));
        Assert.Equal("222", contentId);
        Assert.Equal("", label);
    }

    [Fact]
    public void ReadingAMissingMarkerFailsWithoutThrowing()
    {
        using var temp = new TemporaryDirectory();

        Assert.False(SteamLibraryMarker.TryRead(temp.Root, out var contentId, out var label));
        Assert.Null(contentId);
        Assert.Equal("", label);
    }

    // ---- label rewrite (card rename while Steam is closed) ----

    [Fact]
    public void SetLabelRewritesTheMarkersLabelLineOnly()
    {
        var marker = SteamLibraryVdf.BuildMarker(
            "5167503016717445825", @"C:\Program Files (x86)\Steam\steam.exe", "Old");

        Assert.True(SteamLibraryVdf.TrySetLabel(marker, "5167503016717445825", "Red Card", out var updated));
        Assert.Equal(marker.Replace("\t\"label\"\t\t\"Old\"", "\t\"label\"\t\t\"Red Card\""), updated);
        Assert.DoesNotContain("\r", updated);
    }

    [Fact]
    public void SetLabelTargetsOnlyTheMatchingConfigBlock()
    {
        const string config =
            "\"libraryfolders\"\n"
            + "{\n"
            + "\t\"0\"\n"
            + "\t{\n"
            + "\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n"
            + "\t\t\"label\"\t\t\"CardA\"\n"
            + "\t\t\"contentid\"\t\t\"111\"\n"
            + "\t}\n"
            + "\t\"1\"\n"
            + "\t{\n"
            + "\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n"
            + "\t\t\"label\"\t\t\"CardB\"\n"
            + "\t\t\"contentid\"\t\t\"222\"\n"
            + "\t}\n"
            + "}\n";

        Assert.True(SteamLibraryVdf.TrySetLabel(config, "222", "Blue", out var updated));
        Assert.Contains("\t\t\"label\"\t\t\"CardA\"", updated);
        Assert.Contains("\t\t\"label\"\t\t\"Blue\"", updated);
        Assert.DoesNotContain("CardB", updated);
    }

    [Fact]
    public void SetLabelInsertsALabelLineWhenTheBlockHasNone()
    {
        Assert.True(SteamLibraryVdf.TrySetLabel(TwoEntryConfig, "222", "Named", out var updated));
        Assert.Contains(
            "\t\t\"contentid\"\t\t\"222\"\n\t\t\"label\"\t\t\"Named\"\n", updated);
        // The other block stays byte-identical (no label appears in it).
        Assert.Contains("\t\t\"contentid\"\t\t\"111\"\n\t\t\"apps\"\n", updated);
    }

    [Fact]
    public void SetLabelEscapesQuotesAndRefusesUnknownIds()
    {
        var marker = SteamLibraryVdf.BuildMarker("111", @"C:\Steam\steam.exe");
        Assert.True(SteamLibraryVdf.TrySetLabel(marker, "111", "My \"Fast\" Card", out var updated));
        Assert.Contains("\"label\"\t\t\"My \\\"Fast\\\" Card\"", updated);

        Assert.False(SteamLibraryVdf.TrySetLabel(marker, "999", "X", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void NextIndexIsHighestExistingPlusOne()
    {
        Assert.Equal(2, SteamLibraryVdf.NextIndex(TwoEntryConfig));
    }

    [Fact]
    public void SpliceAppendsBeforeTheFinalBraceAndPreservesExistingBytes()
    {
        var ok = SteamLibraryVdf.TrySplice(
            TwoEntryConfig, @"E:\SteamLibrary", "333", 255_969_853_440L, out var updated);

        Assert.True(ok);
        Assert.NotNull(updated);
        // Everything before the inserted block is unchanged.
        Assert.StartsWith(TwoEntryConfig[..^2], updated); // up to the final "}\n"
        Assert.EndsWith("}\n", updated);
        Assert.Contains("\t\"2\"\n", updated);
        Assert.Contains("\"path\"\t\t\"E:\\\\SteamLibrary\"", updated);
        Assert.Contains("\"contentid\"\t\t\"333\"", updated);
        Assert.Contains("\"totalsize\"\t\t\"255969853440\"", updated);
        // The pre-existing apps map is untouched.
        Assert.Contains("\"2810\"\t\t\"4586967312\"", updated);
        Assert.DoesNotContain("\r", updated);
    }

    [Fact]
    public void SpliceRefusesWhenTheContentIdIsAlreadyRegistered()
    {
        var ok = SteamLibraryVdf.TrySplice(
            TwoEntryConfig, @"E:\SteamLibrary", "222", 1L, out var updated);

        Assert.False(ok);
        Assert.Null(updated);
    }

    [Fact]
    public void SpliceAllowsANewCardAtAnAlreadyRegisteredPath()
    {
        // A card reader keeps its letter across swaps: the same path with a fresh
        // content id is a new card and MUST be added.
        var ok = SteamLibraryVdf.TrySplice(
            TwoEntryConfig, @"D:\SteamLibrary", "777", 1L, out var updated);

        Assert.True(ok);
        Assert.NotNull(updated);
        Assert.Contains("\"contentid\"\t\t\"777\"", updated);
        // The prior D:\SteamLibrary entry (content id 222) is preserved.
        Assert.Contains("\"contentid\"\t\t\"222\"", updated);
    }

    [Fact]
    public void ContentIdRegistrationCheckIsExact()
    {
        Assert.True(SteamLibraryVdf.IsContentIdRegistered(TwoEntryConfig, "222"));
        Assert.False(SteamLibraryVdf.IsContentIdRegistered(TwoEntryConfig, "999"));
    }

    [Fact]
    public void RegisteredPathIsResolvedByContentIdNotTheReusedDriveLetter()
    {
        Assert.Equal(@"D:\SteamLibrary", SteamLibraryVdf.PathForContentId(TwoEntryConfig, "222"));
        Assert.Null(SteamLibraryVdf.PathForContentId(TwoEntryConfig, "999"));
    }

    [Fact]
    public void RemovingByContentIdKeepsTheOtherRegistrationByteForByte()
    {
        var removed = SteamLibraryVdf.TryRemoveContentId(TwoEntryConfig, "222", out var updated);

        Assert.True(removed);
        Assert.NotNull(updated);
        Assert.Contains("\"contentid\"\t\t\"111\"", updated);
        Assert.DoesNotContain("\"contentid\"\t\t\"222\"", updated);
        Assert.EndsWith("}\n", updated);
    }

    [Fact]
    public void RemovingByPathDropsAPreviousCardsRegistrationAtTheSameReaderLetter()
    {
        // The reported bug: the reader keeps its letter, so the card that was
        // pulled out left a registration behind under ITS OWN content id, which
        // content-id dedup cannot see.
        var removed = SteamLibraryVdf.TryRemovePath(TwoEntryConfig, @"D:\SteamLibrary",
            out var updated);

        Assert.Equal(1, removed);
        Assert.NotNull(updated);
        Assert.DoesNotContain("\"contentid\"\t\t\"222\"", updated);
        Assert.Contains("\"contentid\"\t\t\"111\"", updated);
    }

    [Fact]
    public void RemovingByPathDropsEveryDuplicateAtThatPathNotJustTheFirst()
    {
        // Steam happily holds several registrations at one path (live-verified),
        // so a single-match removal would leave a phantom behind.
        var doubled = SteamLibraryVdf.TrySplice(
            TwoEntryConfig, @"D:\SteamLibrary", "333", 1L, out var withDuplicate)
            ? withDuplicate!
            : throw new InvalidOperationException("splice failed");

        var removed = SteamLibraryVdf.TryRemovePath(doubled, @"D:\SteamLibrary", out var updated);

        Assert.Equal(2, removed);
        Assert.NotNull(updated);
        Assert.DoesNotContain(@"D:\\SteamLibrary", updated);
        Assert.Contains("\"contentid\"\t\t\"111\"", updated);
        Assert.Contains("\t\"0\"\n", updated);
        Assert.DoesNotContain("\t\"1\"\n", updated);
    }

    [Theory]
    [InlineData(@"d:\steamlibrary")]
    [InlineData(@"D:\SteamLibrary\")]
    [InlineData("D:/SteamLibrary")]
    public void RemovingByPathIgnoresCaseTrailingSeparatorsAndSlashDirection(string path)
    {
        Assert.Equal(1, SteamLibraryVdf.TryRemovePath(TwoEntryConfig, path, out var updated));
        Assert.NotNull(updated);
    }

    [Fact]
    public void RemovingByPathLeavesTheConfigUntouchedWhenNothingMatches()
    {
        Assert.Equal(0, SteamLibraryVdf.TryRemovePath(TwoEntryConfig, @"E:\SteamLibrary",
            out var updated));
        Assert.Null(updated);
    }

    [Fact]
    public void RemovingFirstContentIdRenumbersRemainingEntries()
    {
        var removed = SteamLibraryVdf.TryRemoveContentId(TwoEntryConfig, "111", out var updated);

        Assert.True(removed);
        Assert.NotNull(updated);
        Assert.Contains("\t\"0\"\n", updated);
        Assert.DoesNotContain("\t\"1\"\n", updated);
        Assert.Contains("\"contentid\"\t\t\"222\"", updated);
    }

    [Fact]
    public void LabelLookupStaysPairedWithItsContentId()
    {
        var labeled = TwoEntryConfig
            .Replace("\t\t\"contentid\"\t\t\"111\"", "\t\t\"label\"\t\t\"Primary\"\n\t\t\"contentid\"\t\t\"111\"")
            .Replace("\t\t\"contentid\"\t\t\"222\"", "\t\t\"label\"\t\t\"Card\"\n\t\t\"contentid\"\t\t\"222\"");
        Assert.Equal("Primary", SteamLibraryVdf.LabelForContentId(labeled, "111"));
        Assert.Equal("Card", SteamLibraryVdf.LabelForContentId(labeled, "222"));
    }

    [Fact]
    public void SpliceRejectsAFileThatIsNotALibraryFoldersConfig()
    {
        var ok = SteamLibraryVdf.TrySplice(
            "\"something else\"\n{\n}\n", @"E:\SteamLibrary", "1", 1L, out var updated);

        Assert.False(ok);
        Assert.Null(updated);
    }

    [Fact]
    public void ContentIdsAreHarvestedForCollisionAvoidance()
    {
        var ids = SteamLibraryVdf.ValuesOf(TwoEntryConfig, "contentid");
        Assert.Equal(["111", "222"], ids);
    }
}
