using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Turns a live HICON into an Avalonia bitmap through a 32-bpp DIB.
///     Shared by the taskbar's window-icon cache and the tray host's synchronous
///     icon snapshots.
/// </summary>
internal static class IconRasterizer
{
    /// <summary>Rasterizes an executable's first icon resource, preferring its large icon.</summary>
    /// <param name="path">Executable whose resources are read.</param>
    /// <param name="size">Positive square output dimension in physical pixels.</param>
    /// <returns>A new caller-owned bitmap to dispose, or null when the file has no readable icon.</returns>
    internal static Bitmap? FromExecutable(string path, int size)
    {
        var extracted = NativeMethods.ExtractIconExW(path, 0, out var large, out var small, 1);
        if (extracted is 0 or unchecked((uint)-1))
        {
            return null;
        }

        try
        {
            var best = large != 0 ? large : small;
            return best != 0 ? Rasterize(best, size) : null;
        }
        finally
        {
            if (large != 0)
            {
                NativeMethods.DestroyIcon(large);
            }

            if (small != 0)
            {
                NativeMethods.DestroyIcon(small);
            }
        }
    }

    /// <summary>
    ///     Draws the icon into a 32-bpp top-down DIB and copies the BGRA pixels
    ///     into an Avalonia bitmap. Legacy icons without an alpha channel come out of
    ///     DrawIconEx fully transparent (alpha 0 everywhere); a second DI_MASK pass
    ///     reconstructs their opacity (mask black = opaque).
    /// </summary>
    /// <param name="hIcon">Borrowed live icon handle; the caller retains ownership and keeps it valid during drawing.</param>
    /// <param name="size">Positive square output dimension in physical pixels.</param>
    /// <returns>
    ///     A new caller-owned bitmap to dispose, or null when native drawing fails; managed allocation failures
    ///     propagate.
    /// </returns>
    internal static Bitmap? Rasterize(nint hIcon, int size)
    {
        var pixels = new byte[size * size * 4];
        if (!DrawIntoDib(hIcon, size, NativeMethods.DiNormal, pixels))
        {
            return null;
        }

        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0)
            {
                continue;
            }

            anyAlpha = true;
            break;
        }

        if (!anyAlpha)
        {
            var mask = new byte[size * size * 4];
            if (DrawIntoDib(hIcon, size, NativeMethods.DiMask, mask))
            {
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                }
            }
            else
            {
                // No usable mask either — treat everything as opaque rather than
                // rendering an invisible tile.
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        var sourceStride = size * 4;
        for (var row = 0; row < size; row++)
        {
            Marshal.Copy(pixels, row * sourceStride, framebuffer.Address + row * framebuffer.RowBytes, sourceStride);
        }

        return bitmap;
    }

    private static unsafe bool DrawIntoDib(nint hIcon, int size, uint flags, byte[] destination)
    {
        var dc = NativeMethods.CreateCompatibleDC(0);
        if (dc == 0)
        {
            return false;
        }

        nint dib = 0;
        nint previous = 0;
        try
        {
            var header = new NativeMethods.BitmapInfoHeader
            {
                biSize = (uint)sizeof(NativeMethods.BitmapInfoHeader),
                biWidth = size,
                biHeight = -size, // top-down so rows read in display order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BiRgb
            };
            dib = NativeMethods.CreateDIBSection(dc, &header, NativeMethods.DibRgbColors, out var bits, 0, 0);
            if (dib == 0 || bits == 0)
            {
                return false;
            }

            previous = NativeMethods.SelectObject(dc, dib);
            if (!NativeMethods.DrawIconEx(dc, 0, 0, hIcon, size, size, 0, 0, flags))
            {
                return false;
            }

            Marshal.Copy(bits, destination, 0, destination.Length);
            return true;
        }
        finally
        {
            if (previous != 0)
            {
                NativeMethods.SelectObject(dc, previous);
            }

            if (dib != 0)
            {
                NativeMethods.DeleteObject(dib);
            }

            NativeMethods.DeleteDC(dc);
        }
    }
}
