using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Reads and changes the primary display's mode.
///     This is the narrow primary-display path behind the overlay's and Steam QAM's resolution and refresh-rate
///     pickers: saved resolution choices and transient refresh-rate pairing on one display. Saved multi-display
///     arrangements are a different problem with different rules and live in
///     <see cref="WindowsDeviceControl.DisplayLayouts" />.
///     The native mode calls go through <see cref="DisplayModes" />, which shares one gate with the
///     overlay's per-target mode changes, so the two paths can never interleave a read and a
///     write.
/// </summary>
internal static class PrimaryDisplayModes
{
    /// <summary>Minimum source-pixel count, independent of aspect ratio and rotation.</summary>
    private const long MinimumUsablePixels = 800L * 600;

    /// <summary>Retains useful widescreen and portrait modes while filtering legacy low-resolution modes.</summary>
    internal static bool IsUsableResolution(int width, int height)
    {
        return width > 0 && height > 0 && (long)width * height >= MinimumUsablePixels;
    }

    /// <summary>Reads the primary display's identity, resolution and colour depth as one operating point.</summary>
    /// <returns>The operating point, or null when the primary changed while it was read or cannot be read.</returns>
    /// <remarks>
    ///     The topology is observed before and after the mode read on purpose: the point is only returned when
    ///     the same target stayed primary and its layout matches the mode just read.
    /// </remarks>
    internal static DisplayOperatingPoint? ReadPrimaryOperatingPoint()
    {
        try
        {
            var primary = DisplayLayouts.Observe().Targets.FirstOrDefault(target => target.Current?.IsPrimary == true);
            return primary?.Current is { } layout && DisplayModes.ReadPrimaryMode() is { } mode
                                                  && DisplayLayouts.Observe().Targets
                                                      .FirstOrDefault(target => target.Current?.IsPrimary == true)
                                                      ?.Target == primary.Target
                                                  && layout.Width == mode.Width && layout.Height == mode.Height
                ? new DisplayOperatingPoint(primary.Target, layout.Width, layout.Height, mode.BitsPerPixel)
                : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Applies a refresh rate to one captured target, keeping its current resolution.</summary>
    /// <param name="target">The display the rate belongs to.</param>
    /// <param name="rate">The rate to apply.</param>
    /// <returns>Whether the target already ran at the rate or Windows accepted the change.</returns>
    internal static bool TryRestoreRefreshRate(DisplayTargetIdentity target, int rate)
    {
        var observed = DisplayModes.Read(target);
        return observed is not null
               && (observed.Current.RefreshHz == rate
                   || DisplayModes.Apply(observed, observed.Current with { RefreshHz = rate }).Applied);
    }

    /// <summary>
    ///     The resolutions Windows lists for the primary display at its current refresh rate and colour depth.
    /// </summary>
    /// <returns>
    ///     Listed resolutions, ascending by pixel count and deduplicated. Empty when the display cannot be read.
    /// </returns>
    /// <remarks>
    ///     Enumerated only, never tested with <c>CDS_TEST</c>: the test refused every mode but the current one
    ///     while a panel was still waking from sleep, and that answer was cached, so a resumed display offered a
    ///     single resolution and a single refresh rate (Atlas, 2026-11-01). A listed mode the driver then refuses is
    ///     reported by the write itself, which changes nothing.
    ///     <para>
    ///         Held at the current refresh rate on purpose. A resolution row that also moved the refresh
    ///         rate would fight the frame-limit pairing, which owns that axis; the two are separate
    ///         controls precisely so one change is one change.
    ///     </para>
    ///     <para>
    ///         Legacy modes below 800x600's pixel count are dropped. The threshold is an area, not a
    ///         minimum height: 960x540 and rotated equivalents are useful handheld modes. The active mode
    ///         remains visible even when it is below that threshold.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<DisplayResolution> EnumerateResolutions()
    {
        if (DisplayModes.ReadPrimaryMode() is not { } current)
        {
            Log.Warn("Display modes: current settings unreadable; no resolutions listed.");
            return [];
        }

        var resolutions = ResolutionsOf(current, DisplayModes.EnumeratePrimaryModes());
        Log.Change(
            "display.modes.resolutions",
            $"Display modes: {current.Width}x{current.Height} at {current.RefreshHz} Hz, "
            + $"resolutions [{string.Join(",", resolutions.Select(mode => $"{mode.Width}x{mode.Height}"))}]");
        return resolutions;
    }

    /// <summary>The refresh rates Windows lists for the primary display at its current resolution.</summary>
    /// <returns>Listed rates, ascending and deduplicated. Empty when the display cannot be read.</returns>
    /// <remarks>
    ///     Enumerated only, never tested, for the reason <see cref="EnumerateResolutions" /> gives. The list is
    ///     what every refresh-rate write may choose from: Windows changes the mode to a listed rate or refuses,
    ///     and a driver commonly lists rates the panel never advertises (the reference Claw lists
    ///     30/48/60/75/100/120 while its EDID names only 60 and 120).
    ///     <para>
    ///         Hardcoding a rate list is the one thing this must never become: a panel without variable
    ///         refresh will likely list nothing but what it advertises, and that is exactly the case the
    ///         frame-limit strategies exist to serve.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> EnumerateRefreshRates()
    {
        if (DisplayModes.ReadPrimaryMode() is not { } current)
        {
            Log.Warn("Display modes: current settings unreadable; no refresh rates listed.");
            return [];
        }

        var rates = RefreshRatesOf(current, DisplayModes.EnumeratePrimaryModes());
        Log.Change(
            "display.modes.rates",
            $"Display modes: {current.Width}x{current.Height} at {current.RefreshHz} Hz, "
            + $"rates [{string.Join(",", rates)}]");
        return rates;
    }

    /// <summary>The resolutions to offer from one enumeration, at the current rate and colour depth.</summary>
    /// <param name="current">The mode the display runs.</param>
    /// <param name="modes">Every mode Windows enumerated, in any order and with duplicates.</param>
    /// <returns>The resolutions, ascending by pixel count, then width; the current one always included.</returns>
    internal static IReadOnlyList<DisplayResolution> ResolutionsOf(
        PrimaryDisplayMode current, IEnumerable<PrimaryDisplayMode> modes)
    {
        return
        [
            .. modes.Where(mode => mode.BitsPerPixel == current.BitsPerPixel
                                   && mode.RefreshHz == current.RefreshHz
                                   && IsUsableResolution(mode.Width, mode.Height))
                .Select(mode => new DisplayResolution(mode.Width, mode.Height))
                .Append(new DisplayResolution(current.Width, current.Height))
                .Distinct()
                .OrderBy(mode => (long)mode.Width * mode.Height)
                .ThenBy(mode => mode.Width)
        ];
    }

    /// <summary>The refresh rates to offer from one enumeration, at the current resolution and colour depth.</summary>
    /// <param name="current">The mode the display runs.</param>
    /// <param name="modes">Every mode Windows enumerated, in any order and with duplicates.</param>
    /// <returns>The rates, ascending; the current one always included.</returns>
    internal static IReadOnlyList<int> RefreshRatesOf(PrimaryDisplayMode current, IEnumerable<PrimaryDisplayMode> modes)
    {
        return
        [
            .. modes.Where(mode => mode.Width == current.Width
                                   && mode.Height == current.Height
                                   && mode.BitsPerPixel == current.BitsPerPixel)
                .Select(mode => mode.RefreshHz)
                .Append(current.RefreshHz)
                .Where(rate => rate > 1)
                .Distinct()
                .Order()
        ];
    }

    /// <summary>
    ///     Applies a resolution to the primary display and saves it as the display's resolution.
    /// </summary>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <returns>Whether Windows accepted the change, or the display already ran at that resolution.</returns>
    /// <remarks>
    ///     Saved like a choice in Windows Settings, because it is the user's choice: a dynamic change ended at
    ///     the next restart, and the display came back at whatever resolution was saved before it.
    ///     <para>
    ///         The refresh rate is carried over from the current mode rather than left to the driver's
    ///         default for the new resolution. Changing one axis must not silently change the other, or a
    ///         resolution change would undo whatever the frame-limit pairing had just set. The saved mode
    ///         keeps the saved refresh rate, so a rate the pairing holds never becomes the default.
    ///     </para>
    /// </remarks>
    public static bool TryApplyResolution(int width, int height)
    {
        if (DisplayModes.ReadPrimaryMode() is not { } current)
        {
            Log.Warn($"Display modes: refusing {width}x{height}; current settings unreadable.");
            return false;
        }

        // Not skipped when the display already shows it: an earlier dynamic change may have put it there
        // without saving it, and the saved resolution is what the next restart comes back at.
        var status = DisplayModes.ApplyPrimaryResolution(width, height);
        if (status != 0)
        {
            Log.Warn(
                $"Display modes: {width}x{height} refused with status {status} "
                + $"(was {current.Width}x{current.Height} at {current.RefreshHz} Hz).");
            return false;
        }

        Log.Info(
            $"Display modes: {current.Width}x{current.Height} -> {width}x{height} "
            + $"at {current.RefreshHz} Hz (saved).");
        return true;
    }

    /// <summary>The resolution the primary display is running at.</summary>
    /// <returns>The resolution, or null when it cannot be read.</returns>
    public static DisplayResolution? ReadCurrentResolution()
    {
        return DisplayModes.ReadPrimaryMode() is { } current
            ? new DisplayResolution(current.Width, current.Height)
            : null;
    }

    /// <summary>The refresh rate the primary display is running at.</summary>
    /// <returns>The rate in Hz, or null when it cannot be read.</returns>
    public static int? ReadCurrentRefreshRate()
    {
        return DisplayModes.ReadPrimaryMode()?.RefreshHz;
    }
}
