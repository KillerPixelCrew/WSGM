using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class UnletteredStorageTests
{
    [Fact]
    public void LinuxDiskWithoutMountedVolumesRemainsAnEjectCandidate()
    {
        List<(char Letter, int Disk, long Size)> volumes = [];
        RemovableDriveManager.AddUnletteredDisk(volumes, 3, 64_000_000_000);
        Assert.Equal(('\0', 3, 64_000_000_000L), Assert.Single(volumes));
    }

    [Fact]
    public void PhysicalDiscoveryDoesNotDuplicateMountedDisksOrEmptyReaders()
    {
        List<(char Letter, int Disk, long Size)> volumes = [('E', 2, 100)];
        RemovableDriveManager.AddUnletteredDisk(volumes, 2, 200);
        RemovableDriveManager.AddUnletteredDisk(volumes, 3, 0);
        RemovableDriveManager.AddUnletteredDisk(volumes, -1, 200);
        Assert.Equal(('E', 2, 100L), Assert.Single(volumes));
    }

    [Fact]
    public void GeometryCapacityUsesFullDiskSizeAndRejectsTruncatedOrNegativeRecords()
    {
        byte[] record = new byte[32];
        BitConverter.GetBytes(4_000_000_000_000L).CopyTo(record, 24);
        Assert.Equal(4_000_000_000_000L, NativeStorage.ReadGeometryCapacity(record));
        Assert.Equal(0, NativeStorage.ReadGeometryCapacity(record.AsSpan(0, 31)));
        BitConverter.GetBytes(-1L).CopyTo(record, 24);
        Assert.Equal(0, NativeStorage.ReadGeometryCapacity(record));
    }
}
