using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WSGM.Core;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void OnFormatSdCard(object? sender, RoutedEventArgs e)
    {
        if (!StorageFormatHost.Available)
        {
            return;
        }

        StorageFormatHost.ShowTargets();
        OpenStorageFormat();
        StorageFormatHost.RefreshTargets();
    }

    private void OpenStorageFormat()
    {
        OpenToolsSection(OverlayPage.SystemStorage);
        EnterSubView(OverlayPage.SteamStorageFormat);
    }

    private void OnAddLibrary(object? sender, RoutedEventArgs e)
    {
        Log.Observe(AddLibraryAsync(), "Steam library folder picker");
    }

    private async Task AddLibraryAsync()
    {
        if (!StorageFormatHost.Available)
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

        StorageFormatHost.ShowAddLibrary();
        OpenStorageFormat();
        await StorageFormatHost.AddLibraryAsync(path);
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
}
