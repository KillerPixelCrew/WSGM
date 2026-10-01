using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
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

        _windowReturnCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _windowReturnCancellation = cancellation;
        Log.Info($"Open apps: focusing '{entry.Title}'.");
        _suppressFocusRestore = true;
        Log.Observe(PickWindowAsync(entry, window, cancellation), "Open apps activation");
        CloseOverlay(true);
    }

    /// <summary>Hands the user from the overlay's Game Library to a page inside Steam.</summary>
    /// <param name="target">Which page.</param>
    /// <remarks>
    ///     The same order as picking a window: the page is asked for, the sheet closes, and only once
    ///     it has closed and the input lease is back does Steam get the focus. A bare dismissal
    ///     returns focus to whatever had it before, which is often not Steam.
    /// </remarks>
    private void OpenGameLibraryInSteam(GameLibrarySteamTarget target)
    {
        if (_disposed || _overlay is not { } window || OpenInSteam is not { } open)
        {
            return;
        }

        _windowReturnCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _windowReturnCancellation = cancellation;
        _suppressFocusRestore = true;
        var navigation = open(target, cancellation.Token);
        Log.Observe(OpenGameLibraryInSteamAsync(navigation, window, cancellation), "Game Library hand-off");
        CloseOverlay(true);
    }

    private async Task OpenGameLibraryInSteamAsync(Task<bool> navigation, OverlayWindow window,
        CancellationTokenSource cancellation)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += OnClosed;
        try
        {
            var navigated = await navigation.WaitAsync(cancellation.Token);
            await closed.Task.WaitAsync(cancellation.Token);
            await _leaseRelease.WaitAsync(cancellation.Token);

            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            if (!navigated)
            {
                // Steam still gets the focus: the user asked to go there, and the page is one press
                // away from where they land.
                Log.Warn("Game Library: Steam did not take the requested page; focusing Steam as it is.");
            }

            _modes.FocusSteam();
        }
        catch (OperationCanceledException)
        {
        } // A reopened sheet or session shutdown ends the hand-off.
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

    private async Task PickWindowAsync(AppSwitcherEntry entry, OverlayWindow window,
        CancellationTokenSource cancellation)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += OnClosed;
        try
        {
            await closed.Task.WaitAsync(cancellation.Token);
            await _leaseRelease.WaitAsync(cancellation.Token);

            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            if (entry.IsSteam)
            {
                _modes.FocusSteam();
            }
            else if (GameReturn is { } gameReturn)
            {
                await gameReturn.ReturnAsync(entry.Hwnd, entry.ProcessId, cancellation.Token);
            }
            else
            {
                NativeMethods.GetWindowThreadProcessId(entry.Hwnd, out var pid);
                if (pid == entry.ProcessId)
                {
                    WindowFinder.BringToForeground(entry.Hwnd);
                }
            }
        }
        catch (OperationCanceledException)
        {
        } // A reopened sheet or session shutdown ends the return.
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
        RunOnUiThreadAfter(TaskManagerFocusInterval, () =>
        {
            // Only the real System32 Task Manager qualifies — never promote a
            // same-named exe running from elsewhere to the foreground.
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
            var pids = WindowFinder.FindProcessIds("Taskmgr");
            pids.RemoveWhere(pid => !WindowFinder.ProcessImagePathEquals(pid, expected));
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
        });
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
        if (_trayHost is not null)
        {
            _trayHost.IconsChanged -= OnTrayIconsChanged;
        }

        _trayHost = host;
        if (host is not null)
        {
            host.IconsChanged += OnTrayIconsChanged;
        }

        OnTrayIconsChanged();
    }

    private void OnTrayIconsChanged()
    {
        _switcherViewModel?.ReconcileTray(_trayHost?.Table.Icons ?? []);
    }

    /// <summary>
    ///     Queues an off-thread snapshot of the process/window tables, then reconciles the
    ///     Open apps chips on Avalonia's dispatcher. While the sheet is open the highlight uses the
    ///     captured pre-open foreground window.
    /// </summary>
    private void RefreshSwitcherEntries()
    {
        var viewModel = _switcherViewModel;
        if (viewModel is null || Interlocked.CompareExchange(ref _switcherRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var refreshSteamPids = _steamPidsAtUtc == default
                               || now - _steamPidsAtUtc >= TimeSpan.FromSeconds(5);
        HashSet<uint> cachedSteamPids = [.. _steamPids];
        var active = _overlay is { IsVisible: true }
            ? _restoreFocusTo
            : NativeMethods.GetForegroundWindow();
        Log.Observe(
            RefreshSwitcherEntriesAsync(
                viewModel,
                active,
                refreshSteamPids,
                cachedSteamPids,
                now),
            "Open apps refresh");
    }

    private async Task RefreshSwitcherEntriesAsync(
        AppSwitcherViewModel viewModel,
        nint active,
        bool refreshSteamPids,
        HashSet<uint> cachedSteamPids,
        DateTime requestedAtUtc)
    {
        try
        {
            // EnumWindows, DWM queries and the process-table snapshot are synchronous Win32 work.
            // Avalonia's dispatcher also owns pointer delivery and the 16 ms gamepad poll, so only
            // a detached snapshot returns to it.
            (HashSet<uint> SteamPids, IReadOnlyList<WindowFinder.AppWindow> Windows) snapshot =
                await Task.Run(() =>
                {
                    var steamPids = refreshSteamPids
                        ? WindowFinder.FindProcessIds(Steam.ProcessNames)
                        : cachedSteamPids;
                    return (steamPids, WindowFinder.ListSwitchableWindows());
                }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || !ReferenceEquals(_switcherViewModel, viewModel))
                {
                    return;
                }

                if (refreshSteamPids)
                {
                    _steamPids = snapshot.SteamPids;
                    _steamPidsAtUtc = requestedAtUtc;
                }

                viewModel.Reconcile(
                    snapshot.Windows,
                    active,
                    window => CreateSwitcherEntry(window, snapshot.SteamPids));
            }, DispatcherPriority.Background);
        }
        finally
        {
            Interlocked.Exchange(ref _switcherRefreshInFlight, 0);
        }
    }

    private AppSwitcherEntry CreateSwitcherEntry(
        WindowFinder.AppWindow window,
        HashSet<uint> steamPids)
    {
        // Cached icons are handed over synchronously; a miss resolves off the UI thread
        // (cross-process WM_GETICON probes plus a possible exe read) and lands in place.
        Bitmap? icon = null;
        if (_iconCache is not null && !_iconCache.TryGetCached(window.Hwnd, out icon))
        {
            _iconCache.ResolveInBackground(window.Hwnd, window.ProcessId, ApplyResolvedIcon);
        }

        return new AppSwitcherEntry(
                window.Hwnd,
                window.Title,
                steamPids.Contains(window.ProcessId),
                icon)
            { ProcessId = window.ProcessId };
    }

    /// <summary>
    ///     Places a background-resolved icon on its chip, if that chip is still on
    ///     the open sheet. Runs off the UI thread, so it marshals before touching view state.
    /// </summary>
    private void ApplyResolvedIcon(nint hwnd, Bitmap? icon)
    {
        if (icon is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // The window may have closed, or the sheet may have been dismissed and its
            // cache cleared, between the resolve starting and finishing.
            if (_switcherViewModel is null || _overlay is not { IsVisible: true })
            {
                return;
            }

            foreach (var entry in _switcherViewModel.Entries)
            {
                if (entry.Hwnd != hwnd)
                {
                    continue;
                }

                entry.Icon = icon;
                return;
            }
        });
    }

    /// <summary>
    ///     Keeps the open sheet's strip current (new/closed windows, titles,
    ///     minimize state) without disturbing the focused chip — Reconcile updates in place.
    /// </summary>
    private void StartSwitcherRefresh()
    {
        StopSwitcherRefresh();
        // Parameterless ctor + explicit Start; see the DispatcherTimer finding in
        // docs\overlay-and-input.md.
        _switcherRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _switcherRefresh.Tick += (_, _) => RefreshSwitcherEntries();
        _switcherRefresh.Start();
    }

    private void StopSwitcherRefresh()
    {
        _switcherRefresh?.Stop();
        _switcherRefresh = null;
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

        _trayHost?.SendClick(entry.Icon, contextMenu, anchor.X, anchor.Y);
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
