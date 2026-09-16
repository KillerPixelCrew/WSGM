using System;
using System.Threading;
using System.Threading.Tasks;
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
    // card rename, and the peer keyboard window owns the typing.
    private string _formatName = "";

    /// <summary>Shows the name on its row so the value is visible without focusing
    /// anything, the way every other name row in the panel reads.</summary>
    private void SetFormatName(string value)
    {
        _formatName = value;
        FormatNameButton.Description = _formatName.Length > 0 ? _formatName : "(required)";
    }

    // Controller text entry for the library name goes through the peer keyboard
    // window (KeyboardService), like every other game-mode text field.
    private void OnFormatEditName(object? sender, RoutedEventArgs e)
    {
        if (!KeyboardService.Request("Name (volume and Steam library)",
                _formatName, 32, SetFormatName))
        {
            // No keyboard window means no way to type on a controller; say so instead
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
        ShowFormatState(pick: true, confirm: false, progress: false);
        EnterSubView(OverlayPage.SteamStorageFormat);
        _format.Refresh();
    }

    private void OnFormatRefresh(object? sender, RoutedEventArgs e) => _format?.Refresh();

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
        ShowFormatState(pick: false, confirm: true, progress: false);
        FocusFirstControl(FormatConfirmView);
    }

    private void OnFormatConfirmed(object? sender, RoutedEventArgs e) =>
        Log.Observe(FormatConfirmedAsync(), "SD-card format");

    private async Task FormatConfirmedAsync()
    {
        if (_format is null || _pendingTarget is null)
        {
            return;
        }
        var target = _pendingTarget;
        var name = _formatName;
        ShowFormatState(pick: false, confirm: false, progress: true);
        ScrollFormatToTop();
        await _format.FormatAsync(target, name);
    }

    private void OnFormatCancel(object? sender, RoutedEventArgs e) => LeaveFormatSubViewToOrigin();

    private LibraryTabManager LibraryTabs => field ??= new LibraryTabManager();

    // Debounce for the on-open auto-sync, shared across overlay instances (the
    // window is recreated per open). Auto-sync keeps card and category tabs current
    // without the user pressing the button; the button forces an immediate sync.
    private static long _lastAutoTabSyncTicks;
    private static readonly TimeSpan AutoTabSyncInterval = TimeSpan.FromMinutes(10);

    /// <summary>Opens the Library Tabs builder sub-view (the gamepad-driven
    /// custom-tab UI). Its own "Sync now" materializes the tabs.</summary>
    private void OnLibraryTabs(object? sender, RoutedEventArgs e)
    {
        LibraryTabsHost.Open(LibraryTabs);
        EnterSubView(OverlayPage.SteamLibraryTabs);
    }

    /// <summary>Opens the SD-card library manager sub-view.</summary>
    private void OnCardManager(object? sender, RoutedEventArgs e)
    {
        CardManagerHost.ShowFormat = _format is not null
            && DataContext is OverlayViewModel { ShowSdCard: true };
        CardManagerHost.Open(LibraryTabs);
        EnterSubView(OverlayPage.SteamCardManager);
    }

    /// <summary>Format picked from inside the Card Manager: hand the surface over to
    /// the format panel. Both are Steam nested pages, so the old one must be left first
    /// or two would claim the surface at once.</summary>
    private void OnFormatFromCardManager()
    {
        LeaveSubView(OverlayPage.SteamCardManager);
        OnFormatSdCard(this, new RoutedEventArgs());
        // Set AFTER entering: OnFormatSdCard runs the ordinary enter path, and
        // LeaveFormatSubView clears this on every exit.
        _formatReturnsToCards = true;
    }

    /// <summary>Whether leaving the format panel should land back in the Card Manager
    /// rather than the Steam root, because that is where the user opened it from.</summary>
    private bool _formatReturnsToCards;

    /// <summary>Cancel/Back out of the format panel, returning to whichever surface
    /// opened it. Re-opening the Card Manager also rescans, so a card that was just
    /// formatted shows up straight away.</summary>
    private void LeaveFormatSubViewToOrigin()
    {
        var toCards = _formatReturnsToCards;
        LeaveSubView(OverlayPage.SteamStorageFormat);
        if (toCards)
        {
            OnCardManager(this, new RoutedEventArgs());
        }
    }

    /// <summary>Opens the SteamGridDB artwork picker sub-view.</summary>
    private void OnChangeArtwork(object? sender, RoutedEventArgs e)
    {
        ArtworkHost.Open();
        EnterSubView(OverlayPage.SteamArtwork);
    }

    /// <summary>Fire-and-forget background sync when the overlay opens, throttled so
    /// it runs at most once per <see cref="AutoTabSyncInterval"/>. Best-effort — a
    /// closed Steam simply leaves the tabs for the next open.</summary>
    private void MaybeAutoSyncTabs()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAutoTabSyncTicks)
            < AutoTabSyncInterval.Ticks)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await LibraryTabs.SyncAllDetailedAsync();
                Log.Info($"Library tabs auto-sync: {result.Summary}");
                if (result.Success)
                {
                    Interlocked.Exchange(ref _lastAutoTabSyncTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Library tabs auto-sync failed: {ex.Message}");
            }
        });
    }

    private void OnAddLibrary(object? sender, RoutedEventArgs e) =>
        Log.Observe(AddLibraryAsync(), "Steam library folder picker");

    private async Task AddLibraryAsync()
    {
        if (_format is null)
        {
            return;
        }
        // A native folder picker: for network shares / second internal drives on
        // DIY Steam machines, where the user has a pointer. Not gamepad-driven —
        // the format flow is the controller-only path.
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose a folder for the Steam library",
                AllowMultiple = false
            });
        if (folders.Count == 0)
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
        ShowFormatState(pick: false, confirm: false, progress: true);
        EnterSubView(OverlayPage.SteamStorageFormat);
        await _format.AddLibraryAsync(path);
    }

    private void ShowFormatState(bool pick, bool confirm, bool progress)
    {
        FormatPickView.IsVisible = pick;
        FormatConfirmView.IsVisible = confirm;
        FormatProgressView.IsVisible = progress;
    }

    /// <summary>Brings a controller-focused control into the overlay viewport.
    /// Directional focus navigation does not raise this request on its own, so
    /// without it the lower keyboard rows could be focused off-screen.</summary>
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

    /// <summary>Returns the format flow to its heading when its state changes.
    /// The confirmation keyboard can leave the scroller at its bottom, where the
    /// terminal format message would otherwise be invisible.</summary>
    private void ScrollFormatToTop() => ContentScroller.Offset = new Vector(0, 0);
}
