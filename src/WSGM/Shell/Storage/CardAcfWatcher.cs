using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Event-based badge/tab freshness: watches <c>SteamLibrary\steamapps</c> on every ready drive
///     carrying a Steam library marker for <c>appmanifest_*.acf</c> create/delete/rename
///     and triggers a debounced full sync, so installing or removing a game on a card
///     updates its tab and in-page badge without the user opening the overlay.
///     Only file-NAME events are watched: an install creates its appmanifest immediately
///     and an uninstall deletes it, while download progress only rewrites file contents —
///     so a multi-gigabyte download does not re-sync every few seconds.
///     Mounts change (insert/eject/format), so the watcher set is reconciled from each settled
///     storage read of <see cref="RemovableDriveManager" />, the same one <see cref="CardVolumeMonitor" /> follows.
///     The watchers hold directory handles on the cards, which would make WSGM veto its
///     own Safe Eject (FSCTL_LOCK_VOLUME needs the volume otherwise unopened) — the eject
///     and format flows hold a <see cref="Suspend" /> scope for their whole run, and
///     reconciliation stays away until the last scope ends.
/// </summary>
internal sealed class CardAcfWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The session's storage owner, whose settled reads say which volumes are mounted.</summary>
    private readonly RemovableDriveManager _drives;

    private readonly object _gate = new();

    private readonly SteamUiReadiness _readiness;
    private readonly Timer _reconcile;
    private readonly SteamClient _steam;
    private readonly ConfigStore _store;
    private readonly CancellationToken _token;

    private readonly Dictionary<char, FileSystemWatcher> _watchers = new();

    /// <summary>
    ///     1 while a deferred boot sync is waiting or running. Its wait outlives many
    ///     debounce ticks, and overlapping syncs would drive concurrent CEF mutation.
    /// </summary>
    private int _bootSyncPending;

    private Timer? _debounce;
    private bool _disposed;
    private int _suspensions;

    private CardAcfWatcher(
        RemovableDriveManager drives, ConfigStore store, SteamClient steam, SteamUiReadiness readiness)
    {
        _drives = drives;
        _store = store;
        _steam = steam;
        _readiness = readiness;
        _token = _cts.Token;
        _reconcile = new Timer(_ => Reconcile(), null, Timeout.Infinite, Timeout.Infinite);
        drives.TopologyChanged += OnTopologyChanged;
    }

    /// <summary>Stops watching and unsubscribes from storage changes. UI thread.</summary>
    public void Dispose()
    {
        _drives.TopologyChanged -= OnTopologyChanged;
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
    /// <param name="drives">The session's storage owner, which outlives this watcher.</param>
    /// <param name="store">The configuration store the card libraries are read from.</param>
    /// <param name="steam">The session's Steam client the tab syncs run through.</param>
    /// <param name="readiness">The session's Steam UI readiness, which a sync waits on.</param>
    /// <returns>A running watcher owned by the caller; dispose before the borrowed storage owner and Steam services.</returns>
    internal static CardAcfWatcher StartNew(
        RemovableDriveManager drives, ConfigStore store, SteamClient steam, SteamUiReadiness readiness)
    {
        var watcher = new CardAcfWatcher(drives, store, steam, readiness);
        watcher._reconcile.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        return watcher;
    }

    /// <summary>
    ///     Drops every directory handle before a Safe Eject or a format and keeps the
    ///     reconciler from re-opening one until the returned scope is disposed. Cards that
    ///     remain mounted are re-watched when the last scope ends.
    /// </summary>
    /// <returns>An independent suspension scope. Dispose it after eject or format; watching resumes after the last scope ends.</returns>
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

    /// <summary>Runs on the storage owner's worker after a settled read changed the volumes or disks.</summary>
    private void OnTopologyChanged()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _reconcile.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Reconcile()
    {
        try
        {
            var mounted = new HashSet<char>();
            // Every ready disk volume, internal ones included: a library on any of them has a tab.
            foreach (var volume in _drives.Inventory.Volumes.Where(volume => volume.Ready))
            {
                try
                {
                    var root = Path.Combine(volume.MountPath, SteamLibraryVdf.CardFolderName);
                    if (File.Exists(Path.Combine(root, SteamLibraryMarker.MarkerFileName))
                        && Directory.Exists(Path.Combine(root, "steamapps")))
                    {
                        mounted.Add(char.ToUpperInvariant(volume.Letter));
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
            // Buffer overflow or the drive vanished; the next storage read's
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
