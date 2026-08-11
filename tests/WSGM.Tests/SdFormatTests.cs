using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SdFormatTests
{
    // ---- diskpart script ----

    [Fact]
    public void DiskpartScriptPreservesTheCardsDriveLetter()
    {
        var script = SdFormatManager.BuildDiskpartScript(3, 'E');

        Assert.Equal(
            "select disk 3\r\n"
            + "clean\r\n"
            + "create partition primary\r\n"
            + "format fs=ntfs quick unit=128k label=\"Games\"\r\n"
            + "assign letter=E\r\n",
            script);
    }

    [Fact]
    public void DiskpartScriptQuotesTheGivenLabel()
        => Assert.Contains(
            "label=\"My Games\"\r\n",
            SdFormatManager.BuildDiskpartScript(1, 'E', "My Games"));

    [Theory]
    [InlineData(null, "Games")]
    [InlineData("   ", "Games")]
    [InlineData("My Card", "My Card")]
    [InlineData("Games/2\"; exit", "Games2 exit")]
    [InlineData("0123456789012345678901234567890123456789", "01234567890123456789012345678901")]
    public void LabelsAreSanitizedForDiskpartAndSteam(string? input, string expected)
        => Assert.Equal(expected, SdFormatManager.SanitizeLabel(input));

    [Fact]
    public void ALetterlessCardGetsABareAssign()
    {
        var script = SdFormatManager.BuildDiskpartScript(3, '\0');

        Assert.EndsWith("assign\r\n", script);
        Assert.DoesNotContain("assign letter=", script);
    }

    [Fact]
    public void DiskpartScriptNeverIssuesCleanAll()
        => Assert.DoesNotContain("clean all", SdFormatManager.BuildDiskpartScript(0, 'E'));

    // ---- bus labelling ----

    [Theory]
    [InlineData(12, "SD card")]
    [InlineData(13, "SD card")]
    [InlineData(7, "USB")]
    [InlineData(1, "")]
    public void BusTypesAreLabelledForTheUser(int busType, string expected)
        => Assert.Equal(expected, SdFormatManager.DescribeBus(busType));

    [Fact]
    public void TargetDetailShowsSizeBusLettersAndTheDeckHint()
    {
        var detail = SdFormatManager.DescribeTarget(new SdFormatManager.FormatTarget(
            "id", 3, "SanDisk", 256_000_000_000L, NativeStorage.BusTypeSd, ['E'],
            HasLinuxPartitions: true));

        Assert.Contains("256 GB", detail);
        Assert.Contains("SD card", detail);
        Assert.Contains("E:", detail);
        Assert.Contains("Steam Deck card", detail);
    }

    [Fact]
    public void ALetterlessDeckCardStillDescribesCleanly()
    {
        var detail = SdFormatManager.DescribeTarget(new SdFormatManager.FormatTarget(
            "id", 3, "Generic", 512_000_000_000L, NativeStorage.BusTypeUsb, [],
            HasLinuxPartitions: false));

        Assert.Equal("512 GB — USB", detail);
    }

    // ---- add-library path resolution ----

    [Theory]
    [InlineData("D:", @"D:\SteamLibrary")]
    [InlineData(@"D:\", @"D:\SteamLibrary")]
    [InlineData(@"D:\Games", @"D:\Games")]
    [InlineData(@"\\nas\media\steam", @"\\nas\media\steam")]
    [InlineData(@"E:\SteamLibrary\", @"E:\SteamLibrary")]
    public void DriveRootsGetTheSteamLibrarySubfolderOthersAreUsedAsIs(
        string picked, string expected)
        => Assert.Equal(expected, SdFormatManager.ResolveLibraryRoot(picked));

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
        var config =
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
    public void NextIndexIsHighestExistingPlusOne()
        => Assert.Equal(2, SteamLibraryVdf.NextIndex(TwoEntryConfig));

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

    // ---- native struct parsing ----

    [Fact]
    public void DeviceDescriptorDecodesBusTypeAndProductStrings()
    {
        // Header (36 bytes) + two ANSI strings past it.
        var buffer = new byte[64];
        // VendorIdOffset @12, ProductIdOffset @16, BusType @28.
        BitConverter.GetBytes(36).CopyTo(buffer, 12);
        BitConverter.GetBytes(44).CopyTo(buffer, 16);
        BitConverter.GetBytes(NativeStorage.BusTypeSd).CopyTo(buffer, 28);
        System.Text.Encoding.ASCII.GetBytes("SanDisk\0").CopyTo(buffer, 36);
        System.Text.Encoding.ASCII.GetBytes("Extreme\0").CopyTo(buffer, 44);

        var (busType, product) = NativeStorage.ReadDeviceDescriptor(buffer);

        Assert.Equal(NativeStorage.BusTypeSd, busType);
        Assert.Equal("SanDisk Extreme", product);
    }

    [Fact]
    public void GptLinuxPartitionIsRecognisedAsTheDeckHint()
    {
        var buffer = new byte[NativeStorage.DriveLayoutHeaderSize
            + NativeStorage.PartitionRecordSize];
        BitConverter.GetBytes(1).CopyTo(buffer, 0);  // PARTITION_STYLE_GPT
        BitConverter.GetBytes(1).CopyTo(buffer, 4);  // one partition
        // GPT PartitionType GUID lives at record offset 32.
        NativeStorage.LinuxFilesystemGuid.ToByteArray()
            .CopyTo(buffer, NativeStorage.DriveLayoutHeaderSize + 32);

        var (style, partitions) = NativeStorage.ReadDriveLayout(buffer);

        Assert.Equal(1, style);
        Assert.Single(partitions);
        Assert.True(partitions[0].IsLinux);
    }

    [Fact]
    public void MbrLinuxPartitionTypeByteIsRecognised()
    {
        var buffer = new byte[NativeStorage.DriveLayoutHeaderSize
            + NativeStorage.PartitionRecordSize];
        BitConverter.GetBytes(0).CopyTo(buffer, 0);  // PARTITION_STYLE_MBR
        BitConverter.GetBytes(1).CopyTo(buffer, 4);
        buffer[NativeStorage.DriveLayoutHeaderSize + 32] = 0x83; // Linux

        var (_, partitions) = NativeStorage.ReadDriveLayout(buffer);

        Assert.Single(partitions);
        Assert.True(partitions[0].IsLinux);
    }

    [Fact]
    public void EmptyMbrSlotsAreSkipped()
    {
        var buffer = new byte[NativeStorage.DriveLayoutHeaderSize
            + (4 * NativeStorage.PartitionRecordSize)];
        BitConverter.GetBytes(0).CopyTo(buffer, 0);
        BitConverter.GetBytes(4).CopyTo(buffer, 4); // MBR always reports 4 slots
        buffer[NativeStorage.DriveLayoutHeaderSize + 32] = 0x07; // one NTFS slot

        var (_, partitions) = NativeStorage.ReadDriveLayout(buffer);

        Assert.Single(partitions);
        Assert.False(partitions[0].IsLinux);
    }
}
