using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One ejectable device as the background snapshot reports it.</summary>
/// <param name="Id">The row identity (device instance path, or "media:X").</param>
/// <param name="Name">The device display name.</param>
/// <param name="Letters">The volume letters, formatted.</param>
/// <param name="SizeBytes">Total capacity across the listed volumes.</param>
/// <param name="Kind">Which eject path applies.</param>
/// <param name="DevInst">The disk devnode (PnP eject rows).</param>
/// <param name="VolumeLetter">The letter to lock (media rows).</param>
internal sealed record EjectableDevice(
    string Id,
    string Name,
    string Letters,
    long SizeBytes,
    EjectKind Kind,
    uint DevInst,
    char VolumeLetter)
{
    /// <summary>Current disk interface path for media without a drive letter; empty when unavailable.</summary>
    internal string DiskPath { get; init; } = "";

    /// <summary>Drive letters included in this eject action, used by library reconciliation.</summary>
    internal IReadOnlyList<char> VolumeLetters { get; init; } = [];
}
