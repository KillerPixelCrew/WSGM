using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>The WakeWatch color vocabulary for the system-wide wake-lock state
/// (maintainer's WakeWatch project, reused deliberately so both tools read the
/// same): grey = unknown, green = free, yellow = standby blocked, red = display
/// pinned on. Only DISPLAY drives red and SYSTEM/AWAYMODE drive yellow; EXECUTION
/// and friends deliberately do not affect the state.</summary>
public enum WakeLockState
{
    /// <summary>No trustworthy answer (unelevated, or an unrecognized layout).</summary>
    Unknown,

    /// <summary>No locks — display and sleep are free.</summary>
    Free,

    /// <summary>At least one standby lock: the system cannot sleep.</summary>
    SystemHeld,

    /// <summary>At least one display lock: the screen cannot turn off.</summary>
    DisplayHeld,
}

/// <summary>The quick-access Keep Awake cycle: off → block standby → block standby
/// and keep the display on → off.</summary>
public enum ManualWakeMode
{
    /// <summary>No manual hold.</summary>
    Off,

    /// <summary>Standby blocked; the display still times out.</summary>
    Standby,

    /// <summary>Standby blocked and the display pinned on.</summary>
    StandbyAndDisplay,
}

/// <summary>Pure mapping from a power-request snapshot to the indicator state and
/// a compact holder summary for the quick-access row.</summary>
public static class WakeLockStatus
{
    private const int MaxNamedHolders = 3;

    /// <summary>Computes the indicator state and a holder summary such as
    /// "Standby blocked by steam.exe ×3, chrome.exe". WSGM's own requests count
    /// toward the state (the color must reflect reality) but are excluded from the
    /// summary — the row's own description already explains WSGM's holds.</summary>
    /// <param name="entries">The decoded request list; null = unknown.</param>
    /// <param name="selfPid">WSGM's own process id, excluded from the summary.</param>
    public static (WakeLockState State, string Summary) Compute(
        IReadOnlyList<PowerRequestEntry>? entries, uint selfPid)
    {
        if (entries is null)
        {
            return (WakeLockState.Unknown, "");
        }
        var display = entries.Any(e => e.HoldsDisplay);
        var system = entries.Any(e => e.HoldsSystem || e.HoldsAwayMode);
        if (!display && !system)
        {
            return (WakeLockState.Free, "");
        }
        var state = display ? WakeLockState.DisplayHeld : WakeLockState.SystemHeld;
        var holders = entries
            .Where(e => (display ? e.HoldsDisplay : e.HoldsSystem || e.HoldsAwayMode)
                && e.Pid != selfPid)
            .Select(HolderName)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key)
            .ToList();
        if (holders.Count == 0)
        {
            return (state, "");
        }
        var listed = string.Join(", ", holders.Take(MaxNamedHolders));
        if (holders.Count > MaxNamedHolders)
        {
            listed += $" +{holders.Count - MaxNamedHolders} more";
        }
        return (state, (display ? "Screen held on by " : "Standby blocked by ") + listed);
    }

    /// <summary>Shortens an NT-device-form image path to its file name; kernel
    /// requesters without a name become "(kernel)".</summary>
    internal static string HolderName(PowerRequestEntry entry)
    {
        var name = entry.Name;
        var cut = name.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }
        return name.Length > 0 ? name : "(kernel)";
    }
}
