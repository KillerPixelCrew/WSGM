using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One mounted disk volume as one inventory read saw it.</summary>
/// <param name="Letter">The drive letter.</param>
/// <param name="Disk">The physical disk number under the volume.</param>
/// <param name="DriveType">The .NET drive type (Fixed or Removable).</param>
/// <param name="Ready">Whether the media was ready when read.</param>
/// <param name="CapacityBytes">The volume size, 0 when the media is not ready.</param>
/// <param name="Label">The volume label for a ready volume on external storage, otherwise empty.</param>
internal sealed record StorageVolumeFacts(
    char Letter,
    int Disk,
    DriveType DriveType,
    bool Ready,
    long CapacityBytes,
    string Label)
{
    /// <summary>The volume's root, such as <c>D:\</c>.</summary>
    internal string MountPath => $@"{Letter}:\";
}

/// <summary>One external disk (hot-pluggable, or holding removable media) as one inventory read saw it.</summary>
/// <param name="Number">The physical disk number.</param>
/// <param name="Kind">How it is ejected safely.</param>
/// <param name="InterfacePath">Its disk interface path, empty when only a volume named it.</param>
/// <param name="DevInst">Its device node, 0 when it has none.</param>
/// <param name="InstanceId">Its device instance path, empty when it has none.</param>
/// <param name="Name">Its device display name, empty when it has none.</param>
/// <param name="CapacityBytes">The whole disk's capacity, 0 when it was not read.</param>
/// <param name="BusType">The STORAGE_BUS_TYPE, -1 when it was not read.</param>
/// <param name="Product">Vendor and product strings, possibly empty.</param>
/// <param name="HasLinuxPartitions">Whether a partition looks like a Linux filesystem.</param>
internal sealed record StorageDiskFacts(
    int Number,
    EjectKind Kind,
    string InterfacePath,
    uint DevInst,
    string InstanceId,
    string Name,
    long CapacityBytes,
    int BusType,
    string Product,
    bool HasLinuxPartitions);

/// <summary>
///     One read of the machine's storage: every mounted disk volume and every external disk, with the disk
///     number as the one identity both carry. The eject list, the format targets, Steam's storage pages and the
///     card and library scans all project from a read like this instead of walking the disks themselves, so
///     within one read they cannot disagree about which card is which.
/// </summary>
/// <remarks>
///     An immutable value, not a cache: whoever needs fresh facts reads again. The checks that guard a
///     destructive step against a swapped card keep their own fresh handles.
/// </remarks>
/// <param name="Volumes">Every mounted volume on a disk, external or not.</param>
/// <param name="ExternalDisks">
///     Every disk classified as external, in disk-interface order, never a disk Windows or WSGM runs from.
/// </param>
internal sealed record StorageInventory(
    IReadOnlyList<StorageVolumeFacts> Volumes,
    IReadOnlyList<StorageDiskFacts> ExternalDisks)
{
    /// <summary>
    ///     The disks Windows and WSGM run from, once resolved. A failed resolution is not kept, so the guard
    ///     is asked again on the next read rather than staying empty for the session.
    /// </summary>
    private static HashSet<int>? _systemDisks;

    /// <summary>The inventory before anything was read.</summary>
    internal static StorageInventory Empty { get; } = new([], []);

    /// <summary>The external disk with this number, or null when it is not one.</summary>
    /// <param name="disk">The physical disk number.</param>
    internal StorageDiskFacts? FindDisk(int disk)
    {
        return ExternalDisks.FirstOrDefault(candidate => candidate.Number == disk);
    }

    /// <summary>The ready volumes on external disks.</summary>
    internal IEnumerable<StorageVolumeFacts> ReadyExternalVolumes()
    {
        return Volumes.Where(volume => volume.Ready && FindDisk(volume.Disk) is not null);
    }

    /// <summary>
    ///     Reads the inventory: one mounted-volume walk, one disk-interface walk and one classification per
    ///     disk. Worker thread only: this opens volume and disk handles.
    /// </summary>
    /// <returns>What the machine has mounted and attached right now.</returns>
    internal static StorageInventory Read()
    {
        var systemDisks = SystemDisks();
        var mounted = NativeStorage.MountedVolumes()
            .Where(volume => volume is { DeviceType: NativeStorage.FileDeviceDisk, Disk: >= 0 })
            .ToList();

        // Physical interfaces exist even when Windows cannot mount any partition, so a blank or Linux
        // card is found here and not through a volume.
        HashSet<int> seen = [];
        List<StorageDiskFacts> disks = [];
        foreach (var path in NativeStorage.ListDiskInterfaces())
        {
            using var probe = NativeStorage.OpenVolumeForQueryPath(path);
            if (probe.IsInvalid || !NativeStorage.TryGetDeviceNumber(probe, out var type, out var disk)
                                || type != NativeStorage.FileDeviceDisk || disk < 0 || !seen.Add(disk))
            {
                continue;
            }

            if (RemovableDriveManager.ClassifyDisk(disk, systemDisks) is { } kind)
            {
                disks.Add(DescribeDisk(disk, kind, path, probe));
            }
        }

        foreach (var disk in mounted.Select(volume => volume.Disk).Distinct())
        {
            if (seen.Add(disk) && RemovableDriveManager.ClassifyDisk(disk, systemDisks) is { } kind)
            {
                disks.Add(new StorageDiskFacts(disk, kind, "", 0, "", "", 0, -1, "", false));
            }
        }

        List<StorageVolumeFacts> volumes = [];
        foreach (var volume in mounted)
        {
            var label = "";
            if (volume.Ready && disks.Any(known => known.Number == volume.Disk)
                             && NativeStorage.TryGetVolumeInformation(volume.Letter + @":\", out var read, out _))
            {
                label = read;
            }

            volumes.Add(new StorageVolumeFacts(
                volume.Letter, volume.Disk, volume.DriveType, volume.Ready, volume.SizeBytes, label));
        }

        return new StorageInventory(volumes, disks);
    }

    /// <summary>Reads the facts the eject list and the format flow show for one external disk.</summary>
    private static StorageDiskFacts DescribeDisk(int disk, EjectKind kind, string path, SafeFileHandle probe)
    {
        var capacity = NativeStorage.GetDiskCapacityForQuery(probe);
        NativeStorage.TryGetDeviceDescriptor(probe, out var busType, out var product);
        var linux = NativeStorage.TryGetPartitionTypes(probe, out var partitions)
                    && partitions.Any(partition => partition.IsLinux);
        if (!NativeStorage.TryGetDevNode(path, out var devInst))
        {
            return new StorageDiskFacts(disk, kind, path, 0, "", "", capacity, busType, product, linux);
        }

        return new StorageDiskFacts(disk, kind, path, devInst,
            NativeStorage.GetDeviceInstanceId(devInst),
            NativeStorage.GetDeviceDisplayName(devInst),
            capacity, busType, product, linux);
    }

    private static HashSet<int> SystemDisks()
    {
        if (Volatile.Read(ref _systemDisks) is { } known)
        {
            return known;
        }

        var resolved = RemovableDriveManager.ResolveSystemDisks();
        if (resolved.Count > 0)
        {
            Volatile.Write(ref _systemDisks, resolved);
        }

        return resolved;
    }
}
