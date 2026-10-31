using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed partial class SdFormatTests
{
    // ---- diskpart script ----

    [Fact]
    public void PartitionScriptCleansAndCreatesOnePrimaryPartition()
    {
        Assert.Equal(
            "select disk 3\r\n"
            + "clean\r\n"
            + "create partition primary\r\n",
            SdFormatManager.BuildDiskpartPartitionScript(3));
    }

    [Fact]
    public void PartitionScriptNeverFormats()
    {
        var script = SdFormatManager.BuildDiskpartPartitionScript(3);

        Assert.DoesNotContain("format", script);
        Assert.DoesNotContain("assign", script);
    }

    [Fact]
    public void FormatScriptSelectsTheNewPartitionAndFormatsOnly()
    {
        Assert.Equal(
            "select disk 3\r\n"
            + "select partition 1\r\n"
            + "format fs=ntfs quick unit=128k label=\"Games\"\r\n",
            SdFormatManager.BuildDiskpartFormatScript(3));
    }

    [Fact]
    public void FormatScriptNeverCleansOrAssigns()
    {
        var script = SdFormatManager.BuildDiskpartFormatScript(3);

        Assert.DoesNotContain("clean", script);
        Assert.DoesNotContain("assign", script);
    }

    [Fact]
    public void DiskpartScriptQuotesTheGivenLabel()
    {
        Assert.Contains(
            "label=\"My Games\"\r\n",
            SdFormatManager.BuildDiskpartFormatScript(1, "My Games"));
    }

    [Fact]
    public void AssignScriptPreservesTheCardsDriveLetter()
    {
        Assert.Equal(
            "select disk 3\r\n"
            + "select partition 1\r\n"
            + "assign letter=E\r\n",
            SdFormatManager.BuildDiskpartAssignScript(3, 'E'));
    }

    [Fact]
    public void AssignScriptNeverCleansOrFormats()
    {
        var script = SdFormatManager.BuildDiskpartAssignScript(3, 'E');

        Assert.DoesNotContain("clean", script);
        Assert.DoesNotContain("format", script);
    }

    [Theory]
    [InlineData(null, "Games")]
    [InlineData("   ", "Games")]
    [InlineData("My Card", "My Card")]
    [InlineData("Games/2\"; exit", "Games2 exit")]
    [InlineData("0123456789012345678901234567890123456789", "01234567890123456789012345678901")]
    public void LabelsAreSanitizedForDiskpartAndSteam(string? input, string expected)
    {
        Assert.Equal(expected, SdFormatManager.SanitizeLabel(input));
    }

    [Fact]
    public void ALetterlessCardGetsABareAssign()
    {
        var script = SdFormatManager.BuildDiskpartAssignScript(3, '\0');

        Assert.EndsWith("assign\r\n", script);
        Assert.DoesNotContain("assign letter=", script);
    }

    [Fact]
    public void DiskpartScriptNeverIssuesCleanAll()
    {
        Assert.DoesNotContain("clean all", SdFormatManager.BuildDiskpartPartitionScript(0));
    }

    // ---- bus labelling ----

    [Theory]
    [InlineData(12, "SD card")]
    [InlineData(13, "SD card")]
    [InlineData(7, "USB")]
    [InlineData(1, "")]
    public void BusTypesAreLabelledForTheUser(int busType, string expected)
    {
        Assert.Equal(expected, SdFormatManager.DescribeBus(busType));
    }

    [Fact]
    public void TargetDetailShowsSizeBusLettersAndTheDeckHint()
    {
        var detail = SdFormatManager.DescribeTarget(new SdFormatManager.FormatTarget(
            "id", 3, "SanDisk", 256_000_000_000L, NativeStorage.BusTypeSd, ['E'],
            true));

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
            false));

        Assert.Equal("512 GB — USB", detail);
    }

    // ---- re-verification before every destructive diskpart run ----

    // The run tests exercise the placement of these checks with fake native operations.

    [Fact]
    public void CompareIdentity_SameCapacityAndBus_ReturnsSame()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, NativeStorage.BusTypeSd,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_BusTypeQueryFailed_ReturnsSame()
        // -1 is TryGetDeviceDescriptor's failure sentinel, not a different bus.
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, -1,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_BusTypeUnknownAtPickTime_ReturnsSame()
        // The sentinel tolerance is symmetric: a reader that did not answer at
        // enumeration records -1 in the baseline too, and a fact we never had cannot
        // contradict one we just read. Without this, an intermittent
        // IOCTL_STORAGE_QUERY_PROPERTY aborts a healthy format after `clean`.
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, NativeStorage.BusTypeSd,
                256_000_000_000L, -1));
    }

    [Fact]
    public void CompareIdentity_SizeUnknownAtPickTime_ReturnsSame()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, NativeStorage.BusTypeSd,
                0L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_BothSizesKnownAndDifferent_StillReturnsChanged()
        // The tolerance must not swallow a real swap: two known, differing capacities
        // remain the one discriminator a card reader actually gives us.
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, true,
                128_000_000_000L, NativeStorage.BusTypeSd,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_DiskHandleCouldNotBeOpened_ReturnsUnreadable()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Unreadable,
            SdFormatManager.CompareIdentity(
                false, false, false,
                0L, -1,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_OpenedButSizeQueryFailed_ReturnsUnreadable()
    {
        // The false-abort case that matters most: a reader whose media is not ready
        // answers the open but reports size 0 and classifies as non-removable. That
        // must NOT abort — the existing waits and retries are what rescue it.
        var identity = SdFormatManager.CompareIdentity(
            true, false, false,
            0L, -1,
            256_000_000_000L, NativeStorage.BusTypeSd);

        Assert.Equal(SdFormatManager.TargetIdentity.Unreadable, identity);
    }

    [Fact]
    public void CompareIdentity_NegativeSize_ReturnsUnreadable()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Unreadable,
            SdFormatManager.CompareIdentity(
                true, false, true,
                -1L, NativeStorage.BusTypeSd,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_DifferentCapacity_ReturnsChanged()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, true,
                512_000_000_000L, NativeStorage.BusTypeSd,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_DifferentBusType_ReturnsChanged()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, NativeStorage.BusTypeUsb,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_NoLongerRemovableMedia_ReturnsChanged()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, false,
                256_000_000_000L, NativeStorage.BusTypeSd,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_SystemDisk_ReturnsChanged()
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, true, true,
                256_000_000_000L, NativeStorage.BusTypeSd,
                256_000_000_000L, NativeStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_SystemDiskThatCannotBeRead_ReturnsChanged()
        // Ordering: the system-disk check runs first and unconditionally, so it wins
        // over the unreadable case rather than being masked by it.
    {
        Assert.Equal(
            SdFormatManager.TargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                false, true, false,
                0L, -1,
                256_000_000_000L, NativeStorage.BusTypeSd));
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
    {
        Assert.Equal(expected, SdFormatManager.ResolveLibraryRoot(picked));
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
        "SanDisk\0"u8.CopyTo(buffer.AsSpan(36));
        "Extreme\0"u8.CopyTo(buffer.AsSpan(44));

        var (busType, product) = NativeStorage.ReadDeviceDescriptor(buffer);

        Assert.Equal(NativeStorage.BusTypeSd, busType);
        Assert.Equal("SanDisk Extreme", product);
    }

    [Fact]
    public void GptLinuxPartitionIsRecognisedAsTheDeckHint()
    {
        var buffer = new byte[NativeStorage.DriveLayoutHeaderSize
                              + NativeStorage.PartitionRecordSize];
        BitConverter.GetBytes(1).CopyTo(buffer, 0); // PARTITION_STYLE_GPT
        BitConverter.GetBytes(1).CopyTo(buffer, 4); // one partition
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
        BitConverter.GetBytes(0).CopyTo(buffer, 0); // PARTITION_STYLE_MBR
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
                              + 4 * NativeStorage.PartitionRecordSize];
        BitConverter.GetBytes(0).CopyTo(buffer, 0);
        BitConverter.GetBytes(4).CopyTo(buffer, 4); // MBR always reports 4 slots
        buffer[NativeStorage.DriveLayoutHeaderSize + 32] = 0x07; // one NTFS slot

        var (_, partitions) = NativeStorage.ReadDriveLayout(buffer);

        Assert.Single(partitions);
        Assert.False(partitions[0].IsLinux);
    }
}
