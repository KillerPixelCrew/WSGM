using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>State for the quick access sheet's Open apps strip and tray area.</summary>
public sealed class AppSwitcherViewModel : ObservableObject
{
    /// <summary>
    ///     Application chips in first-seen order (stable across refreshes; new
    ///     windows append, closed windows drop out).
    /// </summary>
    public ObservableCollection<AppSwitcherEntry> Entries { get; } = [];

    /// <summary>
    ///     Gets or sets whether any application chip exists (drives the
    ///     empty-state hint).
    /// </summary>
    public bool HasEntries
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(HasEntries));
    }

    /// <summary>Tray-icon tiles (registration order, hidden icons filtered out).</summary>
    public ObservableCollection<TrayIconEntry> TrayIcons { get; } = [];

    /// <summary>Gets or sets whether the tray area (separator + icons) renders.</summary>
    public bool HasTrayIcons
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(HasTrayIcons));
    }

    /// <summary>
    ///     Reconciles the chip collection against a fresh enumeration without
    ///     disturbing surviving chips: updates title/minimized/active in place, removes
    ///     chips whose window is gone, appends chips for new windows. Pure with respect
    ///     to its inputs — the executable specification lives in the unit tests.
    /// </summary>
    /// <param name="fresh">The current switchable windows, enumeration order.</param>
    /// <param name="activeHwnd">The window considered foreground for highlighting.</param>
    /// <param name="create">Creates a chip for a newly appearing window.</param>
    public void Reconcile(
        IReadOnlyList<AppWindow> fresh,
        nint activeHwnd,
        Func<AppWindow, AppSwitcherEntry> create)
    {
        var byHwnd = new Dictionary<nint, AppWindow>(fresh.Count);
        foreach (var window in fresh)
        {
            // Duplicate handles cannot occur in one EnumWindows pass; TryAdd keeps
            // the first (top-most) occurrence robustly anyway.
            byHwnd.TryAdd(window.Hwnd, window);
        }

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (byHwnd.TryGetValue(entry.Hwnd, out var window)
                && (entry.ProcessId == 0 || entry.ProcessId == window.ProcessId))
            {
                byHwnd.Remove(entry.Hwnd);
                entry.Title = window.Title;
                entry.IsMinimized = window.IsMinimized;
                entry.IsActive = entry.Hwnd == activeHwnd;
            }
            else
            {
                Entries.RemoveAt(i);
            }
        }

        // Remaining map entries are new windows — append in enumeration order.
        foreach (var window in fresh)
        {
            if (!byHwnd.Remove(window.Hwnd))
            {
                continue;
            }

            var entry = create(window);
            entry.IsMinimized = window.IsMinimized;
            entry.IsActive = window.Hwnd == activeHwnd;
            Entries.Add(entry);
        }

        HasEntries = Entries.Count > 0;
    }

    /// <summary>
    ///     Reconciles the tray tiles against the host's live records — same
    ///     in-place discipline as the app chips (identity = record reference), so a
    ///     focused tray button survives unrelated changes.
    /// </summary>
    /// <param name="icons">The host's registered icons (hidden ones are filtered here).</param>
    public void ReconcileTray(IReadOnlyList<TrayIconRecord> icons)
    {
        List<TrayIconRecord> visible = [.. icons.Where(icon => !icon.IsHidden)];

        for (var i = TrayIcons.Count - 1; i >= 0; i--)
        {
            var entry = TrayIcons[i];
            if (visible.Remove(entry.Icon))
            {
                entry.Refresh();
            }
            else
            {
                TrayIcons.RemoveAt(i);
            }
        }

        foreach (var icon in visible)
        {
            TrayIcons.Add(new TrayIconEntry(icon));
        }

        HasTrayIcons = TrayIcons.Count > 0;
    }
}
