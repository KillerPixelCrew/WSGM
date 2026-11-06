using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed partial class SdFormatTests
{
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
        var detail = SdFormatManager.DescribeTarget(new FormatTarget(
            "id", 3, "SanDisk", 256_000_000_000L, WindowsStorage.BusTypeSd, ['E'],
            true));

        Assert.Contains("256 GB", detail);
        Assert.Contains("SD card", detail);
        Assert.Contains("E:", detail);
        Assert.Contains("Steam Deck card", detail);
    }

    [Fact]
    public void ALetterlessDeckCardStillDescribesCleanly()
    {
        var detail = SdFormatManager.DescribeTarget(new FormatTarget(
            "id", 3, "Generic", 512_000_000_000L, WindowsStorage.BusTypeUsb, [],
            false));

        Assert.Equal("512 GB — USB", detail);
    }

    // ---- re-verification before every destructive diskpart run ----

    // The run tests exercise the placement of these checks with fake native operations.

    [Fact]
    public void CompareIdentity_SameCapacityAndBus_ReturnsSame()
    {
        Assert.Equal(
            FormatTargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, WindowsStorage.BusTypeSd,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_BusTypeQueryFailed_ReturnsSame()
        // -1 is TryGetDeviceDescriptor's failure sentinel, not a different bus.
    {
        Assert.Equal(
            FormatTargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, -1,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_BusTypeUnknownAtPickTime_ReturnsSame()
        // The sentinel tolerance is symmetric: a reader that did not answer at
        // enumeration records -1 in the baseline too, and a fact we never had cannot
        // contradict one we just read. Without this, an intermittent
        // IOCTL_STORAGE_QUERY_PROPERTY aborts a healthy format after `clean`.
    {
        Assert.Equal(
            FormatTargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, WindowsStorage.BusTypeSd,
                256_000_000_000L, -1));
    }

    [Fact]
    public void CompareIdentity_SizeUnknownAtPickTime_ReturnsSame()
    {
        Assert.Equal(
            FormatTargetIdentity.Same,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, WindowsStorage.BusTypeSd,
                0L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_BothSizesKnownAndDifferent_StillReturnsChanged()
        // The tolerance must not swallow a real swap: two known, differing capacities
        // remain the one discriminator a card reader actually gives us.
    {
        Assert.Equal(
            FormatTargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, true,
                128_000_000_000L, WindowsStorage.BusTypeSd,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_DiskHandleCouldNotBeOpened_ReturnsUnreadable()
    {
        Assert.Equal(
            FormatTargetIdentity.Unreadable,
            SdFormatManager.CompareIdentity(
                false, false, false,
                0L, -1,
                256_000_000_000L, WindowsStorage.BusTypeSd));
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
            256_000_000_000L, WindowsStorage.BusTypeSd);

        Assert.Equal(FormatTargetIdentity.Unreadable, identity);
    }

    [Fact]
    public void CompareIdentity_NegativeSize_ReturnsUnreadable()
    {
        Assert.Equal(
            FormatTargetIdentity.Unreadable,
            SdFormatManager.CompareIdentity(
                true, false, true,
                -1L, WindowsStorage.BusTypeSd,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_DifferentCapacity_ReturnsChanged()
    {
        Assert.Equal(
            FormatTargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, true,
                512_000_000_000L, WindowsStorage.BusTypeSd,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_DifferentBusType_ReturnsChanged()
    {
        Assert.Equal(
            FormatTargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, true,
                256_000_000_000L, WindowsStorage.BusTypeUsb,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_NoLongerRemovableMedia_ReturnsChanged()
    {
        Assert.Equal(
            FormatTargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, false, false,
                256_000_000_000L, WindowsStorage.BusTypeSd,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_SystemDisk_ReturnsChanged()
    {
        Assert.Equal(
            FormatTargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                true, true, true,
                256_000_000_000L, WindowsStorage.BusTypeSd,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    [Fact]
    public void CompareIdentity_SystemDiskThatCannotBeRead_ReturnsChanged()
        // Ordering: the system-disk check runs first and unconditionally, so it wins
        // over the unreadable case rather than being masked by it.
    {
        Assert.Equal(
            FormatTargetIdentity.Changed,
            SdFormatManager.CompareIdentity(
                false, true, false,
                0L, -1,
                256_000_000_000L, WindowsStorage.BusTypeSd));
    }

    // ---- native struct parsing ----

}
