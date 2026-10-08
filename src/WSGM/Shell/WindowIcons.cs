using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Resolves window icons and rasterizes them for the taskbar.</summary>
/// <remarks>
///     Tries bounded window messages, class icons, drag icons and executable resources in order.
///     Foreign HICONs remain owned by their source: rendering uses a CopyIcon result and destroys only
///     that copy. Executable extraction avoids dependence on shell icon-cache and COM initialization.
/// </remarks>
public sealed class WindowIconCache
{
    private readonly Dictionary<nint, Bitmap?> _byWindow = [];
    private readonly HashSet<nint> _inFlight = [];
    private readonly int _pixelSize;
    private readonly Lock _sync = new();
    private int _generation;

    /// <summary>Creates a cache that rasterizes icons at one physical pixel size.</summary>
    /// <param name="pixelSize">The square icon size in physical pixels (e.g. 32).</param>
    public WindowIconCache(int pixelSize)
    {
        _pixelSize = Math.Max(8, pixelSize);
    }

    /// <summary>
    ///     Returns an already-resolved icon without doing any work. Callers on the
    ///     UI thread use this and hand a miss to <see cref="ResolveInBackground" />.
    /// </summary>
    /// <param name="hwnd">The window whose icon to look up.</param>
    /// <param name="bitmap">The cached icon, or null when the window has none.</param>
    /// <returns><see langword="true" /> when this window has already been resolved.</returns>
    public bool TryGetCached(nint hwnd, out Bitmap? bitmap)
    {
        lock (_sync)
        {
            return _byWindow.TryGetValue(hwnd, out bitmap);
        }
    }

    /// <summary>
    ///     Resolves a window's icon off the calling thread and reports the result.
    ///     <para>
    ///         Resolution is expensive and must never run on the UI thread: it sends up to
    ///         four cross-process <c>WM_GETICON</c> probes that each wait out a 200 ms timeout
    ///         against a busy or hung app, then falls back to opening the owning process and
    ///         reading its executable — on an SD-card library that adds disk latency. Doing it
    ///         inline froze the bar for hundreds of milliseconds at the moment it was swiped up.
    ///     </para>
    ///     <para>
    ///         At most one resolve per window is in flight, and a resolve that finishes
    ///         after <see cref="Clear" /> disposes its bitmap instead of repopulating a cache the
    ///         caller has already torn down. <paramref name="onResolved" /> runs on a thread-pool
    ///         thread — marshal to the UI thread before touching view state.
    ///     </para>
    /// </summary>
    /// <param name="hwnd">The window whose icon to resolve.</param>
    /// <param name="processId">The owning process, for the executable-icon fallback.</param>
    /// <param name="onResolved">Receives the window handle and its icon (null when none).</param>
    public void ResolveInBackground(nint hwnd, uint processId, Action<nint, Bitmap?> onResolved)
    {
        int generation;
        lock (_sync)
        {
            if (_byWindow.ContainsKey(hwnd) || !_inFlight.Add(hwnd))
            {
                return;
            }

            generation = _generation;
        }

        _ = Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try
            {
                bitmap = Resolve(hwnd, processId);
            }
            catch (Exception ex)
            {
                Log.Warn($"Icon resolution failed for window 0x{hwnd:X}: {ex.Message}");
            }

            lock (_sync)
            {
                _inFlight.Remove(hwnd);
                if (generation != _generation)
                {
                    bitmap?.Dispose();
                    return;
                }

                _byWindow[hwnd] = bitmap;
            }

            onResolved(hwnd, bitmap);
        });
    }

    /// <summary>
    ///     Drops every cached bitmap (called when the taskbar closes so the
    ///     invisible shell doesn't hold pixel data). Resolves still in flight are
    ///     invalidated rather than allowed to repopulate the cache.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _generation++;
            foreach (var bitmap in _byWindow.Values)
            {
                bitmap?.Dispose();
            }

            _byWindow.Clear();
        }
    }

    private Bitmap? Resolve(nint hwnd, uint processId)
    {
        // Foreign handles: render a private copy, never destroy the original.
        var foreign = QueryWindowIcon(hwnd);
        if (foreign != 0)
        {
            var copy = NativeMethods.CopyIcon(foreign);
            if (copy != 0)
            {
                Bitmap? rendered;
                try
                {
                    rendered = Render(copy);
                }
                finally
                {
                    NativeMethods.DestroyIcon(copy);
                }

                // A handle that won't rasterize must not end the chain — the exe's
                // icon resource below is the advertised last resort.
                if (rendered is not null)
                {
                    return rendered;
                }
            }
        }

        // Last resort: the exe's own first icon resource. These handles are ours.
        var exe = NativeShellProcess.TryGetImagePath(processId);
        if (exe is null)
        {
            return null;
        }

        var extracted = NativeMethods.ExtractIconExW(exe, 0, out var large, out var small, 1);
        if (extracted is 0 or unchecked((uint)-1))
        {
            return null;
        }

        try
        {
            var best = large != 0 ? large : small;
            return best != 0 ? Render(best) : null;
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
    ///     Asks the window itself for an icon handle (foreign-owned; may be 0).
    ///     200 ms + SMTO_ABORTIFHUNG per query keeps a hung app from stalling the bar.
    /// </summary>
    private static nint QueryWindowIcon(nint hwnd)
    {
        Span<nint> requests = [NativeMethods.IconBig, NativeMethods.IconSmall2, NativeMethods.IconSmall];
        foreach (var request in requests)
        {
            if (NativeMethods.SendMessageTimeoutW(
                    hwnd, NativeMethods.WmGetIcon, request, 0,
                    NativeMethods.SmtoAbortIfHung, 200, out var icon) != 0 && icon != 0)
            {
                return icon;
            }
        }

        var classIcon = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.GclpHicon);
        if (classIcon != 0)
        {
            return classIcon;
        }

        classIcon = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.GclpHiconSm);
        if (classIcon != 0)
        {
            return classIcon;
        }

        if (NativeMethods.SendMessageTimeoutW(
                hwnd, NativeMethods.WmQueryDragIcon, 0, 0,
                NativeMethods.SmtoAbortIfHung, 200, out var dragIcon) != 0)
        {
            return dragIcon;
        }

        return 0;
    }


    private Bitmap? Render(nint hIcon)
    {
        return IconRasterizer.Rasterize(hIcon, _pixelSize);
    }
}

/// <summary>
///     Turns a live HICON into an Avalonia bitmap through a 32-bpp DIB.
///     Shared by the taskbar's window-icon cache and the tray host's synchronous
///     icon snapshots.
/// </summary>
internal static class IconRasterizer
{
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
