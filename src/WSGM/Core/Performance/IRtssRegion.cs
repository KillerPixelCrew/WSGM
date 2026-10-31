namespace WSGM.Core;

/// <summary>Random access to the mapped RTSS region.</summary>
/// <remarks>
///     A seam over the memory-mapped view, so the layout below can be exercised against a synthetic
///     region. Without it the parsing is only reachable when RTSS happens to be running with a hooked
///     application, which is exactly when a test cannot rely on it.
/// </remarks>
internal interface IRtssRegion
{
    /// <summary>Bytes available in the mapped region.</summary>
    long Capacity { get; }

    /// <summary>Reads one little-endian unsigned 32-bit value.</summary>
    /// <param name="offset">Byte offset into the region.</param>
    /// <returns>The value.</returns>
    uint ReadUInt32(long offset);

    /// <summary>Reads a run of bytes.</summary>
    /// <param name="offset">Byte offset into the region.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="count">Number of bytes to read.</param>
    void ReadBytes(long offset, byte[] buffer, int count);
}
