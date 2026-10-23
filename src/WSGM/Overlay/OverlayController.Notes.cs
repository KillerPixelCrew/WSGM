using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    private static readonly TimeSpan NotesCloseWait = TimeSpan.FromSeconds(3);

    /// <summary>Connects a newly opened sheet to the session's Notes owner.</summary>
    /// <param name="overlay">The sheet.</param>
    /// <remarks>
    ///     Opening the sheet while a screenshot countdown runs is how the player cancels it: they came back to
    ///     the overlay instead of preparing the game. The page they were on is intact and shows the cancellation.
    /// </remarks>
    private void AttachNotes(OverlayWindow overlay)
    {
        var notes = _sources.Notes;
        overlay.AttachNotes(notes);
        if (notes is null)
        {
            return;
        }

        if (notes.Capture.Cancel("Screenshot cancelled: the overlay was opened during the countdown."))
        {
            Log.Info("Notes screenshot cancelled by reopening the overlay.");
        }

        if (_previewOnly)
        {
            // The Settings preview and --overlay-test never leave the sheet for a game.
            return;
        }

        notes.OverlayCapturePorts ??= new NotesCapturePorts(new OverlayNotesSurface(this),
            notes.Countdown ??= new NotesCountdown(),
            (target, token) => NotesGameCapture.CaptureAsync(target, notes.SteamOverlayOpen, token),
            NotesGameCapture.IsAlive);
        overlay.NotesHost.StartCapture = (pageId, seconds) => StartNotesCapture(notes, pageId, seconds);
    }

    private SteamUiCommandResult StartNotesCapture(NotesService notes, string pageId, int seconds)
    {
        // The window in front when the sheet opened: the game the player came from.
        if (NotesGameCapture.DescribeTarget(_restoreFocusTo) is not { } target)
        {
            return new SteamUiCommandResult(false,
                "No game window to capture. Open the overlay from the running game, then add the screenshot.");
        }

        return notes.StartCapture(NotesCaptureOrigin.Overlay, pageId, seconds, target);
    }

    private async Task<bool> ReturnToGameForNotesAsync(NotesCaptureTarget target, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The sheet's own close releases its capture claim and Steam Input lease, returning input to the
            // game; in Game Mode it also brings back the window that was in front before it opened.
            _suppressFocusRestore = false;
            CloseOverlay();
        });
        var deadline = DateTime.UtcNow + NotesCloseWait;
        while (await Dispatcher.UIThread.InvokeAsync(() => _overlay is not null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        if (await Dispatcher.UIThread.InvokeAsync(() => _overlay is not null))
        {
            return false;
        }

        if (target.Window != 0 && NativeMethods.IsWindow(target.Window))
        {
            NativeMethods.GetWindowThreadProcessId(target.Window, out var owner);
            if (owner == target.ProcessId && NativeMethods.GetForegroundWindow() != target.Window)
            {
                WindowFinder.BringToForeground(target.Window);
            }
        }

        return true;
    }

    private Task<bool> ReopenNotesAsync()
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return false;
            }

            ShowOverlay();
            return _overlay?.OpenNotes() == true;
        }).GetTask();
    }

    private void DisposeNotes()
    {
        _sources.Notes?.OverlayCapturePorts = null;
    }

    /// <summary>Leaves and returns to the WSGM overlay for a Notes screenshot.</summary>
    private sealed class OverlayNotesSurface(OverlayController owner) : INotesCaptureSurface
    {
        public Task<bool> ReturnToGameAsync(NotesCaptureTarget target, CancellationToken cancellationToken)
        {
            return owner.ReturnToGameForNotesAsync(target, cancellationToken);
        }

        public Task<bool> ReopenAsync(NotesCaptureTarget target, string pageId, CancellationToken cancellationToken)
        {
            return owner.ReopenNotesAsync();
        }
    }
}
