namespace WSGM.Shell;

internal readonly record struct DiskIdentitySnapshot(
    bool SystemDisk,
    bool HandleOpened,
    bool Removable,
    long SizeBytes,
    int BusType);
