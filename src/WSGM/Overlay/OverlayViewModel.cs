using System.Collections.ObjectModel;
using System.ComponentModel;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>One pickable top-level window in the Switch-app list.</summary>
public sealed class AppWindowEntry
{
    /// <summary>Creates a window entry from an enumerated native window.</summary>
    /// <param name="hwnd">The native window handle to activate.</param>
    /// <param name="title">The title presented in the picker.</param>
    /// <param name="isSteam">Whether the window belongs to the configured home app.</param>
    public AppWindowEntry(nint hwnd, string title, bool isSteam)
    {
        Hwnd = hwnd;
        Title = title;
        IsSteam = isSteam;
    }

    /// <summary>Gets the native window handle to activate.</summary>
    public nint Hwnd { get; }

    /// <summary>Gets the title presented to the user.</summary>
    public string Title { get; }

    /// <summary>Gets whether the window belongs to Steam.</summary>
    public bool IsSteam { get; }
}

/// <summary>State for the overlay, recomputed every time it is shown.</summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    /// <summary>Raised after an overlay property or dependent display value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _explorerRunning;
    private bool _homeAppAlive;
    private string _homeAppName = "Home app";
    private string _warningText = "";
    private GlyphStyle _glyphStyle = GlyphStyle.Xbox;

    /// <summary>Gets or sets whether Explorer is currently running.</summary>
    public bool ExplorerRunning
    {
        get => _explorerRunning;
        set { _explorerRunning = value; Raise(nameof(ExplorerRunning)); Raise(nameof(DesktopButtonText)); }
    }

    /// <summary>Gets or sets whether the configured home application has a live process.</summary>
    public bool HomeAppAlive
    {
        get => _homeAppAlive;
        set { _homeAppAlive = value; Raise(nameof(HomeAppAlive)); Raise(nameof(HomeAppButtonText)); }
    }

    /// <summary>Gets or sets the configured home application's display name.</summary>
    public string HomeAppName
    {
        get => _homeAppName;
        set { _homeAppName = value; Raise(nameof(HomeAppName)); Raise(nameof(HomeAppButtonText)); Raise(nameof(CloseLauncherText)); }
    }

    /// <summary>Gets or sets the non-fatal warning displayed by the overlay.</summary>
    public string WarningText
    {
        get => _warningText;
        set { _warningText = value; Raise(nameof(WarningText)); Raise(nameof(HasWarning)); }
    }

    /// <summary>Gets whether a warning should be rendered.</summary>
    public bool HasWarning => _warningText.Length > 0;

    /// <summary>Gets or sets the controller glyph family used by the overlay.</summary>
    public GlyphStyle GlyphStyle
    {
        get => _glyphStyle;
        set { _glyphStyle = value; Raise(nameof(GlyphStyle)); }
    }

    private bool _confirmingCloseLauncher;
    /// <summary>Armed state of the destructive Close-Steam confirm. Lives here so
    /// the bound title renders it — a direct Text write would fight the binding.</summary>
    public bool ConfirmingCloseLauncher
    {
        get => _confirmingCloseLauncher;
        set { _confirmingCloseLauncher = value; Raise(nameof(ConfirmingCloseLauncher)); Raise(nameof(CloseLauncherText)); }
    }

    /// <summary>Windows offered by the Switch-app picker (rebuilt on each press).</summary>
    public ObservableCollection<AppWindowEntry> SwitchableWindows { get; } = [];

    private bool _showWindowList;
    /// <summary>Gets or sets whether the Switch-app picker is visible.</summary>
    public bool ShowWindowList
    {
        get => _showWindowList;
        set { _showWindowList = value; Raise(nameof(ShowWindowList)); }
    }

    /// <summary>Gets the action label that switches between desktop and game mode.</summary>
    public string DesktopButtonText => ExplorerRunning ? "Back to Game Mode" : "Return to Desktop";

    /// <summary>Gets the action label that starts or focuses the home application.</summary>
    public string HomeAppButtonText => HomeAppAlive ? $"Focus {HomeAppName}" : $"Start {HomeAppName}";

    /// <summary>Gets the destructive-action label, including confirmation state.</summary>
    public string CloseLauncherText => ConfirmingCloseLauncher ? "Really?" : $"Close {HomeAppName}";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
