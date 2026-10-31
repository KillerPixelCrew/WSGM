using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One formattable disk as the background snapshot reports it.</summary>
/// <param name="Id">The row identity (device instance path or "disk:N").</param>
/// <param name="DiskNumber">The physical disk number.</param>
/// <param name="Name">Vendor/product identity.</param>
/// <param name="SizeBytes">Total disk size.</param>
/// <param name="BusType">The STORAGE_BUS_TYPE value.</param>
/// <param name="Letters">Currently mounted letters on this disk, if any.</param>
/// <param name="HasLinuxPartitions">Whether ext4-style partitions were found.</param>
internal sealed record FormatTarget(
    string Id,
    int DiskNumber,
    string Name,
    long SizeBytes,
    int BusType,
    IReadOnlyList<char> Letters,
    bool HasLinuxPartitions);
