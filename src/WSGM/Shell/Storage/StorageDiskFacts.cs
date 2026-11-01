namespace WSGM.Shell;

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
