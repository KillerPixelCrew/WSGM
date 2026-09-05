using System;
using System.ComponentModel;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>One of the four user-facing idle timeouts of the active power scheme.</summary>
public enum PowerTimeoutKind
{
    /// <summary>Turn off the display after (on battery).</summary>
    DisplayDc,

    /// <summary>Turn off the display after (plugged in).</summary>
    DisplayAc,

    /// <summary>Go to standby after (on battery).</summary>
    SleepDc,

    /// <summary>Go to standby after (plugged in).</summary>
    SleepAc,
}

/// <summary>Reads and writes the active power scheme's display-off and standby idle
/// timeouts through the flat powrprof policy API (locale-independent, unlike parsing
/// <c>powercfg /q</c>). Values are seconds; 0 means Never. Writes go to the active
/// scheme and are applied immediately via <c>PowerSetActiveScheme</c> — the same thing
/// the Settings app does, so Windows may later replace them (updates, OEM tools);
/// this is a convenience surface, not managed state WSGM must restore.</summary>
public static class PowerTimeouts
{
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    /// <summary>The cycle presets offered by the quick-access rows, in click order;
    /// 0 (Never) sits last so cycling reads as "longer, longer, …, never, shortest".</summary>
    internal static readonly int[] PresetsSeconds = [60, 180, 300, 600, 900, 1800, 3600, 0];

    /// <summary>Reads the timeout in seconds (0 = Never); null when the power API
    /// fails (reported once by the caller's UI, never thrown).</summary>
    /// <param name="kind">Which timeout to read.</param>
    public static int? Read(PowerTimeoutKind kind)
    {
        if (!TryGetActiveScheme(out var scheme))
        {
            return null;
        }
        var (subgroup, setting, dc) = Locate(kind);
        var status = dc
            ? NativeMethods.PowerReadDCValueIndex(0, in scheme, in subgroup, in setting, out var value)
            : NativeMethods.PowerReadACValueIndex(0, in scheme, in subgroup, in setting, out value);
        if (status != 0)
        {
            Log.Warn($"Power timeout read failed ({kind}, status {status}).");
            return null;
        }
        return (int)value;
    }

    /// <summary>Writes the timeout (seconds, 0 = Never) into the active scheme and
    /// re-activates the scheme so it takes effect immediately.</summary>
    /// <param name="kind">Which timeout to write.</param>
    /// <param name="seconds">The new value.</param>
    public static bool Write(PowerTimeoutKind kind, int seconds)
    {
        if (seconds < 0 || !TryGetActiveScheme(out var scheme))
        {
            return false;
        }
        var (subgroup, setting, dc) = Locate(kind);
        var status = dc
            ? NativeMethods.PowerWriteDCValueIndex(0, in scheme, in subgroup, in setting, (uint)seconds)
            : NativeMethods.PowerWriteACValueIndex(0, in scheme, in subgroup, in setting, (uint)seconds);
        if (status == 0)
        {
            status = NativeMethods.PowerSetActiveScheme(0, in scheme);
        }
        if (status != 0)
        {
            Log.Warn($"Power timeout write failed ({kind} = {seconds} s, status {status}).");
            return false;
        }
        Log.Info($"Power timeout set: {kind} = {Describe(seconds)}.");
        return true;
    }

    /// <summary>The next preset after <paramref name="currentSeconds"/> in cycle
    /// order. A value between presets snaps to the next longer one, so the first
    /// click never shortens an unusual custom timeout. Pure for unit tests.</summary>
    /// <param name="currentSeconds">The current timeout (0 = Never).</param>
    internal static int NextPreset(int currentSeconds)
    {
        for (var i = 0; i < PresetsSeconds.Length; i++)
        {
            if (PresetsSeconds[i] == currentSeconds)
            {
                return PresetsSeconds[(i + 1) % PresetsSeconds.Length];
            }
        }
        // Not a preset: 0 is Never (nothing is longer), otherwise the next longer
        // preset, falling through to Never for values beyond the largest preset.
        if (currentSeconds == 0)
        {
            return PresetsSeconds[0];
        }
        foreach (var preset in PresetsSeconds)
        {
            if (preset > currentSeconds)
            {
                return preset;
            }
        }
        return 0;
    }

    /// <summary>Human label for a timeout value ("5 min", "1 h", "Never").</summary>
    /// <param name="seconds">The timeout (0 = Never).</param>
    public static string Describe(int seconds) => seconds switch
    {
        0 => "Never",
        // A scheme can carry a sub-minute timeout (powercfg takes seconds, and OEM
        // tools write such values). Rounding those to "0 min" would read as off —
        // the opposite of a timeout that fires almost immediately.
        < 60 => "<1 min",
        < 3600 => $"{(seconds + 30) / 60} min",
        _ => seconds % 3600 == 0
            ? $"{seconds / 3600} h"
            // Invariant on purpose: the badge must not become "1,5 h" on a German OS.
            : (seconds / 3600.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " h",
    };

    private static (Guid Subgroup, Guid Setting, bool Dc) Locate(PowerTimeoutKind kind) => kind switch
    {
        PowerTimeoutKind.DisplayDc => (SubVideo, VideoIdle, true),
        PowerTimeoutKind.DisplayAc => (SubVideo, VideoIdle, false),
        PowerTimeoutKind.SleepDc => (SubSleep, StandbyIdle, true),
        _ => (SubSleep, StandbyIdle, false),
    };

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        try
        {
            scheme = PowerSchemes.Windows.ReadActive();
            return true;
        }
        catch (Win32Exception ex)
        {
            scheme = default;
            Log.Warn(ex.Message);
            return false;
        }
    }
}
