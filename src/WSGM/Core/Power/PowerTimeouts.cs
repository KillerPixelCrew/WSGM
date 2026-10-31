using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>One of the six user-facing idle timeouts of the active power scheme.</summary>
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

    /// <summary>Hibernate after (on battery); Modern Standby counts it from standby entry.</summary>
    HibernateDc,

    /// <summary>Hibernate after (plugged in); Modern Standby counts it from standby entry.</summary>
    HibernateAc
}

/// <summary>
///     Reads and writes the active power scheme's display-off, standby and hibernate idle
///     timeouts, located by Windows Device Control, through the flat powrprof policy API (locale-independent, unlike parsing
///     <c>powercfg /q</c>). Values are seconds; 0 means Never. Writes go to the active
///     scheme and are applied immediately via <c>PowerSetActiveScheme</c> — the same thing
///     the Settings app does, so Windows may later replace them (updates, OEM tools);
///     this is a convenience surface, not managed state WSGM must restore.
/// </summary>
/// <remarks>Writes take the scheme owner's mutation lock with every other machine-wide power change.</remarks>
/// <param name="schemes">The session's scheme owner, whose lock and active scheme every write uses.</param>
internal sealed class PowerTimeouts(PowerSchemes schemes)
{
    /// <summary>
    ///     The cycle presets offered by the quick-access rows, in click order;
    ///     0 (Never) sits last so cycling reads as "longer, longer, …, never, shortest".
    /// </summary>
    internal static readonly int[] PresetsSeconds = [60, 180, 300, 600, 900, 1800, 3600, 0];

    /// <summary>The scheme owner whose mutation lock these writes take.</summary>
    internal PowerSchemes Schemes => schemes;

    /// <summary>
    ///     Reads the timeout in seconds (0 = Never); null when the power API
    ///     fails (reported once by the caller's UI, never thrown).
    /// </summary>
    /// <param name="kind">Which timeout to read.</param>
    /// <returns>The timeout in seconds, zero for Never, or null for unavailable/out-of-range native values.</returns>
    public int? Read(PowerTimeoutKind kind)
    {
        if (!TryGetActiveScheme(out var scheme))
        {
            return null;
        }

        return Read(scheme, kind);
    }

    /// <summary>
    ///     Reads every timeout value after resolving the active power scheme once.
    /// </summary>
    /// <returns>
    ///     One independently nullable value in seconds per kind; all are null when the active scheme cannot be
    ///     read, and the hibernate values are null while the machine cannot hibernate.
    /// </returns>
    internal IReadOnlyDictionary<PowerTimeoutKind, int?> ReadAll()
    {
        var found = TryGetActiveScheme(out var scheme);
        var hibernation = found && HibernationAvailable();
        return Enum.GetValues<PowerTimeoutKind>().ToDictionary(kind => kind,
            kind => found ? Read(scheme, kind, hibernation) : null);
    }

    private int? Read(Guid scheme, PowerTimeoutKind kind)
    {
        return Read(scheme, kind, !IsHibernate(kind) || HibernationAvailable());
    }

    private int? Read(Guid scheme, PowerTimeoutKind kind, bool hibernation)
    {
        // A hibernate timeout on a machine that cannot hibernate does nothing, so it is not offered.
        if (IsHibernate(kind) && !hibernation)
        {
            return null;
        }

        var (subgroup, setting, dc) = Locate(kind);
        try
        {
            var value = schemes.ReadSetting(scheme, subgroup, setting, dc);
            return value <= int.MaxValue ? (int)value : null;
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"Power timeout read failed ({kind}, status {ex.NativeErrorCode}).");
            return null;
        }
    }

    /// <summary>
    ///     Writes the timeout (seconds, 0 = Never) into the active scheme and
    ///     re-activates the scheme so it takes effect immediately.
    /// </summary>
    /// <param name="kind">Which timeout to write.</param>
    /// <param name="seconds">Nonnegative timeout in seconds; zero means Never, negative values are rejected.</param>
    /// <returns>True when write and activation succeeded; false may follow an accepted write whose activation failed.</returns>
    public bool Write(PowerTimeoutKind kind, int seconds)
    {
        using (schemes.EnterMutation())
        {
            if (seconds < 0 || !TryGetActiveScheme(out var scheme))
            {
                return false;
            }

            var (subgroup, setting, dc) = Locate(kind);
            try
            {
                schemes.WriteSetting(scheme, subgroup, setting, dc, (uint)seconds);
                // Re-activating the scheme the value went to applies it at once.
                schemes.Select(scheme);
            }
            catch (Win32Exception ex)
            {
                Log.Warn($"Power timeout write failed ({kind} = {seconds} s, status {ex.NativeErrorCode}).");
                return false;
            }
        }

        Log.Info($"Power timeout set: {kind} = {Describe(seconds)}.");
        return true;
    }

    /// <summary>
    ///     The next preset after <paramref name="currentSeconds" /> in cycle
    ///     order. A value between presets snaps to the next longer one, so the first
    ///     click never shortens an unusual custom timeout. Pure for unit tests.
    /// </summary>
    /// <param name="currentSeconds">The current timeout (0 = Never).</param>
    /// <returns>The next cycle value in seconds, with zero representing Never.</returns>
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
        return currentSeconds == 0
            ? PresetsSeconds[0]
            : PresetsSeconds.FirstOrDefault(preset => preset > currentSeconds);
    }

    /// <summary>Human label for a timeout value ("5 min", "1 h", "Never").</summary>
    /// <param name="seconds">The timeout (0 = Never).</param>
    /// <returns>An invariant compact label; sub-minute values are shown as less than one minute.</returns>
    public static string Describe(int seconds)
    {
        return seconds switch
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
                : (seconds / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h"
        };
    }

    private static (Guid Subgroup, Guid Setting, bool Dc) Locate(PowerTimeoutKind kind)
    {
        var (subgroup, setting) = WindowsPower.LocateIdleTimeout(kind switch
        {
            PowerTimeoutKind.DisplayDc or PowerTimeoutKind.DisplayAc => WindowsIdleTimeout.DisplayOff,
            PowerTimeoutKind.SleepDc or PowerTimeoutKind.SleepAc => WindowsIdleTimeout.Standby,
            _ => WindowsIdleTimeout.Hibernate
        });
        return (subgroup, setting,
            kind is PowerTimeoutKind.DisplayDc or PowerTimeoutKind.SleepDc or PowerTimeoutKind.HibernateDc);
    }

    private static bool IsHibernate(PowerTimeoutKind kind)
    {
        return kind is PowerTimeoutKind.HibernateDc or PowerTimeoutKind.HibernateAc;
    }

    /// <summary>Whether a hibernate timeout would take effect; a failed query counts as unavailable.</summary>
    private static bool HibernationAvailable()
    {
        try
        {
            return WindowsPower.IsHibernationAvailable();
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"Hibernation availability could not be read (status {ex.NativeErrorCode}).");
            return false;
        }
    }

    private bool TryGetActiveScheme(out Guid scheme)
    {
        try
        {
            scheme = schemes.ReadActive();
            return true;
        }
        catch (Win32Exception ex)
        {
            scheme = Guid.Empty;
            Log.Warn(ex.Message);
            return false;
        }
    }
}
