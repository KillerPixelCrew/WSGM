using System;

namespace WSGM.Core;

/// <summary>The little of protobuf's wire format that launchers' own files need.</summary>
/// <remarks>
///     Ubisoft Connect's configuration cache and Battle.net's <c>product.db</c> are protobuf messages
///     with no published schema here, so they are read field by field. Every length is checked against
///     the buffer, anything unexpected is skipped by its wire type, and a malformed file yields less
///     rather than an exception.
/// </remarks>
internal static class Protobuf
{
    /// <summary>Reads one varint and advances past it.</summary>
    /// <param name="data">Complete message buffer.</param>
    /// <param name="position">Nonnegative cursor; partial consumption remains visible on failure.</param>
    /// <param name="value">Decoded value on success.</param>
    /// <returns>Whether a terminating byte appeared within the 64-bit read bound.</returns>
    internal static bool TryReadVarint(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= data.Length)
            {
                return false;
            }

            var current = data[position++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads one length-delimited field and advances past it.</summary>
    /// <param name="data">Complete message buffer.</param>
    /// <param name="position">Nonnegative cursor; a failed length read can still advance it.</param>
    /// <param name="bytes">Borrowed slice of data on success.</param>
    /// <returns>Whether the declared field fits inside the remaining buffer.</returns>
    internal static bool TryReadBytes(ReadOnlySpan<byte> data, ref int position, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (!TryReadVarint(data, ref position, out var length) || length > (ulong)(data.Length - position))
        {
            return false;
        }

        bytes = data.Slice(position, (int)length);
        position += (int)length;
        return true;
    }

    /// <summary>Skips one field of the given wire type; false for a group or a truncated field.</summary>
    /// <param name="data">Complete message buffer.</param>
    /// <param name="position">Nonnegative cursor advanced by consumed data.</param>
    /// <param name="wireType">Supported protobuf wire type: 0, 1, 2 or 5.</param>
    /// <returns>Whether the field was supported and fully skipped; failure does not rewind the cursor.</returns>
    internal static bool TrySkip(ReadOnlySpan<byte> data, ref int position, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref position, out _);
            case 1:
                return Advance(data, ref position, 8);
            case 2:
                return TryReadBytes(data, ref position, out _);
            case 5:
                return Advance(data, ref position, 4);
            default:
                // Groups are long obsolete and nothing here uses them; stop rather than guess.
                return false;
        }
    }

    private static bool Advance(ReadOnlySpan<byte> data, ref int position, int count)
    {
        if (data.Length - position < count)
        {
            return false;
        }

        position += count;
        return true;
    }
}
