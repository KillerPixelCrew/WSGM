using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>One application tile on the game-mode taskbar. Mutable presentation
/// state is INPC so the 1 s refresh can update tiles IN PLACE — replacing the
/// collection wholesale would destroy the focused button under the gamepad cursor
/// on every tick.</summary>
public sealed class TaskbarEntry : INotifyPropertyChanged
{
    /// <summary>Raised when a mutable presentation property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a taskbar tile for an enumerated window.</summary>
    /// <param name="hwnd">The native window handle to activate.</param>
    /// <param name="title">The window title (tooltip text).</param>
    /// <param name="isSteam">Whether the window belongs to Steam (activated via protocol).</param>
    /// <param name="icon">The rasterized application icon, or null for the fallback glyph.</param>
    public TaskbarEntry(nint hwnd, string title, bool isSteam, Bitmap? icon)
    {
        Hwnd = hwnd;
        _title = title;
        IsSteam = isSteam;
        Icon = icon;
    }

    /// <summary>Gets the native window handle to activate.</summary>
    public nint Hwnd { get; }

    /// <summary>Gets whether the window belongs to Steam.</summary>
    public bool IsSteam { get; }

    /// <summary>Gets the rasterized application icon (null renders the fallback glyph).</summary>
    public Bitmap? Icon { get; }

    /// <summary>Gets whether a fallback glyph should render instead of an icon.</summary>
    public bool HasNoIcon => Icon is null;

    private string _title;
    /// <summary>Gets or sets the window title shown as the tile's tooltip.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                Raise(nameof(Title));
            }
        }
    }

    private bool _isMinimized;
    /// <summary>Gets or sets whether the window is currently minimized.</summary>
    public bool IsMinimized
    {
        get => _isMinimized;
        set
        {
            if (_isMinimized != value)
            {
                _isMinimized = value;
                Raise(nameof(IsMinimized));
            }
        }
    }

    private bool _isActive;
    /// <summary>Gets or sets whether this window was foreground when the bar opened
    /// (or last refreshed) — the highlighted tile.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                Raise(nameof(IsActive));
            }
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>State for the game-mode taskbar window.</summary>
public sealed class TaskbarViewModel : INotifyPropertyChanged
{
    /// <summary>Raised after a taskbar property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Application tiles in first-seen order (stable across refreshes; new
    /// windows append, closed windows drop out).</summary>
    public ObservableCollection<TaskbarEntry> Entries { get; } = [];

    private bool _hasEntries;
    /// <summary>Gets or sets whether any application tile exists (drives the
    /// empty-state hint).</summary>
    public bool HasEntries
    {
        get => _hasEntries;
        set
        {
            if (_hasEntries != value)
            {
                _hasEntries = value;
                Raise(nameof(HasEntries));
            }
        }
    }

    /// <summary>Reconciles the tile collection against a fresh enumeration without
    /// disturbing surviving tiles: updates title/minimized/active in place, removes
    /// tiles whose window is gone, appends tiles for new windows. Pure with respect
    /// to its inputs — the executable specification lives in the unit tests.</summary>
    /// <param name="fresh">The current switchable windows, enumeration order.</param>
    /// <param name="activeHwnd">The window considered foreground for highlighting.</param>
    /// <param name="create">Creates a tile for a newly appearing window.</param>
    public void Reconcile(
        IReadOnlyList<WindowFinder.AppWindow> fresh,
        nint activeHwnd,
        Func<WindowFinder.AppWindow, TaskbarEntry> create)
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
            if (byHwnd.Remove(entry.Hwnd, out var window))
            {
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
            if (byHwnd.Remove(window.Hwnd))
            {
                var entry = create(window);
                entry.IsMinimized = window.IsMinimized;
                entry.IsActive = window.Hwnd == activeHwnd;
                Entries.Add(entry);
            }
        }

        HasEntries = Entries.Count > 0;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
