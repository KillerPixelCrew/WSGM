using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class RemovableDriveTests
{
    [Theory]
    [InlineData(true, true, EjectKind.UsbDevice)]
    [InlineData(true, false, EjectKind.UsbDevice)]
    [InlineData(false, true, EjectKind.Media)]
    public void HotplugFactsPickTheEjectPath(
        bool deviceHotplug, bool mediaRemovable, EjectKind expected)
        => Assert.Equal(expected, RemovableDriveManager.Classify(deviceHotplug, mediaRemovable));

    [Fact]
    public void InternalFixedDisksAreNeverListed()
        => Assert.Null(RemovableDriveManager.Classify(deviceHotplug: false, mediaRemovable: false));

    [Theory]
    [InlineData(0L, "")]
    [InlineData(-5L, "")]
    [InlineData(1L, "1 MB")]
    [InlineData(500_000_000L, "500 MB")]
    [InlineData(64_000_000_000L, "64 GB")]
    [InlineData(512_100_000_000L, "512.1 GB")]
    [InlineData(1_500_000_000_000L, "1.5 TB")]
    public void CapacitiesFormatInDecimalUnitsInvariantly(long bytes, string expected)
        => Assert.Equal(expected, RemovableDriveManager.FormatSize(bytes));

    [Fact]
    public void DriveLettersFormatWithColons()
    {
        Assert.Equal("E:", RemovableDriveManager.FormatLetters(['E']));
        Assert.Equal("E:, F:", RemovableDriveManager.FormatLetters(['E', 'F']));
    }

    [Fact]
    public void DeviceNumberRecordsDecodeTheFixedLayout()
    {
        var buffer = new byte[NativeStorage.DeviceNumberRecordSize];
        BitConverter.GetBytes(7).CopyTo(buffer, 0);
        BitConverter.GetBytes(2).CopyTo(buffer, 4);
        BitConverter.GetBytes(1).CopyTo(buffer, 8);

        var (deviceType, deviceNumber, partition) = NativeStorage.ReadDeviceNumber(buffer);

        Assert.Equal(NativeStorage.FileDeviceDisk, deviceType);
        Assert.Equal(2, deviceNumber);
        Assert.Equal(1, partition);
    }

    [Fact]
    public void HotplugRecordsDecodeTheFixedLayout()
    {
        var buffer = new byte[NativeStorage.HotplugRecordSize];
        buffer[4] = 1; // MediaRemovable
        buffer[6] = 0; // DeviceHotplug

        var (mediaRemovable, deviceHotplug) = NativeStorage.ReadHotplugInfo(buffer);

        Assert.True(mediaRemovable);
        Assert.False(deviceHotplug);
    }

    [Fact]
    public void OpenHandleVetoesBlameTheLikelyHolders()
    {
        NativeStorage.PnpVetoType[] openHandleVetoes =
        [
            NativeStorage.PnpVetoType.OutstandingOpen,
            NativeStorage.PnpVetoType.PendingClose,
            NativeStorage.PnpVetoType.WindowsApp,
        ];
        foreach (var veto in openHandleVetoes)
        {
            Assert.Contains("Still in use", RemovableDriveManager.DescribeVeto(veto, ""));
        }
    }

    [Fact]
    public void NamedVetoesNameTheBlocker()
    {
        Assert.Contains("steam.exe", RemovableDriveManager.DescribeVeto(
            NativeStorage.PnpVetoType.WindowsApp, "steam.exe"));
        Assert.Contains("StorSvc", RemovableDriveManager.DescribeVeto(
            NativeStorage.PnpVetoType.WindowsService, "StorSvc"));
    }

    [Fact]
    public void UnknownVetoesStillProduceAMessage()
        => Assert.False(string.IsNullOrWhiteSpace(RemovableDriveManager.DescribeVeto(
            NativeStorage.PnpVetoType.Device, "")));

    [Fact]
    public void StatusLinePrefersProgressThenOutcomeThenFacts()
    {
        var entry = new RemovableDriveEntry("id", EjectKind.UsbDevice)
        {
            Letters = "E:",
            SizeText = "512 GB",
        };
        Assert.Equal("E: — 512 GB", entry.StatusLine);

        entry.ResultText = "Safe to remove";
        Assert.Equal("Safe to remove", entry.StatusLine);

        entry.Busy = true;
        Assert.Equal("Ejecting...", entry.StatusLine);
    }

    [Fact]
    public void EjectActionDisablesWhileBusyOrAlreadyEjected()
    {
        var entry = new RemovableDriveEntry("id", EjectKind.Media);
        Assert.True(entry.ActionEnabled);

        entry.Busy = true;
        Assert.False(entry.ActionEnabled);

        entry.Busy = false;
        entry.Ejected = true;
        Assert.False(entry.ActionEnabled);
    }

    [Fact]
    public void RefreshesKeepSurvivingRowsAndUpdateThemInPlace()
    {
        var manager = new RemovableDriveManager();
        manager.Apply(
        [
            Device("stick", "Old name", "E:"),
            Device("gone", "Other stick", "F:"),
        ]);
        var survivor = manager.Drives[0];

        manager.Apply(
        [
            Device("stick", "New name", "E:"),
            Device("card", "SD Card", "G:"),
        ]);

        Assert.Equal(2, manager.Drives.Count);
        Assert.Same(survivor, manager.Drives[0]);
        Assert.Equal("New name", survivor.Name);
        Assert.Equal("SD Card", manager.Drives[1].Name);
        Assert.True(manager.HasDrives);
    }

    [Fact]
    public void RowsMidEjectSurviveASnapshotThatNoLongerListsThem()
    {
        var manager = new RemovableDriveManager();
        manager.Apply([Device("stick", "Stick", "E:")]);
        manager.Drives[0].Busy = true;

        manager.Apply([]);

        Assert.Single(manager.Drives);
        Assert.True(manager.HasDrives);
    }

    [Fact]
    public void AnEjectedDeviceListedAgainIsBackInService()
    {
        var manager = new RemovableDriveManager();
        manager.Apply([Device("stick", "Stick", "E:")]);
        var row = manager.Drives[0];
        row.Ejected = true;
        row.ResultText = "Safe to remove";

        manager.Apply([Device("stick", "Stick", "E:")]);

        Assert.False(row.Ejected);
        Assert.Equal("E: — 32 GB", row.StatusLine);
        Assert.True(row.ActionEnabled);
    }

    [Fact]
    public void AnEmptySnapshotClearsTheListAndTheTileVisibility()
    {
        var manager = new RemovableDriveManager();
        manager.Apply([Device("stick", "Stick", "E:")]);

        manager.Apply([]);

        Assert.Empty(manager.Drives);
        Assert.False(manager.HasDrives);
    }

    private static RemovableDriveManager.EjectableDevice Device(
        string id, string name, string letters)
        => new(id, name, letters, 32_000_000_000L, EjectKind.UsbDevice, 1, letters[0]);
}
