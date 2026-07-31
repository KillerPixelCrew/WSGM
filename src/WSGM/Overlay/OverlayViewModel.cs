using System.ComponentModel;
using WSGM.Core;

namespace WSGM.Overlay;

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

    /// <summary>Shown when startup apps are configured — without a taskbar the
    /// sidebar is the only way to switch between them.</summary>
    public bool ShowCycleButton { get; init; }

    public string DesktopButtonText => ExplorerRunning ? "Back to Game Mode" : "Return to Desktop";
    public string HomeAppButtonText => HomeAppAlive ? $"Focus {HomeAppName}" : $"Start {HomeAppName}";
    public string CloseLauncherText => $"Close {HomeAppName}";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
