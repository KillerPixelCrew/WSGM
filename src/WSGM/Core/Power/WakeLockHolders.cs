using System;
using System.Collections.Generic;
using System.Linq;
using WindowsDeviceControl;

namespace WSGM.Core;

/// <summary>
///     Groups a power-request snapshot into the per-lock holder list shown by the
///     quick-access Power tab. Mirrors the maintainer's WakeWatch aggregation deliberately
///     (same tool, same vocabulary): dedupe on the identity a user perceives, so Steam's
///     thirty identical standby requests read as <c>steam.exe ×30</c> rather than thirty
///     rows.
///     <para>
///         Unlike <see cref="WakeLockStatus.Compute" /> this does NOT hide WSGM's own
///         requests: the summary line omits them because the row above already explains
///         WSGM's holds, but a user opening the full list is asking what is holding the
///         device awake and WSGM's own keep-awake hold is part of that answer.
///     </para>
/// </summary>
public static class WakeLockHolders
{
    /// <summary>
    ///     Builds the grouped holder list. Returns an empty list when the
    ///     snapshot is unknown (unelevated or an unrecognized layout) — callers must
    ///     distinguish that from "nothing holds a lock" using the null entries.
    /// </summary>
    /// <param name="entries">The decoded request list; null = unknown.</param>
    /// <returns>Nonempty lock-kind groups in display/system/away order; empty also represents unknown input.</returns>
    public static IReadOnlyList<WakeLockHolderGroup> Build(IReadOnlyList<PowerRequestEntry>? entries)
    {
        if (entries is null)
        {
            return [];
        }

        var groups = new List<WakeLockHolderGroup>();
        AddGroup(groups, "Screen kept on", entries.Where(e => e.HoldsDisplay));
        AddGroup(groups, "Standby blocked", entries.Where(e => e.HoldsSystem));
        AddGroup(groups, "Away mode", entries.Where(e => e.HoldsAwayMode));
        return groups;
    }

    private static void AddGroup(
        List<WakeLockHolderGroup> groups, string title, IEnumerable<PowerRequestEntry> matching)
    {
        var holders = new List<WakeLockHolder>();
        foreach (var entry in matching)
        {
            var label = WakeLockStatus.HolderName(entry);
            var detail = Describe(entry);
            var reason = string.IsNullOrWhiteSpace(entry.Reason) ? null : entry.Reason;
            var existing = holders.FindIndex(h =>
                string.Equals(h.Label, label, StringComparison.OrdinalIgnoreCase)
                && h.Detail == detail && h.Reason == reason);
            if (existing >= 0)
            {
                holders[existing] = holders[existing] with { Count = holders[existing].Count + 1 };
            }
            else
            {
                holders.Add(new WakeLockHolder(label, detail, reason, 1));
            }
        }

        if (holders.Count == 0)
        {
            return;
        }

        holders.Sort((a, b) => b.Count.CompareTo(a.Count) is var c && c != 0
            ? c
            : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        groups.Add(new WakeLockHolderGroup(title, holders));
    }

    /// <summary>
    ///     Formats the secondary line: caller kind, pid, and the full requester
    ///     name as the kernel reported it.
    /// </summary>
    /// <param name="entry">The request to describe.</param>
    /// <returns>A caller-kind, optional PID, and full-name description suitable for a secondary UI line.</returns>
    internal static string Describe(PowerRequestEntry entry)
    {
        // REQUESTER_TYPE: 0 kernel, 1 process, 2 service.
        if (entry.CallerType == 0)
        {
            return entry.Name.Length > 0 ? $"Driver: {entry.Name}" : "Kernel driver";
        }

        var kind = entry.CallerType == 2 ? "Service" : "Process";
        var name = entry.Name.Length > 0 ? entry.Name : "(unknown)";
        return entry.Pid is { } pid ? $"{kind} (pid {pid}): {name}" : $"{kind}: {name}";
    }
}
