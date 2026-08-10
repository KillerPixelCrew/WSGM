using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SdFormatTests
{
    // ---- diskpart script ----

    [Fact]
    public void DiskpartScriptTargetsTheDiskWithGameLibraryTuning()
    {
        var script = SdFormatManager.BuildDiskpartScript(3);

        Assert.Equal(
            "select disk 3\r\n"
            + "clean\r\n"
            + "create partition primary\r\n"
            + "format fs=ntfs quick unit=128k label=Games\r\n"
            + "assign\r\n",
            script);
    }

    [Fact]
    public void DiskpartScriptNeverIssuesCleanAll()
        => Assert.DoesNotContain("clean all", SdFormatManager.BuildDiskpartScript(0));

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
    public void SpliceRefusesWhenThePathIsAlreadyRegistered()
    {
        var ok = SteamLibraryVdf.TrySplice(
            TwoEntryConfig, @"D:\SteamLibrary", "999", 1L, out var updated);

        Assert.False(ok);
        Assert.Null(updated);
    }

    [Fact]
    public void RegisteredPathCheckIsCaseInsensitive()
    {
        Assert.True(SteamLibraryVdf.IsRegistered(TwoEntryConfig, @"d:\steamlibrary"));
        Assert.False(SteamLibraryVdf.IsRegistered(TwoEntryConfig, @"E:\SteamLibrary"));
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
