using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>How many times to look for Task Manager's window after starting it.</summary>
    /// <remarks>
    ///     Polled by image path rather than through the started process: Taskmgr is single-instance,
    ///     so the process ShellExecute starts may only hand over to one already running and exit.
    /// </remarks>
    private const int TaskManagerFocusAttempts = 12;

    private static readonly TimeSpan TaskManagerFocusInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>
    ///     Picking an Open apps chip dismisses the sheet and brings the app
    ///     forward (Steam via the UIPI-proof protocol). The switched-to window must stay
    ///     foreground, so the sheet's focus restore is suppressed; see the focus-restore finding in
    ///     <c>docs\overlay-and-input.md</c>.
    /// </summary>
    private void PickWindow(AppSwitcherEntry entry)
    {
        if (_disposed || _overlay is not { } window)
        {
            return;
        }

        Log.Info($"Open apps: focusing '{entry.Title}'.");
        RunAfterClose(window, "Open apps activation", async token =>
        {
            if (entry.IsSteam)
            {
                _modes.SteamLifecycle.Focus();
            }
            else if (GameReturn is { } gameReturn)
            {
                await gameReturn.ReturnAsync(entry.Hwnd, entry.ProcessId, token);
            }
            else
            {
                NativeMethods.GetWindowThreadProcessId(entry.Hwnd, out var pid);
                if (pid == entry.ProcessId)
                {
                    WindowFinder.BringToForeground(entry.Hwnd);
                }
            }
        });
    }

    /// <summary>
    ///     Closes the sheet and runs an activation once the window has closed and the input lease is
    ///     released, by default without restoring focus to the window the sheet came from.
    /// </summary>
    /// <param name="window">The open sheet.</param>
    /// <param name="operation">The name failures are logged under.</param>
    /// <param name="activate">The activation; its token ends when the sheet reopens or the session shuts down.</param>
    /// <param name="restoreFocus">
    ///     Whether the close still returns focus to the window the sheet came from, for an activation that acts on
    ///     that window, such as a hotkey sent to the game.
    /// </param>
    private void RunAfterClose(OverlayWindow window, string operation, Func<CancellationToken, Task> activate,
        bool restoreFocus = false)
    {
        _windowReturnCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _windowReturnCancellation = cancellation;
        _suppressFocusRestore = !restoreFocus;
        Log.Observe(RunAsync(), operation);
        CloseOverlay(true);
        return;

        async Task RunAsync()
        {
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += OnClosed;
            try
            {
                await closed.Task.WaitAsync(cancellation.Token);
                await _leaseRelease.WaitAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_disposed)
                {
                    await activate(cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            } // A reopened sheet or session shutdown ends the activation.
            finally
            {
                window.Closed -= OnClosed;
                if (ReferenceEquals(_windowReturnCancellation, cancellation))
                {
                    _windowReturnCancellation = null;
                }

                cancellation.Dispose();
            }

            return;

            void OnClosed(object? sender, EventArgs args)
            {
                closed.TrySetResult();
            }
        }
    }

    private static void StartTaskManager()
    {
        var taskmgr = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
        // ShellExecute-open: Taskmgr auto-elevates through its own manifest.
        if (!AppLauncher.Open(taskmgr).Started)
        {
            return;
        }

        Log.Info("Started Task Manager.");

        // It opens while our focused panel is closing, so the game underneath
        // reclaims the foreground and Task Manager lands behind it. Wait for
        // its window and promote it.
        FocusTaskManagerWhenVisible(1);
    }

    /// <summary>
    ///     Polls for the Task Manager window on the UI thread and promotes it to the
    ///     foreground once found.
    /// </summary>
    private static void FocusTaskManagerWhenVisible(int attempt)
    {
        DispatcherTimer.RunOnce(() =>
        {
            // Only the real System32 Task Manager qualifies — never promote a
            // same-named exe running from elsewhere to the foreground.
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
            var pids = ProcessLookup.FindProcessIds("Taskmgr");
            pids.RemoveWhere(pid => !ProcessLookup.ProcessImagePathEquals(pid, expected));
            var hwnd = WindowFinder.FindWindow(pids, null);
            if (hwnd != 0)
            {
                WindowFinder.BringToForeground(hwnd);
                return;
            }

            if (attempt >= TaskManagerFocusAttempts)
            {
                Log.Warn("Task Manager window not found to focus.");
                return;
            }

            FocusTaskManagerWhenVisible(attempt + 1);
        }, TaskManagerFocusInterval);
    }

    /// <summary>
    ///     The bottom-swipe entry: the sheet, with controller focus landing on
    ///     the Open apps strip rather than the selected root's first row — one gesture to
    ///     the running programs, which is what the bottom edge used to open.
    /// </summary>
    public void ShowOverlayOnOpenApps()
    {
        ShowOverlay();
        // Background priority: after the window's own Opened focus (DefaultFocusTarget)
        // AND the first layout pass, which is what realizes the chip buttons.
        Dispatcher.UIThread.Post(
            () => _overlay?.FocusOpenApps(), DispatcherPriority.Background);
    }

    /// <summary>
    ///     Opens the sheet on its Open apps strip, or closes it when it is up. This is
    ///     the OEM button's open apps action.
    /// </summary>
    public void ToggleOpenApps()
    {
        if (_overlay is null)
        {
            ShowOverlayOnOpenApps();
        }
        else
        {
            CloseOverlay();
        }
    }

    /// <summary>
    ///     Attaches (or detaches, with null) the game-mode tray host whose
    ///     icons render in the sheet's bottom rail. ShellSession owns the host's
    ///     lifecycle — created per game-mode span, destroyed before explorer starts.
    /// </summary>
    /// <param name="host">The live tray host, or null when leaving game mode.</param>
    public void AttachTrayHost(TrayHost? host)
    {
        _openApps.AttachTrayHost(host);
    }

    /// <summary>
    ///     Forwards a tray-icon activation to its owner. For context menus
    ///     the sheet additionally drops Topmost until it is next activated: WinForms tray menus shown
    ///     via plain Show() (Handheld Companion since its commit c86932bc) are
    ///     NON-topmost and never activated — over a topmost sheet they open BEHIND it,
    ///     which reads as "the menu doesn't appear" (device-reported).
    /// </summary>
    private void OnTrayIconActivated(TrayIconEntry entry, bool contextMenu, PixelPoint anchor)
    {
        if (contextMenu)
        {
            if (_overlay is not null)
            {
                _overlay.Topmost = false;
                _overlay.Activated -= RestoreTopmost;
                _overlay.Activated += RestoreTopmost;
            }
        }
        else
        {
            // A plain activation opens/shows the owning app — dismiss the sheet so it
            // comes forward (same rule as picking an Open apps chip). A context-menu
            // request keeps the sheet: the menu pops over it.
            _suppressFocusRestore = true;
            CloseOverlay();
        }

        _openApps.SendTrayClick(entry, contextMenu, anchor.X, anchor.Y);
    }

    private static void RestoreTopmost(object? sender, EventArgs e)
    {
        if (sender is OverlayWindow overlay)
        {
            overlay.Activated -= RestoreTopmost;
            overlay.Topmost = true;
        }
    }
}
