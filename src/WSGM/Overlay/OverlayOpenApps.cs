using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The open sheet's Open apps strip: the window chips with their icons, refreshed while the sheet shows,
///     and the game-mode tray icons beside them.
/// </summary>
/// <remarks>
///     The chips and their rasterized icons live only while the sheet is open. ShellSession owns the tray
///     host; this only follows its icons and forwards clicks. Use on the UI thread.
/// </remarks>
internal sealed class OverlayOpenApps
{
    private readonly Func<nint> _activeWindow;
    private readonly Func<bool> _disposed;
    private readonly Func<bool> _sheetShowing;
    private WindowIconCache? _iconCache;
    private HashSet<uint> _steamPids = [];
    private DateTime _steamPidsAtUtc;
    private DispatcherTimer? _switcherRefresh;
    private int _switcherRefreshInFlight;
    private TrayHost? _trayHost;

    /// <summary>Creates the strip's owner.</summary>
    /// <param name="activeWindow">The window the strip marks active: the one the sheet opened over while it shows.</param>
    /// <param name="sheetShowing">Whether the sheet shows, which a late icon needs to land on its chip.</param>
    /// <param name="disposed">Whether the owning controller has been disposed.</param>
    internal OverlayOpenApps(Func<nint> activeWindow, Func<bool> sheetShowing, Func<bool> disposed)
    {
        _activeWindow = activeWindow;
        _sheetShowing = sheetShowing;
        _disposed = disposed;
    }

    /// <summary>The open sheet's chips and tray icons, or null while it is closed.</summary>
    internal AppSwitcherViewModel? Switcher { get; private set; }

    /// <summary>Creates the strip for a sheet that is opening and reads the open windows and tray icons.</summary>
    /// <returns>The strip's view model.</returns>
    internal AppSwitcherViewModel Open()
    {
        // 48 px rasters downscale crisply into the chips' 18-DIP icons on high-DPI panels.
        _iconCache ??= new WindowIconCache(48);
        var switcher = new AppSwitcherViewModel();
        Switcher = switcher;
        Refresh();
        OnTrayIconsChanged();
        return switcher;
    }

    /// <summary>Drops the strip and its icons once the sheet has closed; the next open re-resolves them.</summary>
    internal void Close()
    {
        Stop();
        Switcher = null;
        // Free the rasterized icons with the sheet; the next open re-resolves.
        _iconCache?.Clear();
        // Same for the cached Steam pid set: the next session starts fresh.
        _steamPidsAtUtc = default;
    }

    /// <summary>
    ///     Attaches (or detaches, with null) the game-mode tray host whose
    ///     icons render in the sheet's bottom rail. ShellSession owns the host's
    ///     lifecycle — created per game-mode span, destroyed before explorer starts.
    /// </summary>
    /// <param name="host">The live tray host, or null when leaving game mode.</param>
    internal void AttachTrayHost(TrayHost? host)
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

    /// <summary>Forwards a tray icon's activation to the app that owns it.</summary>
    /// <param name="entry">The icon.</param>
    /// <param name="contextMenu">Whether the app should show its context menu.</param>
    /// <param name="x">The screen pixel the menu anchors to, horizontally.</param>
    /// <param name="y">The screen pixel the menu anchors to, vertically.</param>
    internal void SendTrayClick(TrayIconEntry entry, bool contextMenu, int x, int y)
    {
        _trayHost?.SendClick(entry.Icon, contextMenu, x, y);
    }

    /// <summary>
    ///     Queues an off-thread snapshot of the process/window tables, then reconciles the
    ///     Open apps chips on Avalonia's dispatcher. While the sheet is open the highlight uses the
    ///     captured pre-open foreground window.
    /// </summary>
    internal void Refresh()
    {
        var viewModel = Switcher;
        if (viewModel is null || Interlocked.CompareExchange(ref _switcherRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var refreshSteamPids = _steamPidsAtUtc == default
                               || now - _steamPidsAtUtc >= TimeSpan.FromSeconds(5);
        HashSet<uint> cachedSteamPids = [.. _steamPids];
        Log.Observe(
            RefreshAsync(
                viewModel,
                _activeWindow(),
                refreshSteamPids,
                cachedSteamPids,
                now),
            "Open apps refresh");
    }

    /// <summary>
    ///     Keeps the open sheet's strip current (new/closed windows, titles,
    ///     minimize state) without disturbing the focused chip — Reconcile updates in place.
    /// </summary>
    internal void Start()
    {
        Stop();
        // Parameterless ctor + explicit Start; see the DispatcherTimer finding in
        // docs\overlay-and-input.md.
        _switcherRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _switcherRefresh.Tick += (_, _) => Refresh();
        _switcherRefresh.Start();
    }

    /// <summary>Stops refreshing the strip.</summary>
    internal void Stop()
    {
        _switcherRefresh?.Stop();
        _switcherRefresh = null;
    }

    private void OnTrayIconsChanged()
    {
        Switcher?.ReconcileTray(_trayHost?.Table.Icons ?? []);
    }

    private async Task RefreshAsync(
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
            (HashSet<uint> SteamPids, IReadOnlyList<AppWindow> Windows) snapshot =
                await Task.Run(() =>
                {
                    var steamPids = refreshSteamPids
                        ? ProcessLookup.FindProcessIds(Steam.ProcessNames)
                        : cachedSteamPids;
                    return (steamPids, WindowFinder.ListSwitchableWindows());
                }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed() || !ReferenceEquals(Switcher, viewModel))
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
                    window => CreateEntry(window, snapshot.SteamPids));
            }, DispatcherPriority.Background);
        }
        finally
        {
            Interlocked.Exchange(ref _switcherRefreshInFlight, 0);
        }
    }

    private AppSwitcherEntry CreateEntry(
        AppWindow window,
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
            if (Switcher is null || !_sheetShowing())
            {
                return;
            }

            foreach (var entry in Switcher.Entries)
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
}
