using System;
using System.Buffers.Binary;
using System.IO;

namespace WSGM.Core;

internal static class ArtworkImageHeader
{
    private const int MaxDimension = 20_000;
    private const long MaxPixels = 80_000_000;

    internal static bool IsWithinLimits(int width, int height)
    {
        return width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension
               && (long)width * height <= MaxPixels;
    }

    internal static bool TryReadSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 512, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[8];
            if (!Fill(stream, signature))
            {
                return false;
            }

            if (signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            {
                Span<byte> ihdr = stackalloc byte[16];
                if (!Fill(stream, ihdr) || !ihdr[4..8].SequenceEqual("IHDR"u8))
                {
                    return false;
                }

                var pngWidth = BinaryPrimitives.ReadUInt32BigEndian(ihdr[8..12]);
                var pngHeight = BinaryPrimitives.ReadUInt32BigEndian(ihdr[12..16]);
                if (pngWidth is 0 or > int.MaxValue || pngHeight is 0 or > int.MaxValue)
                {
                    return false;
                }

                width = (int)pngWidth;
                height = (int)pngHeight;
                return true;
            }

            if (signature[0] != 0xff || signature[1] != 0xd8)
            {
                return false;
            }

            stream.Position = 2;
            return TryReadJpeg(stream, out width, out height);
        }
        catch (Exception)
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    private static bool TryReadJpeg(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> pair = stackalloc byte[2];
        Span<byte> frame = stackalloc byte[5];
        while (stream.ReadByte() == 0xff)
        {
            int marker;
            do
            {
                marker = stream.ReadByte();
            } while (marker == 0xff);

            if (marker < 0 || marker is 0xd9 or 0xda)
            {
                return false;
            }

            if (marker is 0x01 or >= 0xd0 and <= 0xd8)
            {
                continue;
            }

            if (!Fill(stream, pair))
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(pair);
            var isFrame = marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc);
            if (isFrame)
            {
                if (length < 7 || !Fill(stream, frame))
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(frame[1..3]);
                width = BinaryPrimitives.ReadUInt16BigEndian(frame[3..5]);
                return width > 0 && height > 0;
            }

            if (length < 2)
            {
                return false;
            }

            stream.Seek(length - 2, SeekOrigin.Current);
        }

        return false;
    }

    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
