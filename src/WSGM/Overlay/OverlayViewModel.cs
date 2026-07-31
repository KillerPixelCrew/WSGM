using System.Collections.ObjectModel;
using System.ComponentModel;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>One pickable window in the Switch-app list.</summary>
public sealed class AppWindowEntry(nint hwnd, string title, bool isSteam)
{
    public nint Hwnd { get; } = hwnd;
    public string Title { get; } = title;
    public bool IsSteam { get; } = isSteam;
}

/// <summary>State for the overlay, recomputed every time it is shown.</summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _explorerRunning;
    private bool _homeAppAlive;
    private string _homeAppName = "Home app";
    private string _warningText = "";
    private GlyphStyle _glyphStyle = GlyphStyle.Xbox;

    public bool ExplorerRunning
    {
        get => _explorerRunning;
        set { _explorerRunning = value; Raise(nameof(ExplorerRunning)); Raise(nameof(DesktopButtonText)); }
    }

    public bool HomeAppAlive
    {
        get => _homeAppAlive;
        set { _homeAppAlive = value; Raise(nameof(HomeAppAlive)); Raise(nameof(HomeAppButtonText)); }
    }

    public string HomeAppName
    {
        get => _homeAppName;
        set { _homeAppName = value; Raise(nameof(HomeAppName)); Raise(nameof(HomeAppButtonText)); Raise(nameof(CloseLauncherText)); }
    }

    public string WarningText
    {
        get => _warningText;
        set { _warningText = value; Raise(nameof(WarningText)); Raise(nameof(HasWarning)); }
    }

    public bool HasWarning => _warningText.Length > 0;

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
    public bool ShowWindowList
    {
        get => _showWindowList;
        set { _showWindowList = value; Raise(nameof(ShowWindowList)); }
    }

    public string DesktopButtonText => ExplorerRunning ? "Back to Game Mode" : "Return to Desktop";
    public string HomeAppButtonText => HomeAppAlive ? $"Focus {HomeAppName}" : $"Start {HomeAppName}";
    public string CloseLauncherText => ConfirmingCloseLauncher ? "Really?" : $"Close {HomeAppName}";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
