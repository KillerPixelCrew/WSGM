using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>When Windows last recorded keyboard, mouse or touch input for this session.</summary>
internal static class LastInput
{
    /// <summary>The 32-bit tick count of the last input, or null when Windows cannot say.</summary>
    /// <returns>Windows session input tick count, or null when GetLastInputInfo fails.</returns>
    internal static uint? Tick()
    {
        NativeMethods.LastInputInfo info = new() { CbSize = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        return NativeMethods.GetLastInputInfo(ref info) ? info.DwTime : null;
    }

    /// <summary>How long ago the last input was, or zero when it cannot be read.</summary>
    /// <remarks>
    ///     Zero reads as "somebody just touched it", the answer that refuses anything done behind a
    ///     user's back. The tick count wraps every 49.7 days; the unchecked subtraction is correct
    ///     across the wrap.
    /// </remarks>
    /// <returns>Unsigned wrap-safe elapsed time, or zero on query failure to suppress idle actions.</returns>
    internal static TimeSpan Age()
    {
        return Tick() is { } tick
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - tick))
            : TimeSpan.Zero;
    }
}
