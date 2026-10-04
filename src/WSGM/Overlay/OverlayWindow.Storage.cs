using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    // The library name the confirm step will format with. Held here rather than in a
    // TextBox: the row is press-to-edit (see the XAML), matching the tab editor and
    // card rename, and the in-window keyboard owns the typing.
    private string _formatName = "";

    /// <summary>
    ///     Shows the name on its row so the value is visible without focusing
    ///     anything, the way every other name row in the panel reads.
    /// </summary>
    private void SetFormatName(string value)
    {
        _formatName = value;
        FormatNameButton.Description = _formatName.Length > 0 ? _formatName : "(required)";
    }

    // The library name uses this window's keyboard surface.
    private void OnFormatEditName(object? sender, RoutedEventArgs e)
    {
        if (!RequestText("Name (volume and Steam library)",
                _formatName, 32, SetFormatName))
        {
            // No keyboard surface means no way to type on a controller; say so instead
            // of leaving a row that silently does nothing when pressed.
            Log.Warn("Format: no on-screen keyboard available for the library name.");
        }
    }

    private void OnFormatSdCard(object? sender, RoutedEventArgs e)
    {
        if (_format is null)
        {
            return;
        }

        FormatHeading.Text = "Format SD Card";
        ShowFormatState(true, false, false);
        OpenStorageFormat();
        _format.Refresh();
    }

    private void OnFormatRefresh(object? sender, RoutedEventArgs e)
    {
        _format?.Refresh();
    }

    private void OnFormatTargetChosen(object? sender, RoutedEventArgs e)
    {
        if (_format is null || _format.Busy
                            || (sender as Control)?.DataContext is not FormatTargetEntry entry)
        {
            return;
        }

        _pendingTarget = entry;
        FormatConfirmTarget.Text = $"Erase {entry.Name}?";
        FormatConfirmDetail.Text = entry.Detail;
        SetFormatName(SdFormatManager.DefaultLabel);
        ShowFormatState(false, true, false);
        FocusFirstControl(FormatConfirmView);
    }

    private void OnFormatConfirmed(object? sender, RoutedEventArgs e)
    {
        Log.Observe(FormatConfirmedAsync(), "SD-card format");
    }

    private async Task FormatConfirmedAsync()
    {
        if (_format is null || _pendingTarget is null)
        {
            return;
        }

        var target = _pendingTarget;
        var name = _formatName;
        ShowFormatState(false, false, true);
        ScrollFormatToTop();
        await _format.FormatAsync(target, name);
    }

    private void OnFormatCancel(object? sender, RoutedEventArgs e)
    {
        LeaveFormatSubViewToOrigin();
    }

    /// <summary>Opens the Game Library sub-view on its current state, including an apply in progress.</summary>
    private void OnGameLibrary(object? sender, RoutedEventArgs e)
    {
        EnterSubView(OverlayPage.SteamGameLibrary);
    }

    /// <summary>
    ///     Opens the Library Tabs builder sub-view (the gamepad-driven
    ///     custom-tab UI). Its own "Sync now" materializes the tabs.
    /// </summary>
    private void OnLibraryTabs(object? sender, RoutedEventArgs e)
    {
        LibraryTabsHost.Open();
        EnterSubView(OverlayPage.SteamLibraryTabs);
    }

    /// <summary>Opens the SD-card library manager sub-view.</summary>
    private void OnCardManager(object? sender, RoutedEventArgs e)
    {
        CardManagerHost.Open();
        EnterSubView(OverlayPage.SteamCardManager);
    }

    private void OpenStorageFormat()
    {
        SelectDestination(OverlayDestination.System);
        SelectWorkspaceSection(_workspaceSections.Find(section => section.Page == OverlayPage.SystemStorage)!, false);
        EnterSubView(OverlayPage.SteamStorageFormat);
    }

    private void LeaveFormatSubViewToOrigin()
    {
        LeaveSubView(OverlayPage.SteamStorageFormat);
    }

    private void OnAddLibrary(object? sender, RoutedEventArgs e)
    {
        Log.Observe(AddLibraryAsync(), "Steam library folder picker");
    }

    private async Task AddLibraryAsync()
    {
        if (_format is null)
        {
            return;
        }

        // A native folder picker: for network shares / second internal drives on
        // DIY Steam machines, where the user has a pointer. Not gamepad-driven —
        // the format flow is the controller-only path.
        IReadOnlyList<IStorageFolder> folders;
        SystemDialogActive?.Invoke(true);
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder for the Steam library",
                AllowMultiple = false
            });
        }
        finally
        {
            SystemDialogActive?.Invoke(false);
        }

        if (_closed || folders.Count == 0)
        {
            return;
        }

        var path = folders[0].Path is { IsAbsoluteUri: true, IsFile: true }
            ? folders[0].Path.LocalPath
            : null;
        if (string.IsNullOrEmpty(path))
        {
            Log.Warn("Add library: picked folder has no local path (a network location "
                     + "without a mapped drive?).");
            return;
        }

        FormatHeading.Text = "Add Steam Library";
        ShowFormatState(false, false, true);
        OpenStorageFormat();
        await _format.AddLibraryAsync(path);
    }

    private void ShowFormatState(bool pick, bool confirm, bool progress)
    {
        FormatPickView.IsVisible = pick;
        FormatConfirmView.IsVisible = confirm;
        FormatProgressView.IsVisible = progress;
    }

    /// <summary>
    ///     Brings a controller-focused control into the overlay viewport.
    ///     Directional focus navigation does not raise this request on its own, so
    ///     without it the lower keyboard rows could be focused off-screen.
    /// </summary>
    private void OnContentGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not Control control || control is ScrollViewer)
        {
            return;
        }

        control.BringIntoView();
        if (AnySubView || control.Tag is not string semanticKey)
        {
            return;
        }

        _session.Focus.Remember(
            _navigation.Destination,
            semanticKey,
            ContentScroller.Offset.Y);
    }

    /// <summary>
    ///     Returns the format flow to its heading when its state changes.
    ///     The confirmation keyboard can leave the scroller at its bottom, where the
    ///     terminal format message would otherwise be invisible.
    /// </summary>
    private void ScrollFormatToTop()
    {
        ContentScroller.Offset = new Vector(0, 0);
    }
}
