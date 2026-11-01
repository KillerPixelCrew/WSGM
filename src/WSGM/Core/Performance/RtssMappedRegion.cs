using System.IO.MemoryMappedFiles;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     The <c>RTSSSharedMemoryV2</c> mapping's identity and its view as a region, shared by the
///     frametime reader and the OSD writer.
/// </summary>
/// <param name="view">
///     The mapped view. The frametime reader passes a read-only view and never calls the write
///     members.
/// </param>
internal sealed class RtssMappedRegion(MemoryMappedViewAccessor view) : IRtssOsdRegion
{
    /// <summary>The name RTSS publishes its shared memory under.</summary>
    internal const string MapName = "RTSSSharedMemoryV2";

    /// <summary>The header's <c>dwSignature</c> value.</summary>
    /// <remarks>
    ///     The server writes dwSignature = 'RTSS' as a C multichar constant, so the DWORD VALUE is
    ///     0x52545353 and the bytes in memory read "SSTR". The byte-order-mirrored 0x53535452 shipped
    ///     first and made the reader refuse every real mapping silently. Live-verified on RTSS 2.21
    ///     (2026-09-01, OSD work).
    /// </remarks>
    internal const uint Signature = 0x52545353;

    /// <summary>Byte offset of the header's <c>dwVersion</c>, right after the signature.</summary>
    internal const int HeaderVersionOffset = 4;

    /// <inheritdoc />
    public long Capacity => view.Capacity;

    /// <inheritdoc />
    public uint ReadUInt32(long offset)
    {
        return view.ReadUInt32(offset);
    }

    /// <inheritdoc />
    public void WriteUInt32(long offset, uint value)
    {
        view.Write(offset, value);
    }

    /// <inheritdoc />
    public void ReadBytes(long offset, byte[] buffer, int count)
    {
        view.ReadArray(offset, buffer, 0, count);
    }

    /// <inheritdoc />
    public void WriteBytes(long offset, byte[] buffer, int count)
    {
        view.WriteArray(offset, buffer, 0, count);
    }

    /// <inheritdoc />
    public unsafe bool TryAcquireBusy(long offset)
    {
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            return Interlocked.CompareExchange(ref *(int*)(pointer + offset), 1, 0) == 0;
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    /// <inheritdoc />
    public unsafe void ReleaseBusy(long offset)
    {
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            Volatile.Write(ref *(int*)(pointer + offset), 0);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }
}
