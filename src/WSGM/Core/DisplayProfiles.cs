using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Microsoft.Win32;
using WindowsDeviceControl;
using static WSGM.Interop.NativeDisplay;

namespace WSGM.Core;

/// <summary>
///     Reads and transiently changes the primary display's mode.
///     This is the narrow GDI path behind the overlay's and Steam QAM's resolution and refresh-rate
///     pickers: one display, no persistence, no registry staging. Saved multi-display arrangements are
///     a different problem with different rules and live in
///     <see cref="WindowsDeviceControl.DisplayLayouts" />.
///     The native mode calls go through <see cref="DisplayModes" />, which shares one gate with the
///     overlay's per-target mode changes, so the two paths can never interleave a read and a
///     write.
/// </summary>
public static unsafe class DisplayProfiles
{
    /// <summary>Smallest resolution worth offering. Below this is legacy driver noise.</summary>
    private const int MinimumUsableWidth = 800;

    /// <summary>Smallest resolution height worth offering.</summary>
    private const int MinimumUsableHeight = 600;

    /// <summary>
    ///     Discovers the resolutions the driver accepts on the primary display, at its current refresh
    ///     rate and colour depth.
    /// </summary>
    /// <returns>
    ///     Accepted resolutions, ascending by pixel count and deduplicated. Empty when the display
    ///     cannot be read.
    /// </returns>
    /// <remarks>
    ///     The same discover-then-test discipline as <see cref="EnumerateAcceptedRefreshRates" />, and
    ///     for the same reason: an enumerated mode is a claim, not a promise, and `CDS_TEST` changes
    ///     nothing so this is safe to call while a game is running.
    ///     <para>
    ///         Held at the current refresh rate on purpose. A resolution row that also moved the refresh
    ///         rate would fight the frame-limit pairing, which owns that axis; the two are separate
    ///         controls precisely so one change is one change.
    ///     </para>
    ///     <para>
    ///         Anything below 800x600 is dropped. Drivers enumerate legacy modes no handheld panel should
    ///         offer, and a resolution list whose first entry is 640x480 is a list the user has to scroll
    ///         past rather than one they can use.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<DisplayResolution> EnumerateAcceptedResolutions()
    {
        return
        [
            .. EnumerateAccepted(
                    "resolutions",
                    "accepted resolutions",
                    static (mode, current) => mode.BitsPerPixel == current.BitsPerPixel
                                              && mode.RefreshHz == current.RefreshHz
                                              && mode is
                                              {
                                                  Width: >= MinimumUsableWidth, Height: >= MinimumUsableHeight
                                              },
                    static (mode, current) => new CandidateMode(mode.Width, mode.Height, current.RefreshHz),
                    static mode => $"{mode.Width}x{mode.Height}")
                .Select(mode => new DisplayResolution(mode.Width, mode.Height))
        ];
    }

    /// <summary>
    ///     The refresh rates the primary display will actually accept at its current resolution.
    /// </summary>
    /// <returns>Accepted rates, ascending and deduplicated. Empty when the display cannot be read.</returns>
    /// <remarks>
    ///     Enumerated and then <em>tested</em>, never assumed: a driver commonly offers rates the panel
    ///     never advertises — the reference Claw accepts 30/48/60/75/100/120 while its EDID lists only
    ///     60 and 120 — and equally may refuse one it enumerated. `CDS_TEST` changes nothing, so this is
    ///     safe to call while a game is running.
    ///     <para>
    ///         Hardcoding a rate list is the one thing this must never become: a panel without variable
    ///         refresh will likely accept nothing but what it advertises, and that is exactly the case the
    ///         frame-limit strategies exist to serve.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> EnumerateAcceptedRefreshRates()
    {
        return
        [
            .. EnumerateAccepted(
                    "refresh rates",
                    "accepted",
                    static (mode, current) => mode.Width == current.Width
                                              && mode.Height == current.Height
                                              && mode.BitsPerPixel == current.BitsPerPixel
                                              && mode.RefreshHz > 1,
                    static (mode, current) => new CandidateMode(current.Width, current.Height, mode.RefreshHz),
                    static mode => mode.RefreshHz.ToString())
                .Select(mode => mode.RefreshHz)
        ];
    }

    private static List<CandidateMode> EnumerateAccepted(
        string noun,
        string acceptedLabel,
        Func<PrimaryDisplayMode, PrimaryDisplayMode, bool> keep,
        Func<PrimaryDisplayMode, PrimaryDisplayMode, CandidateMode> candidate,
        Func<CandidateMode, string> describe)
    {
        if (DisplayModes.ReadPrimaryMode() is not { } current)
        {
            Log.Warn($"Display modes: current settings unreadable; no {noun} discovered.");
            return [];
        }

        HashSet<CandidateMode> enumerated = [];
        foreach (var mode in DisplayModes.EnumeratePrimaryModes())
        {
            if (keep(mode, current))
            {
                enumerated.Add(candidate(mode, current));
            }
        }

        List<CandidateMode> accepted = [];
        List<string> refused = [];
        foreach (var mode in enumerated
                     .OrderBy(entry => (long)entry.Width * entry.Height)
                     .ThenBy(entry => entry.Width)
                     .ThenBy(entry => entry.RefreshHz))
        {
            var isCurrent = mode.Width == current.Width
                            && mode.Height == current.Height
                            && mode.RefreshHz == current.RefreshHz;
            if (isCurrent || DisplayModes.TestPrimaryMode(mode.Width, mode.Height, mode.RefreshHz))
            {
                accepted.Add(mode);
            }
            else
            {
                refused.Add(describe(mode));
            }
        }

        Log.Info(
            $"Display modes: {current.Width}x{current.Height} at {current.RefreshHz} Hz, "
            + $"{acceptedLabel} [{string.Join(",", accepted.Select(describe))}]"
            + (refused.Count is 0 ? "" : $", refused [{string.Join(",", refused)}]"));
        return accepted;
    }

    /// <summary>
    ///     Applies a refresh rate to the primary display without persisting it.
    /// </summary>
    /// <param name="refreshHz">The rate to apply.</param>
    /// <returns><see langword="true" /> when the display reports the new rate afterwards.</returns>
    /// <remarks>
    ///     Deliberately dynamic: no `CDS_UPDATEREGISTRY`, so the user's saved display configuration is
    ///     untouched and exit, a crash, or a reboot all restore it without WSGM doing anything. That is
    ///     what makes a game-scoped refresh change safe to make at all.
    ///     <para>
    ///         Distinct from the display-profile path above, which deliberately does persist. Do not merge
    ///         them: a profile is the user's chosen configuration, and this is a transient pairing WSGM owns
    ///         for the duration of a cap.
    ///     </para>
    /// </remarks>
    public static bool TryApplyTransientRefreshRate(int refreshHz)
    {
        return TryApplyTransient(
            $"{refreshHz} Hz",
            current => current.RefreshHz == refreshHz,
            current => current with { RefreshHz = refreshHz },
            static current => $"{current.RefreshHz} Hz",
            current => $"{current.RefreshHz} Hz -> {refreshHz} Hz");
    }

    /// <summary>
    ///     Applies a resolution to the primary display without persisting it.
    /// </summary>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <returns>Whether the display is now at that resolution.</returns>
    /// <remarks>
    ///     The same discipline as <see cref="TryApplyTransientRefreshRate" />, and for the same reason:
    ///     no <c>CDS_UPDATEREGISTRY</c>, so exit, crash, and reboot all restore the user's own
    ///     persisted configuration without WSGM having to remember to.
    ///     <para>
    ///         The refresh rate is carried over from the current mode rather than left to the driver's
    ///         default for the new resolution. Changing one axis must not silently change the other, or a
    ///         resolution change would undo whatever the frame-limit pairing had just set.
    ///     </para>
    /// </remarks>
    public static bool TryApplyTransientResolution(int width, int height)
    {
        return TryApplyTransient(
            $"{width}x{height}",
            current => current.Width == width && current.Height == height,
            current => current with { Width = width, Height = height },
            static current => $"{current.Width}x{current.Height} at {current.RefreshHz} Hz",
            current => $"{current.Width}x{current.Height} -> {width}x{height} "
                       + $"at {current.RefreshHz} Hz");
    }

    private static bool TryApplyTransient(
        string what,
        Func<PrimaryDisplayMode, bool> alreadyApplied,
        Func<PrimaryDisplayMode, PrimaryDisplayMode> retarget,
        Func<PrimaryDisplayMode, string> was,
        Func<PrimaryDisplayMode, string> transition)
    {
        if (DisplayModes.ReadPrimaryMode() is not { } current)
        {
            Log.Warn($"Display modes: refusing {what}; current settings unreadable.");
            return false;
        }

        if (alreadyApplied(current))
        {
            return true;
        }

        var target = retarget(current);
        var status = DisplayModes.ApplyPrimaryModeTransient(target.Width, target.Height, target.RefreshHz);
        if (status != 0)
        {
            Log.Warn($"Display modes: {what} refused with status {status} (was {was(current)}).");
            return false;
        }

        Log.Info($"Display modes: {transition(current)} (transient).");
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

    /// <summary>
    ///     The refresh rates the primary panel advertises in its own EDID.
    /// </summary>
    /// <returns>Advertised rates, ascending; empty when the EDID cannot be read.</returns>
    /// <remarks>
    ///     Distinct from <see cref="EnumerateAcceptedRefreshRates" />, and the difference is the point:
    ///     the driver accepts rates the panel never advertised, so only the EDID can say which modes are
    ///     the panel's own. An empty result makes the native-modes strategy offer nothing rather than
    ///     guess, which is the correct failure — a wrong list would pair caps against timings the panel
    ///     does not really have.
    /// </remarks>
    public static IReadOnlyList<int> ReadAdvertisedRefreshRates()
    {
        var instance = ReadPrimaryMonitorInstanceId();
        if (instance is null)
        {
            Log.Warn("Display modes: primary monitor instance unreadable; no advertised rates.");
            return [];
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid)
            {
                Log.Warn($"Display modes: no EDID under '{instance}'.");
                return [];
            }

            var rates = EdidModes.ReadAdvertisedRefreshRates(edid);
            Log.Info($"Display modes: panel advertises [{string.Join(",", rates)}].");
            return rates;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            Log.Warn($"Display modes: EDID unreadable for '{instance}': {ex.Message}");
            return [];
        }
    }

    /// <remarks>
    ///     The interface name is asked for specifically, because the default form of the monitor's
    ///     device id is a class path that does not identify the enum key the EDID lives under.
    /// </remarks>
    private static string? ReadPrimaryMonitorInstanceId()
    {
        for (uint i = 0;; i++)
        {
            var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(null, i, ref device, 0))
            {
                return null;
            }

            if ((device.StateFlags & DisplayDevicePrimary) == 0)
            {
                continue;
            }

            var monitor = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(device.DeviceName, 0, ref monitor, GetDeviceInterfaceName))
            {
                return null;
            }

            // \\?\DISPLAY#CSW0801#4&8f346&1&UID8388688#{guid} -> DISPLAY\CSW0801\4&8f346&1&UID8388688
            var id = FixedString(monitor.DeviceId, 128);
            var start = id.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            var trimmed = id[start..];
            var guid = trimmed.IndexOf("#{", StringComparison.Ordinal);
            if (guid > 0)
            {
                trimmed = trimmed[..guid];
            }

            return trimmed.Replace('#', '\\');
        }
    }

    private static string FixedString(char* value, int length)
    {
        var span = new ReadOnlySpan<char>(value, length);
        var end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    /// <summary>A full candidate mode, so one test path serves both discovery axes.</summary>
    private readonly record struct CandidateMode(int Width, int Height, int RefreshHz);
}

/// <summary>One display resolution the driver accepted.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct DisplayResolution(int Width, int Height)
{
    /// <summary>Renders the resolution the way a user reads it.</summary>
    /// <returns>Width and height separated by an <c>x</c>.</returns>
    public override string ToString()
    {
        return $"{Width}x{Height}";
    }
}
