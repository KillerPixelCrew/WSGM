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
