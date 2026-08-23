using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Event-based badge/tab freshness for card libraries: watches every mounted
/// card's <c>SteamLibrary\steamapps</c> for <c>appmanifest_*.acf</c> create/delete/rename
/// and triggers a debounced full sync, so installing or removing a game on a card
/// updates its tab and in-page badge without the user opening the overlay.
///
/// Only file-NAME events are watched: an install creates its appmanifest immediately
/// and an uninstall deletes it, while download progress only rewrites file contents —
/// so a multi-gigabyte download does not re-sync every few seconds.
///
/// Mounts change (insert/eject/format), so a cheap poll reconciles the watcher set.
/// The watchers hold directory handles on the cards, which would make WSGM veto its
/// own Safe Eject (FSCTL_LOCK_VOLUME needs the volume otherwise unopened) — the eject
/// flow calls <see cref="SuspendAll"/> first, and reconciliation stays away until the
/// suppression window passes.</summary>
internal sealed class CardAcfWatcher : IDisposable
{
    private static CardAcfWatcher? _active;

    private readonly Dictionary<char, FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private Timer? _reconcile;
    private Timer? _debounce;

    /// <summary>1 while a deferred boot sync is running. Its retry loop outlives many
    /// debounce ticks, and overlapping loops would drive concurrent CEF mutation.</summary>
    private int _bootSyncPending;
    private long _suppressedUntilTicks;
    private bool _disposed;

    private CardAcfWatcher()
    {
        _token = _cts.Token;
    }

    /// <summary>Creates, registers and starts the session's watcher.</summary>
    internal static CardAcfWatcher StartNew()
    {
        var watcher = new CardAcfWatcher();
        _active = watcher;
        watcher._reconcile = new Timer(_ => watcher.Reconcile(), null, 0, 5000);
        return watcher;
    }

    /// <summary>Drops every directory handle before a Safe Eject and keeps the
    /// reconciler from re-opening one for the next 20 seconds. Cards that remain
    /// mounted are re-watched automatically afterwards.</summary>
    internal static void SuspendAll()
    {
        var active = _active;
        if (active is null)
        {
            return;
        }
        lock (active._gate)
        {
            active._suppressedUntilTicks = DateTime.UtcNow.AddSeconds(20).Ticks;
            foreach (var watcher in active._watchers.Values)
            {
                watcher.Dispose();
            }
            if (active._watchers.Count > 0)
            {
                Log.Info("Card watcher: suspended for eject.");
            }
            active._watchers.Clear();
        }
    }

    private void Reconcile()
    {
        try
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressedUntilTicks))
            {
                return;
            }
            var mounted = new HashSet<char>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }
                    var root = Path.Combine(drive.Name, "SteamLibrary");
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
                if (_disposed)
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
            var watcher = new FileSystemWatcher(
                $"{letter}:\\SteamLibrary\\steamapps", "appmanifest_*.acf")
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, _) => Debounce();
            watcher.Deleted += (_, _) => Debounce();
            watcher.Renamed += (_, _) => Debounce();
            // Buffer overflow or the drive vanished; the next reconcile pass
            // disposes or replaces the watcher.
            watcher.Error += (_, _) => Debounce();
            _watchers[letter] = watcher;
            Log.Info($"Card watcher: watching {letter}:\\SteamLibrary\\steamapps.");
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
            _debounce?.Dispose();
            _debounce = new Timer(_ => _ = SyncAsync(), null, 2000, Timeout.Infinite);
        }
    }

    private async Task SyncAsync()
    {
        try
        {
            var manager = new LibraryTabManager();
            if (!SteamUiReadiness.IsReady)
            {
                // SyncOnBootAsync retries for up to ~2.5 minutes. A card finishing
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
                    await manager.SyncOnBootAsync(_token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _bootSyncPending, 0);
                }
                return;
            }
            var summary = await manager.SyncAllAsync(_token).ConfigureAwait(false);
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

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            _disposed = true;
            _reconcile?.Dispose();
            _reconcile = null;
            _debounce?.Dispose();
            _debounce = null;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }
        _cts.Dispose();
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
    }
}
