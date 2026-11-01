namespace WSGM.Core;

/// <summary>Write access to one RTSS on-screen-display slot, for the writer and tests.</summary>
/// <remarks>
///     Offsets are absolute within the <c>RTSSSharedMemoryV2</c> mapping. The busy flag is
///     separate from the plain writes because the real region takes it with an interlocked exchange
///     on the mapped page, which an accessor cannot express.
/// </remarks>
internal interface IRtssOsdRegion : IRtssRegion
{
    /// <summary>Writes one little-endian DWORD.</summary>
    /// <param name="offset">Byte offset.</param>
    /// <param name="value">The value.</param>
    void WriteUInt32(long offset, uint value);

    /// <summary>Writes <paramref name="count" /> bytes from <paramref name="buffer" />.</summary>
    /// <param name="offset">Byte offset.</param>
    /// <param name="buffer">Source.</param>
    /// <param name="count">Bytes to write.</param>
    void WriteBytes(long offset, byte[] buffer, int count);

    /// <summary>Attempts to take the v2.14+ OSD busy flag. False means skip this text write.</summary>
    /// <param name="offset">The busy DWORD's byte offset.</param>
    /// <returns>Whether the flag was taken.</returns>
    bool TryAcquireBusy(long offset);

    /// <summary>Releases the busy flag taken by <see cref="TryAcquireBusy" />.</summary>
    /// <param name="offset">The busy DWORD's byte offset.</param>
    void ReleaseBusy(long offset);
}
