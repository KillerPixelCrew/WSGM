using System;
using System.Text;

namespace WSGM.Core;

/// <summary>
///     The RTSS OSD slot protocol: claim by owner name, update text, release by zeroing. A C# port of
///     the slot semantics of RTSSSharedMemoryNET (the library HandheldCompanion ships), decided over
///     vendoring its C++/CLI project — the decompiled claim/update/release paths and the header layout
///     were live-verified against RTSS 2.21 on the reference Claw (2026-09-01).
/// </summary>
internal static class RtssOsdSlots
{
    // Same header the frametime reader walks: signature and version (both on RtssMappedRegion),
    // the app fields, then the OSD fields.
    private const int HeaderOsdEntrySizeOffset = 20;
    private const int HeaderOsdArrOffsetOffset = 24;
    private const int HeaderOsdArrSizeOffset = 28;
    private const int HeaderOsdFrameOffset = 32;
    private const int HeaderOsdBusyOffset = 36;

    // OSD entry: szOSD[256] (pre-2.7 text), szOSDOwner[256], szOSDEx[4096] (2.7+ text).
    private const int OwnerOffset = 256;
    private const int OwnerLength = 256;
    private const int TextOffset = 0;
    private const int TextLength = 256;
    private const int TextExOffset = 512;
    private const int TextExLength = 4096;

    // 2.7 moved text to szOSDEx; 2.14 added the busy flag around text writes.
    private const uint TextExMinimumVersion = 0x0002_0007;
    private const uint BusyMinimumVersion = 0x0002_000E;

    /// <summary>Claims a slot for <paramref name="owner" /> when needed and writes the OSD text.</summary>
    /// <param name="region">The mapped RTSS shared memory.</param>
    /// <param name="owner">Slot owner identity, ANSI, at most 255 bytes.</param>
    /// <param name="text">OSD text; empty clears the display while keeping the slot.</param>
    /// <returns>
    ///     False when the mapping is not an RTSS region this build understands or every
    ///     slot is owned by someone else.
    /// </returns>
    /// <remarks>
    ///     Slot 0 belongs to RTSS itself and is never touched. An existing slot carrying
    ///     this owner is reused, so the claim survives a writer restart.
    /// </remarks>
    internal static bool TryWrite(IRtssOsdRegion region, string owner, string text)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!TryReadHeader(region, out var version, out var entrySize, out var arrayOffset,
                out var arraySize))
        {
            return false;
        }

        var synchronized = version >= BusyMinimumVersion;
        if (synchronized && !region.TryAcquireBusy(HeaderOsdBusyOffset))
        {
            // No owner, text or frame-counter mutation happened. Retry on the next render tick.
            return false;
        }

        try
        {
            var ownerBytes = Encoding.ASCII.GetBytes(owner);
            var slot = FindSlot(region, ownerBytes, entrySize, arrayOffset, arraySize, text.Length > 0);
            if (slot < 0)
            {
                return text.Length == 0;
            }

            var entry = arrayOffset + slot * entrySize;
            var textBytes = Encoding.ASCII.GetBytes(text);
            var useExtended = version >= TextExMinimumVersion
                              && entrySize >= TextExOffset + TextExLength;
            var textAt = entry + (useExtended ? TextExOffset : TextOffset);
            var capacity = useExtended ? TextExLength : TextLength;
            var payload = new byte[Math.Min(textBytes.Length, capacity - 1) + 1];
            Array.Copy(textBytes, payload, payload.Length - 1);
            region.WriteBytes(textAt, payload, payload.Length);
            region.WriteUInt32(HeaderOsdFrameOffset, unchecked(region.ReadUInt32(HeaderOsdFrameOffset) + 1));
            return true;
        }
        finally
        {
            if (synchronized)
            {
                region.ReleaseBusy(HeaderOsdBusyOffset);
            }
        }
    }

    /// <summary>Releases every slot owned by <paramref name="owner" /> by zeroing it whole.</summary>
    /// <param name="region">The mapped RTSS shared memory.</param>
    /// <param name="owner">Slot owner identity.</param>
    /// <returns>True when released or absent; false when the mapping is incompatible or a writer is busy.</returns>
    internal static bool Release(IRtssOsdRegion region, string owner)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!TryReadHeader(region, out var version, out var entrySize, out var arrayOffset,
                out var arraySize))
        {
            return false;
        }

        var synchronized = version >= BusyMinimumVersion;
        if (synchronized && !region.TryAcquireBusy(HeaderOsdBusyOffset))
        {
            return false;
        }

        try
        {
            var ownerBytes = Encoding.ASCII.GetBytes(owner);
            var zero = new byte[Math.Min(entrySize, 65536)];
            while (FindSlot(region, ownerBytes, entrySize, arrayOffset, arraySize, false) is var slot
                   && slot >= 0)
            {
                var remaining = entrySize;
                var at = arrayOffset + slot * entrySize;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, zero.Length);
                    region.WriteBytes(at, zero, chunk);
                    at += chunk;
                    remaining -= chunk;
                }

                region.WriteUInt32(HeaderOsdFrameOffset, unchecked(region.ReadUInt32(HeaderOsdFrameOffset) + 1));
            }

            return true;
        }
        finally
        {
            if (synchronized)
            {
                region.ReleaseBusy(HeaderOsdBusyOffset);
            }
        }
    }

    private static bool TryReadHeader(
        IRtssOsdRegion region,
        out uint version,
        out long entrySize,
        out long arrayOffset,
        out uint arraySize)
    {
        version = 0;
        entrySize = 0;
        arrayOffset = 0;
        arraySize = 0;
        if (region.Capacity < HeaderOsdBusyOffset + 4 || region.ReadUInt32(0) != RtssMappedRegion.Signature)
        {
            return false;
        }

        version = region.ReadUInt32(RtssMappedRegion.HeaderVersionOffset);
        entrySize = region.ReadUInt32(HeaderOsdEntrySizeOffset);
        arrayOffset = region.ReadUInt32(HeaderOsdArrOffsetOffset);
        arraySize = region.ReadUInt32(HeaderOsdArrSizeOffset);
        return entrySize >= OwnerOffset + OwnerLength && arraySize > 1;
    }

    private static long FindSlot(
        IRtssOsdRegion region,
        byte[] owner,
        long entrySize,
        long arrayOffset,
        uint arraySize,
        bool claim)
    {
        var current = new byte[OwnerLength];
        long firstEmpty = -1;
        for (uint index = 1; index < arraySize; index++)
        {
            var entry = arrayOffset + index * entrySize;
            if (entry < 0 || entry + entrySize > region.Capacity)
            {
                break;
            }

            region.ReadBytes(entry + OwnerOffset, current, OwnerLength);
            if (OwnerMatches(current, owner))
            {
                return index;
            }

            if (firstEmpty < 0 && current[0] == 0)
            {
                firstEmpty = index;
            }
        }

        if (!claim || firstEmpty < 0)
        {
            return -1;
        }

        var claimBytes = new byte[OwnerLength];
        Array.Copy(owner, claimBytes, Math.Min(owner.Length, OwnerLength - 1));
        region.WriteBytes(arrayOffset + firstEmpty * entrySize + OwnerOffset,
            claimBytes, OwnerLength);
        return firstEmpty;
    }

    private static bool OwnerMatches(byte[] current, byte[] owner)
    {
        if (owner.Length >= OwnerLength || current.Length < owner.Length + 1)
        {
            return false;
        }

        return current.AsSpan(0, owner.Length).SequenceEqual(owner)
               && current[owner.Length] == 0;
    }
}
