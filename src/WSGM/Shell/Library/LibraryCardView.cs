using System;
using System.Collections.Generic;
using System.Globalization;

namespace WSGM.Shell;

/// <summary>One tracked card as the SD Card Manager shows it, on the overlay and in Steam.</summary>
/// <param name="ContentId">The card's stable identity (its library content id).</param>
/// <param name="Name">The display name, from the card's own marker.</param>
/// <param name="Enabled">Whether a Steam library tab is maintained for it.</param>
/// <param name="Hidden">Whether it is kept out of tab creation while still tracked.</param>
/// <param name="Inserted">Whether the card is mounted right now.</param>
/// <param name="GameCount">How many games the card holds, remembered while it is ejected.</param>
/// <param name="AppIds">The remembered installed app ids.</param>
/// <param name="CapacityBytes">The volume's size from its last mount, 0 when never read.</param>
/// <param name="FreeBytes">The free space from that same reading.</param>
internal sealed record LibraryCardView(
    string ContentId,
    string Name,
    bool Enabled,
    bool Hidden,
    bool Inserted,
    int GameCount,
    IReadOnlyList<long> AppIds,
    long CapacityBytes,
    long FreeBytes)
{
    /// <summary>Whether the card ever reported its size.</summary>
    public bool HasCapacity => CapacityBytes > 0;

    /// <summary>The used space from the last reading, never negative.</summary>
    public long UsedBytes => HasCapacity ? Math.Max(0, CapacityBytes - FreeBytes) : 0;

    /// <summary>Formats a byte count the way both managers print capacity.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>Gigabytes with one decimal, or megabytes below one gigabyte.</returns>
    internal static string FormatBytes(long bytes)
    {
        const double gigabyte = 1024d * 1024 * 1024;
        return bytes >= gigabyte
            ? (bytes / gigabyte).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024d * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }
}
