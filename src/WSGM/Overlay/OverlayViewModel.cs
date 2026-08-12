using System.ComponentModel;
using WSGM.Core;

namespace WSGM.Overlay;

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

    /// <summary>Gets the action label that switches between desktop and game mode.</summary>
    public string DesktopButtonText => ExplorerRunning ? "Back to Game Mode" : "Return to Desktop";

    /// <summary>Gets the action label that starts or focuses the home application.</summary>
    public string HomeAppButtonText => HomeAppAlive ? $"Focus {HomeAppName}" : $"Start {HomeAppName}";

    /// <summary>Gets the destructive-action label, including confirmation state.</summary>
    public string CloseLauncherText => ConfirmingCloseLauncher ? "Really?" : $"Close {HomeAppName}";

    /// <summary>Whether the CEF library-tabs builder button is shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.LibraryTabs</c>). Set per show; a hidden button
    /// removes the only entry point to that CEF feature.</summary>
    public bool ShowLibraryTabs { get; init; } = true;

    /// <summary>Whether the CEF SD-card library-manager button is shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.CardManager</c>).</summary>
    public bool ShowCardManager { get; init; } = true;

    /// <summary>Whether the CEF shortcut-artwork button is shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.Artwork</c>).</summary>
    public bool ShowArtwork { get; init; } = true;

    /// <summary>Whether the CEF Format-SD-card and Add-library buttons are shown
    /// (<c>Cef.Enabled &amp;&amp; Cef.SdFormat</c>).</summary>
    public bool ShowSdCard { get; init; } = true;

    /// <summary>Whether the "STEAM LIBRARY" tools section has any visible button, so
    /// its header is hidden rather than left orphaned when every CEF feature is off.</summary>
    public bool ShowSteamLibrarySection =>
        ShowLibraryTabs || ShowCardManager || ShowArtwork || ShowSdCard;

    /// <summary>Whether the launch-wrapper buttons configure the selected game in the
    /// running Steam client (<c>Cef.Enabled</c>) instead of copying a command to the
    /// clipboard for the user to paste by hand.</summary>
    public bool ConfigureLaunchOptionsLive { get; init; } = true;

    /// <summary>Whether the "remove wrappers" row is shown — only meaningful when WSGM
    /// can write the launch configuration itself.</summary>
    public bool ShowRemoveLaunchWrapper => ConfigureLaunchOptionsLive;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
