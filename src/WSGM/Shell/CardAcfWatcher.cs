using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Event-based badge/tab freshness: watches <c>SteamLibrary\steamapps</c> on every ready drive
///     carrying a Steam library marker for <c>appmanifest_*.acf</c> create/delete/rename
///     and triggers a debounced full sync, so installing or removing a game on a card
///     updates its tab and in-page badge without the user opening the overlay.
///     Only file-NAME events are watched: an install creates its appmanifest immediately
///     and an uninstall deletes it, while download progress only rewrites file contents —
///     so a multi-gigabyte download does not re-sync every few seconds.
///     Mounts change (insert/eject/format), so the watcher set is reconciled on the same
///     volume arrival/removal notification <see cref="CardVolumeMonitor" /> reacts to.
///     The watchers hold directory handles on the cards, which would make WSGM veto its
///     own Safe Eject (FSCTL_LOCK_VOLUME needs the volume otherwise unopened) — the eject
///     and format flows hold a <see cref="Suspend" /> scope for their whole run, and
///     reconciliation stays away until the last scope ends.
/// </summary>
internal sealed class CardAcfWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private readonly SteamUiReadiness _readiness;
    private readonly Timer _reconcile;
    private readonly bool _registered;
    private readonly SteamClient _steam;
    private readonly ConfigStore _store;
    private readonly CancellationToken _token;

    private readonly Dictionary<char, FileSystemWatcher> _watchers = new();
    private readonly MessageWindow _window;

    /// <summary>
    ///     1 while a deferred boot sync is waiting or running. Its wait outlives many
    ///     debounce ticks, and overlapping syncs would drive concurrent CEF mutation.
    /// </summary>
    private int _bootSyncPending;

    private Timer? _debounce;
    private bool _disposed;
    private int _suspensions;

    private CardAcfWatcher(MessageWindow window, ConfigStore store, SteamClient steam, SteamUiReadiness readiness)
    {
        _store = store;
        _steam = steam;
        _readiness = readiness;
        _token = _cts.Token;
        _window = window;
        _reconcile = new Timer(_ => Reconcile(), null, Timeout.Infinite, Timeout.Infinite);
        _registered = window.RegisterVolumeNotifications();
        if (_registered)
        {
            window.VolumeChanged += OnVolumeChanged;
        }
    }

    /// <summary>Stops watching and ends this watcher's volume notification claim. UI thread.</summary>
    public void Dispose()
    {
        if (_registered)
        {
            _window.VolumeChanged -= OnVolumeChanged;
            _window.DeregisterVolumeNotifications();
        }

        _cts.Cancel();
        lock (_gate)
        {
            _disposed = true;
            _reconcile.Dispose();
            _debounce?.Dispose();
            _debounce = null;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _cts.Dispose();
    }

    /// <summary>Creates the session's watcher and watches the cards already mounted. UI thread.</summary>
    /// <param name="window">The session's message window, which outlives this watcher.</param>
    /// <param name="store">The configuration store the card libraries are read from.</param>
    /// <param name="steam">The session's Steam client the tab syncs run through.</param>
    /// <param name="readiness">The session's Steam UI readiness, which a sync waits on.</param>
    internal static CardAcfWatcher StartNew(
        MessageWindow window, ConfigStore store, SteamClient steam, SteamUiReadiness readiness)
    {
        var watcher = new CardAcfWatcher(window, store, steam, readiness);
        watcher._reconcile.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        return watcher;
    }

    /// <summary>
    ///     Drops every directory handle before a Safe Eject or a format and keeps the
    ///     reconciler from re-opening one until the returned scope is disposed. Cards that
    ///     remain mounted are re-watched when the last scope ends.
    /// </summary>
    internal IDisposable Suspend()
    {
        lock (_gate)
        {
            _suspensions++;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            if (_watchers.Count > 0)
            {
                Log.Info("Card watcher: suspended for an eject or format.");
            }

            _watchers.Clear();
        }

        return new Suspension(this);
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (--_suspensions == 0 && !_disposed)
            {
                _reconcile.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnVolumeChanged(bool _)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                // The notification precedes the mount and its letter; settle first.
                _reconcile.Change(CardVolumeMonitor.SettleDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Reconcile()
    {
        try
        {
            var mounted = new HashSet<char>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    var root = Path.Combine(drive.Name, SteamLibraryVdf.CardFolderName);
                    if (File.Exists(Path.Combine(root, "libraryfolder.vdf"))
                        && Directory.Exists(Path.Combine(root, "steamapps")))
                    {
                        mounted.Add(char.ToUpperInvariant(drive.Name[0]));
                    }
                }
                catch
                {
                    // A vanishing drive mid-probe is normal (eject, card swap).
                }
            }

            lock (_gate)
            {
                if (_disposed || _suspensions > 0)
                {
                    return;
                }

                foreach (var letter in _watchers.Keys.Where(l => !mounted.Contains(l)).ToList())
                {
                    _watchers[letter].Dispose();
                    _watchers.Remove(letter);
                    Log.Info($"Card watcher: stopped watching {letter}: (unmounted).");
                }

                foreach (var letter in mounted.Where(l => !_watchers.ContainsKey(l)))
                {
                    TryWatch(letter);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Card watcher: reconcile failed: {ex.Message}");
        }
    }

    // Under _gate.
    private void TryWatch(char letter)
    {
        try
        {
            var path = $@"{letter}:\{SteamLibraryVdf.CardFolderName}\steamapps";
            var watcher = new FileSystemWatcher(path, "appmanifest_*.acf")
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => Debounce();
            watcher.Deleted += (_, _) => Debounce();
            watcher.Renamed += (_, _) => Debounce();
            // Buffer overflow or the drive vanished; the removal notification's
            // reconcile disposes or replaces the watcher.
            watcher.Error += (_, _) => Debounce();
            _watchers[letter] = watcher;
            Log.Info($"Card watcher: watching {path}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Card watcher: could not watch {letter}:: {ex.Message}");
        }
    }

    private void Debounce()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounce ??= new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(2000, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        _ = SyncAsync();
    }

    private async Task SyncAsync()
    {
        try
        {
            if (!_readiness.IsReady)
            {
                // SyncOnBootAsync waits for Big Picture's next ready edge. A card finishing
                // several installs during a cold boot fires one ACF event per game,
                // and without this gate each would start its own loop — they would
                // then all clear the readiness gate at once and run
                // SyncAllDetailedAsync concurrently against the same collectionStore,
                // which is exactly the interleaving LibraryTabManager's single-flight
                // discipline exists to prevent. A second event while one is pending is
                // dropped, not queued: the pending sync reads current state anyway.
                if (Interlocked.CompareExchange(ref _bootSyncPending, 1, 0) != 0)
                {
                    Log.Info("Card watcher: a deferred boot sync is already pending; skipping.");
                    return;
                }

                try
                {
                    Log.Info(
                        "Card watcher: Steam UI is still starting; deferring automatic tab sync.");
                    await LibraryTabManager.SyncOnBootAsync(_store, _steam, _readiness, _token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _bootSyncPending, 0);
                }

                return;
            }

            var summary = await LibraryTabManager.SyncAllAsync(_store, _steam, _token).ConfigureAwait(false);
            Log.Info($"Card watcher: {summary}");
        }
        catch (OperationCanceledException)
        {
            // Desktop transition or session shutdown.
        }
        catch (Exception ex)
        {
            Log.Warn($"Card watcher: sync failed: {ex.Message}");
        }
    }

    private sealed class Suspension(CardAcfWatcher owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Resume();
        }
    }
}
