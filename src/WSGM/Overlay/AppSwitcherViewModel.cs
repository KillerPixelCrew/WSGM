using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     One application chip on the quick access sheet's Open apps strip.
///     Mutable presentation state is INPC so the 1 s refresh can update chips IN PLACE —
///     replacing the collection wholesale would destroy the focused button under the
///     gamepad cursor on every tick.
/// </summary>
public sealed class AppSwitcherEntry : ObservableObject
{
    private string _title;

    /// <summary>Creates a switcher chip for an enumerated window.</summary>
    /// <param name="hwnd">The native window handle to activate.</param>
    /// <param name="title">The window title (tooltip text).</param>
    /// <param name="isSteam">Whether the window belongs to Steam (activated via protocol).</param>
    /// <param name="icon">The rasterized application icon, or null for the fallback glyph.</param>
    public AppSwitcherEntry(nint hwnd, string title, bool isSteam, Bitmap? icon)
    {
        Hwnd = hwnd;
        _title = title;
        IsSteam = isSteam;
        Icon = icon;
    }

    /// <summary>Gets the native window handle to activate.</summary>
    public nint Hwnd { get; }

    /// <summary>Gets the process that owned this exact window when it was enumerated.</summary>
    public uint ProcessId { get; init; }

    /// <summary>Gets whether the window belongs to Steam.</summary>
    public bool IsSteam { get; }

    /// <summary>
    ///     Gets or sets the rasterized application icon (null renders the fallback
    ///     glyph). Settable because resolution runs off the UI thread: the tile is created
    ///     with whatever is cached and the icon lands here IN PLACE when it arrives — a
    ///     wholesale rebuild would destroy the button under the gamepad cursor.
    /// </summary>
    public Bitmap? Icon
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            Raise(nameof(Icon));
            Raise(nameof(HasNoIcon));
        }
    }

    /// <summary>Gets whether a fallback glyph should render instead of an icon.</summary>
    public bool HasNoIcon => Icon is null;

    /// <summary>Gets or sets the window title shown on the chip.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            Raise(nameof(Title));
        }
    }

    /// <summary>Gets or sets whether the window is currently minimized.</summary>
    public bool IsMinimized
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(IsMinimized));
    }

    /// <summary>
    ///     Gets or sets whether this window was foreground when the sheet opened
    ///     (or last refreshed) — the highlighted chip.
    /// </summary>
    public bool IsActive
    {
        get;
        set => SetFieldIfChanged(ref field, value, nameof(IsActive));
    }
}

/// <summary>
///     One tray icon tile. Wraps the host's live record; Refresh() re-raises
///     the bindable projections after a NIM_MODIFY.
/// </summary>
public sealed class TrayIconEntry : ObservableObject
{
    private object? _image;
    private string _tip;

    /// <summary>Creates a tile over a live tray-icon record.</summary>
    /// <param name="icon">The host's icon record.</param>
    public TrayIconEntry(TrayIconTable.TrayIcon icon)
    {
        Icon = icon;
        _image = icon.IconImage;
        _tip = icon.Tip;
    }

    /// <summary>Gets the underlying tray-icon record (click forwarding target).</summary>
    public TrayIconTable.TrayIcon Icon { get; }

    /// <summary>Gets the rasterized icon image.</summary>
    public Bitmap? Image => Icon.IconImage as Bitmap;

    /// <summary>Gets the tooltip text.</summary>
    public string Tip => Icon.Tip;

    /// <summary>Re-raises the projections after the underlying record changed.</summary>
    /// <remarks>
    ///     The tray host replaces an icon's bitmap rather than drawing into it, so a reference
    ///     change is a real change; unchanged tiles are not rebound on every reconcile.
    /// </remarks>
    public void Refresh()
    {
        if (!ReferenceEquals(_image, Icon.IconImage))
        {
            _image = Icon.IconImage;
            Raise(nameof(Image));
        }

        if (string.Equals(_tip, Icon.Tip, StringComparison.Ordinal))
        {
            return;
        }

        _tip = Icon.Tip;
        Raise(nameof(Tip));
    }
}

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
        IReadOnlyList<WindowFinder.AppWindow> fresh,
        nint activeHwnd,
        Func<WindowFinder.AppWindow, AppSwitcherEntry> create)
    {
        var byHwnd = new Dictionary<nint, WindowFinder.AppWindow>(fresh.Count);
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
    public void ReconcileTray(IReadOnlyList<TrayIconTable.TrayIcon> icons)
    {
        List<TrayIconTable.TrayIcon> visible = [.. icons.Where(icon => !icon.IsHidden)];

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
