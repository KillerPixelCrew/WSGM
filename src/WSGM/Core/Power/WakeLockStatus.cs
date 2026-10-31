using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Pure mapping from a power-request snapshot to the indicator state and
///     a compact holder summary for the quick-access row.
/// </summary>
public static class WakeLockStatus
{
    private const int MaxNamedHolders = 3;

    /// <summary>Words why a power-request read gave no trustworthy list; null when the list was read.</summary>
    /// <param name="status">The outcome of <see cref="PowerRequestList.Query" />.</param>
    /// <param name="nativeStatus">The NTSTATUS that accompanies a failed query.</param>
    /// <returns>A diagnostic for unavailable data, or null for a readable snapshot.</returns>
    public static string? DescribeQueryStatus(PowerRequestListStatus status, int nativeStatus)
    {
        return status switch
        {
            PowerRequestListStatus.Read => null,
            PowerRequestListStatus.AccessDenied => "Administrator rights required",
            PowerRequestListStatus.QueryFailed => $"Query failed (NTSTATUS 0x{(uint)nativeStatus:X8})",
            PowerRequestListStatus.Unsupported => "Not readable from a 32-bit process",
            _ => "Unrecognized power request layout"
        };
    }

    /// <summary>
    ///     Computes the indicator state and a holder summary such as
    ///     "Standby blocked by steam.exe ×3, chrome.exe". WSGM's own requests count
    ///     toward the state (the color must reflect reality) but are excluded from the
    ///     summary — the row's own description already explains WSGM's holds.
    /// </summary>
    /// <param name="entries">The decoded request list; null = unknown.</param>
    /// <param name="selfPid">WSGM's own process id, excluded from the summary.</param>
    /// <returns>
    ///     The display-prioritized state and a summary naming up to three external holders; unknown/free may have no
    ///     summary.
    /// </returns>
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

    /// <summary>
    ///     Shortens an NT-device-form image path to its file name; kernel
    ///     requesters without a name become "(kernel)".
    /// </summary>
    /// <param name="entry">Decoded requester whose native image name is used.</param>
    /// <returns>The final path component, or (kernel) when no name was provided.</returns>
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
