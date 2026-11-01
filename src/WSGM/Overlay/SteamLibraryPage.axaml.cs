using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     Steam > Steam library: the library tab builder, the card manager and adding a library folder, which
///     continues in the storage format flow.
/// </summary>
public sealed partial class SteamLibraryPage : StackPanel
{
    private bool _closed;
    private StorageFormatView? _format;

    /// <summary>Builds the page; the window supplies the format flow with <see cref="Configure" />.</summary>
    public SteamLibraryPage()
    {
        InitializeComponent();
    }

    /// <summary>Raised with the nested page a row opens.</summary>
    internal event Action<OverlayPage>? PageRequested;

    /// <summary>Raised once a picked folder is handed to the format flow, which the window then shows.</summary>
    internal event Action? FormatPageRequested;

    /// <summary>Raised with true while the native folder picker owns the screen, and false once it closes.</summary>
    internal event Action<bool>? SystemDialogActive;

    /// <summary>Supplies the format flow that registers a picked folder as a library.</summary>
    /// <param name="format">The window's format flow.</param>
    internal void Configure(StorageFormatView format)
    {
        _format = format;
    }

    /// <summary>Drops a folder pick still in flight as the window closes.</summary>
    internal void Detach()
    {
        _closed = true;
    }

    private void OnOpenPage(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: OverlayPage page })
        {
            PageRequested?.Invoke(page);
        }
    }

    private void OnAddLibrary(object? sender, RoutedEventArgs e)
    {
        Log.Observe(AddLibraryAsync(), "Steam library folder picker");
    }

    private async Task AddLibraryAsync()
    {
        if (_format is not { Available: true } format || TopLevel.GetTopLevel(this) is not { } top)
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
            folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

        format.ShowAddLibrary();
        FormatPageRequested?.Invoke();
        await format.AddLibraryAsync(path);
    }
}
