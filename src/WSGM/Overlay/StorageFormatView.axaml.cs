using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Tools storage page's format flow: pick a removable drive, confirm the erase and its
///     library name, then watch the shared format manager run. Adding a library folder reuses the
///     progress state.
/// </summary>
/// <remarks>
///     The flow only reports intent to the session-owned <see cref="SdFormatManager" />, which owns
///     every destructive step and its identity checks.
/// </remarks>
public partial class StorageFormatView : UserControl
{
    private SdFormatManager? _format;

    // The library name the confirm step will format with. Held here rather than in a TextBox: the
    // row is press-to-edit (see the XAML), matching the tab editor and card rename, and the
    // in-window keyboard owns the typing.
    private string _name = "";
    private FormatTargetEntry? _pendingTarget;

    /// <summary>Creates the flow; it shows nothing until a format manager is attached.</summary>
    public StorageFormatView()
    {
        InitializeComponent();
    }

    /// <summary>Whether a format manager is attached.</summary>
    internal bool Available => _format is not null;

    /// <summary>Raised by Cancel and Done to leave the page.</summary>
    internal event Action? CloseRequested;

    /// <summary>
    ///     Raised when the flow moves to its progress state. The confirmation keyboard can leave the
    ///     page scrolled to its bottom, where the terminal format message would otherwise be invisible.
    /// </summary>
    internal event Action? ScrollToTopRequested;

    /// <summary>Drives the shared removable-storage format manager.</summary>
    /// <param name="format">The controller-owned format manager, which outlives the window.</param>
    internal void Attach(SdFormatManager format)
    {
        _format = format;
        DataContext = format;
    }

    /// <summary>Shows the drive list for a new format.</summary>
    internal void ShowTargets()
    {
        FormatHeading.Text = "Format SD Card";
        ShowState(true, false, false);
    }

    /// <summary>Reads the removable drives again.</summary>
    internal void RefreshTargets()
    {
        _format?.Refresh();
    }

    /// <summary>Shows the progress of adding a library folder and starts it.</summary>
    /// <param name="path">The local folder to register as a Steam library.</param>
    /// <returns>The manager's add operation.</returns>
    internal Task AddLibraryAsync(string path)
    {
        return _format?.Libraries.AddLibraryAsync(path) ?? Task.CompletedTask;
    }

    /// <summary>Shows the progress state under the Add Steam Library heading.</summary>
    internal void ShowAddLibrary()
    {
        FormatHeading.Text = "Add Steam Library";
        ShowState(false, false, true);
    }

    /// <summary>Forgets the drive chosen for confirmation when the page is left.</summary>
    internal void ForgetTarget()
    {
        _pendingTarget = null;
    }

    private void SetName(string value)
    {
        // Shown on its row so the value is visible without focusing anything, the way every other
        // name row in the panel reads.
        _name = value;
        FormatNameButton.Description = _name.Length > 0 ? _name : "(required)";
    }

    private void OnEditName(object? sender, RoutedEventArgs e)
    {
        if (!OverlayWindow.RequestTextFor(this, "Name (volume and Steam library)", _name,
                SdFormatManager.MaximumLabelLength, SetName))
        {
            // No keyboard surface means no way to type on a controller; say so instead
            // of leaving a row that silently does nothing when pressed.
            Log.Warn("Format: no on-screen keyboard available for the library name.");
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        _format?.Refresh();
    }

    private void OnTargetChosen(object? sender, RoutedEventArgs e)
    {
        if (_format is null || _format.Progress.Busy
                            || (sender as Control)?.DataContext is not FormatTargetEntry entry)
        {
            return;
        }

        _pendingTarget = entry;
        FormatConfirmTarget.Text = $"Erase {entry.Name}?";
        FormatConfirmDetail.Text = entry.Detail;
        SetName(SdFormatManager.DefaultLabel);
        ShowState(false, true, false);
        FocusSearch.FirstNavigable(FormatConfirmView)?.Focus(NavigationMethod.Directional);
    }

    private void OnConfirmed(object? sender, RoutedEventArgs e)
    {
        Log.Observe(FormatAsync(), "SD-card format");
    }

    private async Task FormatAsync()
    {
        if (_format is null || _pendingTarget is null)
        {
            return;
        }

        var target = _pendingTarget;
        var name = _name;
        ShowState(false, false, true);
        ScrollToTopRequested?.Invoke();
        await _format.FormatAsync(target, name);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }

    private void ShowState(bool pick, bool confirm, bool progress)
    {
        FormatPickView.IsVisible = pick;
        FormatConfirmView.IsVisible = confirm;
        FormatProgressView.IsVisible = progress;
    }
}
